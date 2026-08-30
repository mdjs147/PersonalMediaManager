using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.Settings;
using PersonalMediaManager.Application.Services.Settings;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Infrastructure.Persistence.Services.Parse;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Settings;

/// <summary>通用设置服务实现（System_Setting 表 KV）</summary>
/// <remarks>
/// 列表按 Category 分组返回（前端按分组渲染表单）；
/// Update 走批量 upsert：Key 已存在则更新 Value，不存在则新增（Category 默认 "General"，避免误覆盖）。
/// 系统级保留 Key（System.SetupCompleted）禁止通过本端点更新。
/// </remarks>
internal sealed class GeneralSettingsService : IGeneralSettingsService
{
    private static readonly HashSet<string> ProtectedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.SetupCompleted",
        // 升级检查相关 7 个 key 走专用 UpdateSettings 页面与 IUpdateSettingService，禁止从「常规设置」列表更新：
        //   · GitHubPat 必须走加密通道（明文 KV 提交会绕过 IProtectedFieldService）；
        //   · Enabled / CheckIntervalHours 必须走专用服务的范围校验（CheckIntervalHours 钳位 [1,720]），裸 KV 提交会写入非法值。
        "Update_GitHubOwner",
        "Update_GitHubRepo",
        "Update_GitHubPat",
        "Update_LastCheckJson",
        "Update_SkippedVersion",
        "Update_Enabled",
        "Update_CheckIntervalHours",
        // Webhook 总开关走专用 Webhooks 页（GET/POST settings/webhooks/enabled），不从「常规设置」列表暴露 / 更新：
        //   与 86c50ae「Webhook 分组移出常规页」一致，避免总开关在两处可改造成歧义。
        "Webhook_Enabled",
        // 归档命名模板 4 个 key 走专用「归档命名」页（settings/archive-naming）+ NamingTemplateRenderer.ValidateTemplate 校验：
        //   裸 KV 提交会绕过模板校验（{tmdb 字面量 / 路径分隔符 / 未知占位符 / 单集缺 {episode}），写入非法模板。
        "Archive_MovieNameTemplate",
        "Archive_TvShowFolderTemplate",
        "Archive_TvEpisodeNameTemplate",
        "Archive_SeasonFolderIncludeTitle",
    };

    /// <summary>已知设置项元数据：填充 ValueType/Options 让前端渲染合适控件；DB 缺失时合并默认项让设置页可见可编辑</summary>
    /// <remarks>归档配置 key 须与 ArchiveService 常量一致（Archive_ConflictPolicy / Archive_MinFreeSpaceMB / File.CleanEmptyDir / File.CleanEmptyDirIgnoreExts）。</remarks>
    private static readonly IReadOnlyDictionary<string, SettingMeta> KnownSettings = new Dictionary<string, SettingMeta>(StringComparer.Ordinal)
    {
        // 空目录清理：总开关（bool，前端 el-switch）+ 可忽略扩展名清单（string，前端文本框）
        ["File.CleanEmptyDir"] = new(
            Category: "General",
            Description: "归档（移动）完成后回收源端空目录；配合下方「可忽略扩展名」可把仅剩残留文件的目录也视为空一并删除",
            ValueType: "bool",
            Options: null,
            Default: "false"),
        ["File.CleanEmptyDirIgnoreExts"] = new(
            Category: "General",
            Description: "清理空目录时视为「可忽略残留」的扩展名清单（逗号分隔，如 .torrent,.nfo）；目录仅剩这些文件 + 子目录递归为空也算空，连残留一并删除；留空 = 仅清真正的空目录",
            ValueType: "string",
            Options: null,
            Default: ".torrent"),
        ["Notification_ReviewRequiredEnabled"] = new(
            Category: "General",
            Description: "待确认文件提醒：扫描处理产生需要人工确认的文件时，在网页右下角显示提醒（点击提醒可前往待确认队列）",
            ValueType: "bool",
            Options: null,
            Default: "true"),
        ["Archive_ConflictPolicy"] = new(
            Category: "Archive",
            Description: "同名冲突处理策略：Skip 跳过 / Overwrite 升级替换（新文件更大才替换）/ KeepBoth 保留多版本 / Ask 询问（放入待确认队列，人工裁定是否覆盖）",
            ValueType: "enum",
            Options: new[] { "Skip", "Overwrite", "KeepBoth", "Ask" },
            Default: "Skip"),
        ["Archive_HoldBeforeArchive"] = new(
            Category: "Archive",
            Description: "归档前拦截：开启后即便自动匹配高置信命中，也先把记录放入「人工确认」队列，待你核对去向后点确认再归档——从源头拦下自动跑错（默认关闭，保持全自动）",
            ValueType: "bool",
            Options: null,
            Default: "false"),
        ["Archive_MinFreeSpaceMB"] = new(
            Category: "Archive",
            Description: "归档前目标盘最小可用空间余量（MB，0 = 仅需 ≥ 源文件大小；范围 0~1048576）",
            ValueType: "int",
            Options: null,
            Default: "0"),
        ["Archive_DiskWarnPercent"] = new(
            Category: "Archive",
            Description: "归档盘剩余空间低于此百分比 → 健康检查 warn + 周期巡检（5 分钟）发 disk.low 通知（0=不检查；数值应不小于严重阈值）",
            ValueType: "int",
            Options: null,
            Default: "10"),
        ["Archive_DiskCriticalPercent"] = new(
            Category: "Archive",
            Description: "归档盘剩余空间低于此百分比 → 健康检查 fail + 周期巡检（5 分钟）发 disk.low 严重通知（0=不检查；数值应不大于警告阈值）",
            ValueType: "int",
            Options: null,
            Default: "5"),
        ["System_AlertSuppressMinutes"] = new(
            Category: "Alert",
            Description: "同类告警抑制窗口（分钟，窗口内同一告警只发一次，防风暴）",
            ValueType: "int",
            Options: null,
            Default: "60"),
        // 「TMDB 未收录」自动重试（key 须与 TmdbZeroResultRetrySweeper 常量一致）
        ["Parse_ZeroResultAutoRetry"] = new(
            Category: "Parse",
            Description: "TMDB 未收录自动重试：规则高置信但 TMDB 多查询全零结果的记录（多为新番/新剧发布初期未收录），每天自动重新识别一次，TMDB 收录后自动归档（关闭 = 仅人工处理）",
            ValueType: "bool",
            Options: null,
            Default: "true"),
        ["Parse_ZeroResultRetryWindowDays"] = new(
            Category: "Parse",
            Description: "TMDB 未收录自动重试窗口（天，范围 1~90）：入库超过该天数仍未收录则停止自动重试、停留人工确认队列",
            ValueType: "int",
            Options: null,
            Default: "14"),
        // 音频不兼容轨处理（av3a Audio Vivid 等；key 须与 ProcessFileService.Audio*Key / SystemSettingConfig 种子一致）
        ["Audio_IncompatibleCheckEnabled"] = new(
            Category: "Audio",
            Description: "音频不兼容轨检查总开关：开则归档前用 ffprobe 探测 av3a 等不兼容音轨并在历史打标（需配置下方 ffmpeg 路径）",
            ValueType: "bool",
            Options: null,
            Default: "false"),
        ["Audio_FfmpegPath"] = new(
            Category: "Audio",
            Description: "ffmpeg / ffprobe 所在目录或 ffmpeg 可执行文件路径（需自行安装 ffmpeg；留空 = 不探测不重混，功能不启用）",
            ValueType: "string",
            Options: null,
            Default: ""),
        ["Audio_IncompatibleCodecs"] = new(
            Category: "Audio",
            Description: "视为不兼容的音频编解码器清单（逗号分隔，小写；默认 av3a 菁彩声；留空 = 不检查任何编码）",
            ValueType: "string",
            Options: null,
            Default: "av3a"),
        ["Audio_AutoRemux"] = new(
            Category: "Audio",
            Description: "自动重混丢不兼容音轨：开且存在其它兼容音轨时归档用 ffmpeg 流复制去掉 av3a 轨（不重编码）；关 = 仅标记由你手动处理",
            ValueType: "bool",
            Options: null,
            Default: "false"),
    };

    private sealed record SettingMeta(string Category, string Description, string ValueType, IReadOnlyList<string>? Options, string Default);

    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly IProxyResolver _proxyResolver;
    private readonly IFfmpegToolTester _ffmpegTester;

    public GeneralSettingsService(
        IDbContextFactory<PmmDbContext> dbFactory,
        IProxyResolver proxyResolver,
        IFfmpegToolTester ffmpegTester)
    {
        _dbFactory = dbFactory;
        _proxyResolver = proxyResolver;
        _ffmpegTester = ffmpegTester;
    }

    public async Task<GroupedSettingsResponse> ListAsync(CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        List<SystemSetting> rows = await ctx.SystemSettings.AsNoTracking()
            .Where(s => !ProtectedKeys.Contains(s.Key))
            .ToListAsync(ct);

        // DB 已有项填元数据；再合并 DB 缺失的已知配置默认项（如归档策略），让设置页总能看到并编辑
        Dictionary<string, GeneralSettingItem> merged = new(StringComparer.Ordinal);
        foreach (SystemSetting s in rows)
            merged[s.Key] = ToItem(s.Key, s.Value, s.Category, s.Description);
        foreach ((string key, SettingMeta meta) in KnownSettings)
        {
            if (!merged.ContainsKey(key))
                merged[key] = new GeneralSettingItem(key, meta.Default, meta.Category, meta.Description, meta.ValueType, meta.Options);
        }

        Dictionary<string, IReadOnlyList<GeneralSettingItem>> groups = merged.Values
            .GroupBy(i => i.Category)
            .OrderBy(g => g.Key)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<GeneralSettingItem>)g.OrderBy(i => i.Key).ToList());

        return new GroupedSettingsResponse(groups);
    }

    /// <summary>把 DB 行投影为响应项；已知 key 补 ValueType/Options，描述以代码元数据为准</summary>
    /// <remarks>
    /// 已知 key 的描述改用本类 KnownSettings（而非 DB 行）：Description 无任何编辑入口（UpdateAsync 只写 Value），
    /// DB 里的值只能来自迁移种子、会随版本演进过时（如 disk.low 通知行为变化后种子文案失真），
    /// 而种子描述按红线不可改动——以代码元数据为单一事实源保证「描述=真实行为」。未知 key 仍用 DB 描述。
    /// </remarks>
    private static GeneralSettingItem ToItem(string key, string? value, string category, string? description)
    {
        if (KnownSettings.TryGetValue(key, out SettingMeta? meta))
            return new GeneralSettingItem(key, value, meta.Category, meta.Description, meta.ValueType, meta.Options);
        return new GeneralSettingItem(key, value, category, description);
    }

    public async Task UpdateAsync(UpdateGeneralRequest req, CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        bool touchedProxy = false;

        foreach (UpdateGeneralItem item in req.Items)
        {
            if (ProtectedKeys.Contains(item.Key))
                throw new BusinessException($"配置项 {item.Key} 受保护，不能修改");

            if (item.Key.StartsWith("Proxy_", StringComparison.Ordinal))
                touchedProxy = true;

            SystemSetting? existing = await ctx.SystemSettings.FirstOrDefaultAsync(s => s.Key == item.Key, ct);
            if (existing is not null)
            {
                existing.Value = item.Value;
            }
            else
            {
                ctx.SystemSettings.Add(new SystemSetting
                {
                    Key = item.Key,
                    Value = item.Value,
                    Category = "General",
                });
            }
        }

        await ctx.SaveChangesAsync(ct);

        // 代理设置改动后让 IProxyResolver 缓存立即失效，下次请求秒级生效
        if (touchedProxy) _proxyResolver.Invalidate();
    }

    /// <summary>自检 ffmpeg / ffprobe：解析配置路径 → 各跑 -version → 汇总可用性</summary>
    /// <remarks>
    /// PathOverride 非空测「当前输入框未保存的值」，否则读 DB 已存的 Audio_FfmpegPath；
    /// 路径解析复用 <see cref="AudioProbeSupport.ResolveFfmpegTools"/>（与归档探测 / 重混同源，避免逻辑漂移）；
    /// 真正跑 -version 委托 Platform 的 <see cref="IFfmpegToolTester"/>（本层不启进程，守 Persistence 不直接执行外部进程的分工）。
    /// </remarks>
    public async Task<TestFfmpegResponse> TestFfmpegAsync(TestFfmpegRequest req, CancellationToken ct = default)
    {
        string? configured = req.PathOverride;
        if (string.IsNullOrWhiteSpace(configured))
        {
            await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
            configured = (await ctx.SystemSettings.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Key == AudioProbeSupport.FfmpegPathKey, ct))?.Value;
        }
        configured = configured?.Trim();

        if (string.IsNullOrWhiteSpace(configured))
        {
            FfmpegToolStatus empty = new(false, null, null, "未配置路径");
            return new TestFfmpegResponse(false, empty, empty,
                "尚未配置 ffmpeg 路径，请先填写 ffmpeg 安装目录或 ffmpeg/ffprobe 可执行文件路径");
        }

        (string? ffprobePath, string? ffmpegPath) = AudioProbeSupport.ResolveFfmpegTools(configured);
        // 解析不到 exe 的两种成因区分：路径整体不存在 vs 目录在但缺 exe，给用户精准提示
        bool pathExists = Directory.Exists(configured) || File.Exists(configured);
        string notFound = pathExists ? "未在该路径下找到可执行文件" : $"路径不存在：{configured}";

        FfmpegToolStatus ffprobe = await ProbeToolAsync(ffprobePath, notFound, ct);
        FfmpegToolStatus ffmpeg = await ProbeToolAsync(ffmpegPath, notFound, ct);

        bool success = ffprobe.Runnable && ffmpeg.Runnable;
        return new TestFfmpegResponse(success, ffprobe, ffmpeg, BuildFfmpegMessage(success, ffprobe, ffmpeg));
    }

    /// <summary>exe 路径非空则跑 -version，否则直接判未找到</summary>
    private async Task<FfmpegToolStatus> ProbeToolAsync(string? exePath, string notFoundReason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            return new FfmpegToolStatus(false, null, null, notFoundReason);
        FfmpegToolProbe probe = await _ffmpegTester.ProbeAsync(exePath, ct);
        return new FfmpegToolStatus(probe.Runnable, exePath, probe.Version, probe.Error);
    }

    /// <summary>拼可直接展示的中文结论（成功含版本号；失败逐工具列原因）</summary>
    private static string BuildFfmpegMessage(bool success, FfmpegToolStatus ffprobe, FfmpegToolStatus ffmpeg)
    {
        if (success)
            return $"ffmpeg 与 ffprobe 均可用：{ffmpeg.Version ?? ffprobe.Version}";
        List<string> parts = new();
        if (!ffprobe.Runnable) parts.Add($"ffprobe {ffprobe.Error}");
        if (!ffmpeg.Runnable) parts.Add($"ffmpeg {ffmpeg.Error}");
        return "ffmpeg 自检未通过：" + string.Join("；", parts);
    }
}
