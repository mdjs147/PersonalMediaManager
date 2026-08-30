using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.System;
using PersonalMediaManager.Application.Services.System;
using PersonalMediaManager.Domain.Entities;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Update;

/// <summary>IUpdateSettingService 实现（System_Setting KV 编排 + PAT 加密 + IUpdateChecker 调用）</summary>
/// <remarks>
/// 7 个 System_Setting key：
///   - Update_GitHubOwner  / Update_GitHubRepo   明文
///   - Update_GitHubPat                          IProtectedFieldService 加密
///   - Update_Enabled                            "true" / "false"，自动检查启用开关
///   - Update_CheckIntervalHours                 自动检查周期（小时，[1, 720]）
///   - Update_LastCheckJson                      RunCheck 后写入快照
///   - Update_SkippedVersion                     用户跳过的版本号
///
/// 任一 key 缺失时按「DataSeeder 已通过 Migration HasData 注入」前置假设：
///   缺失即数据库未升级，按 GeneralSettingsService 同款兜底 upsert 创建（保留旧 db 升级期）。
/// appsettings.json Update:Enabled / Update:CheckIntervalHours 仅作为新 key 兜底创建时的默认值；
/// 运行时一律以 db 为准（worker 与本服务都从 db 读）。
/// </remarks>
internal sealed class UpdateSettingService : IUpdateSettingService
{
    /// <summary>7 个 key 字面量集中常量；与 SystemSettingConfig HasData / DataSeeder 必须保持完全一致</summary>
    public const string KeyOwner = "Update_GitHubOwner";
    public const string KeyRepo = "Update_GitHubRepo";
    public const string KeyPat = "Update_GitHubPat";
    public const string KeyEnabled = "Update_Enabled";
    public const string KeyInterval = "Update_CheckIntervalHours";
    public const string KeyLastCheck = "Update_LastCheckJson";
    public const string KeySkipped = "Update_SkippedVersion";

    /// <summary>CheckIntervalHours 合法区间（与 UpdateSettingsRequest 注释保持一致）</summary>
    public const int IntervalHoursMin = 1;
    public const int IntervalHoursMax = 720;

    /// <summary>System_Setting.Category 统一为 "Update"，前端按分组渲染时与「常规设置」隔离</summary>
    public const string CategoryName = "Update";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly IProtectedFieldService _protector;
    private readonly IUpdateChecker _checker;
    private readonly IVersionInfoProvider _versionInfo;
    private readonly IOptions<UpdateOptions> _options;
    private readonly IClock _clock;
    private readonly ILogger<UpdateSettingService> _logger;

    public UpdateSettingService(
        IDbContextFactory<PmmDbContext> dbFactory,
        IProtectedFieldService protector,
        IUpdateChecker checker,
        IVersionInfoProvider versionInfo,
        IOptions<UpdateOptions> options,
        IClock clock,
        ILogger<UpdateSettingService> logger)
    {
        _dbFactory = dbFactory;
        _protector = protector;
        _checker = checker;
        _versionInfo = versionInfo;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    public async Task<UpdateCheckSettingsResponse> GetAsync(CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        Dictionary<string, SystemSetting> rows = await LoadUpdateRowsAsync(ctx, ct);
        return ToResponse(rows);
    }

    public async Task<UpdateCheckSettingsResponse> UpdateAsync(UpdateSettingsRequest req, CancellationToken ct = default)
    {
        // owner/repo 非空、CheckIntervalHours 区间已迁到 UpdateSettingsRequest 注解（绑定阶段校验）；
        // 此处仅保留「私有仓库 PAT」跨字段+读 DB 既有值的条件式校验。
        if (req.IsPrivateRepo && string.IsNullOrEmpty(req.GitHubPat))
        {
            // PUT 时 null=不变；调用方明确选了 Private 仓但 PAT 留空 + 既有 PAT 也为空 → 必填校验
            await using PmmDbContext probe = await _dbFactory.CreateDbContextAsync(ct);
            string? existing = (await probe.SystemSettings.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Key == KeyPat, ct))?.Value;
            if (req.GitHubPat is null && string.IsNullOrEmpty(existing))
            {
                throw new BusinessException("私有仓库必须配置 PAT");
            }
            if (req.GitHubPat is { Length: 0 } && string.IsNullOrEmpty(existing) == false)
            {
                // ""=清空时若仍勾选 Private，等价于"清空后又要求私有"
                throw new BusinessException("私有仓库下不能清空 PAT");
            }
        }

        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        Dictionary<string, SystemSetting> rows = await LoadUpdateRowsAsync(ctx, ct);

        rows[KeyOwner].Value = req.GitHubOwner.Trim();
        rows[KeyRepo].Value = req.GitHubRepo.Trim();

        // PAT 三态：null=不变 / ""=清空 / 非空=Protect
        if (req.GitHubPat is not null)
        {
            rows[KeyPat].Value = req.GitHubPat.Length == 0
                ? null
                : _protector.Protect(req.GitHubPat);
        }

        // Enabled / CheckIntervalHours：null=不变；非空=覆写
        if (req.Enabled is { } enabled)
        {
            rows[KeyEnabled].Value = enabled ? "true" : "false";
        }
        if (req.CheckIntervalHours is { } interval)
        {
            rows[KeyInterval].Value = interval.ToString();
        }

        await ctx.SaveChangesAsync(ct);
        return ToResponse(rows);
    }

    public async Task<RunUpdateCheckResponse> RunCheckAsync(CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        Dictionary<string, SystemSetting> rows = await LoadUpdateRowsAsync(ctx, ct);

        if (!ParseEnabled(rows[KeyEnabled].Value))
        {
            DateTimeOffset now = _clock.UtcNow;
            return new RunUpdateCheckResponse(
                CheckedAt: now,
                Success: false,
                HasNewVersion: false,
                CurrentVersion: _versionInfo.GetStatic().Product,
                LatestVersion: null,
                LatestUrl: null,
                HttpStatus: null,
                ErrorCategory: "disabled",
                ErrorMessage: "自动检查已关闭（设置 → 软件更新 → 自动检查）");
        }

        string owner = rows[KeyOwner].Value ?? string.Empty;
        string repo = rows[KeyRepo].Value ?? string.Empty;
        string? patPlain = null;
        string? patStored = rows[KeyPat].Value;
        if (!string.IsNullOrEmpty(patStored))
        {
            try
            {
                patPlain = _protector.Unprotect(patStored);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Update_GitHubPat 解密失败，本次按匿名访问");
                patPlain = null;
            }
        }

        // 取上次 ETag 复用（HasNewVersion 不能仅靠 304 推断，仍需 LatestVersion 上次缓存）
        LastUpdateCheckSnapshot? prev = TryParseSnapshot(rows[KeyLastCheck].Value);
        string? prevEtag = prev?.Etag;
        string? prevLatestVersion = prev?.LatestVersion;
        string? prevLatestUrl = prev?.LatestUrl;
        string? prevLatestTag = prev?.LatestTagName;
        string? prevLatestName = prev?.LatestName;
        DateTimeOffset? prevPublishedAt = prev?.PublishedAt;

        string currentVersion = _versionInfo.GetStatic().Product;
        UpdateCheckOutcome outcome = await _checker.CheckAsync(owner, repo, patPlain, currentVersion, prevEtag, ct);

        LastUpdateCheckSnapshot snapshot;
        if (outcome.NotModified)
        {
            // 304：HasNewVersion 沿用上次结果（如果上次有新版本，等于「等用户操作」期间仓库无新 release）
            bool hasNew = prevLatestVersion is not null
                && SemVerComparer.IsNewer(prevLatestVersion, currentVersion);
            snapshot = new LastUpdateCheckSnapshot(
                CheckedAt: outcome.CheckedAt,
                Success: true,
                HasNewVersion: hasNew,
                CurrentVersion: currentVersion,
                LatestVersion: prevLatestVersion,
                LatestTagName: prevLatestTag,
                LatestName: prevLatestName,
                LatestUrl: prevLatestUrl,
                PublishedAt: prevPublishedAt,
                Etag: outcome.Etag,
                HttpStatus: outcome.HttpStatus,
                ErrorCategory: null,
                ErrorMessage: null);
        }
        else if (outcome.Success && outcome.Latest is { } latest)
        {
            snapshot = new LastUpdateCheckSnapshot(
                CheckedAt: outcome.CheckedAt,
                Success: true,
                HasNewVersion: outcome.HasNewVersion,
                CurrentVersion: currentVersion,
                LatestVersion: latest.Version,
                LatestTagName: latest.TagName,
                LatestName: latest.Name,
                LatestUrl: latest.HtmlUrl,
                PublishedAt: latest.PublishedAt,
                Etag: outcome.Etag,
                HttpStatus: outcome.HttpStatus,
                ErrorCategory: null,
                ErrorMessage: null);
        }
        else
        {
            // 失败：保留上次成功的 LatestVersion / LatestUrl 作为前端展示兜底，避免 UI 闪烁回退
            snapshot = new LastUpdateCheckSnapshot(
                CheckedAt: outcome.CheckedAt,
                Success: false,
                HasNewVersion: false,
                CurrentVersion: currentVersion,
                LatestVersion: prevLatestVersion,
                LatestTagName: prevLatestTag,
                LatestName: prevLatestName,
                LatestUrl: prevLatestUrl,
                PublishedAt: prevPublishedAt,
                Etag: prev?.Etag,
                HttpStatus: outcome.HttpStatus,
                ErrorCategory: outcome.ErrorCategory,
                ErrorMessage: outcome.ErrorMessage);
        }

        rows[KeyLastCheck].Value = JsonSerializer.Serialize(snapshot, JsonOptions);
        await ctx.SaveChangesAsync(ct);

        return new RunUpdateCheckResponse(
            CheckedAt: snapshot.CheckedAt,
            Success: snapshot.Success,
            HasNewVersion: snapshot.HasNewVersion,
            CurrentVersion: snapshot.CurrentVersion,
            LatestVersion: snapshot.LatestVersion,
            LatestUrl: snapshot.LatestUrl,
            HttpStatus: snapshot.HttpStatus,
            ErrorCategory: snapshot.ErrorCategory,
            ErrorMessage: snapshot.ErrorMessage);
    }

    public async Task<TestUpdateCheckResponse> TestAsync(TestUpdateCheckRequest req, CancellationToken ct = default)
    {
        // owner/repo 非空已迁到 TestUpdateCheckRequest 注解（绑定阶段校验）。
        string currentVersion = _versionInfo.GetStatic().Product;
        UpdateCheckOutcome outcome = await _checker.CheckAsync(
            req.GitHubOwner.Trim(),
            req.GitHubRepo.Trim(),
            string.IsNullOrEmpty(req.GitHubPat) ? null : req.GitHubPat,
            currentVersion,
            etag: null, // /test 强制不带 If-None-Match，避免 304 让"连接 OK 但无 Latest"歧义
            ct);

        return new TestUpdateCheckResponse(
            Success: outcome.Success,
            HasNewVersion: outcome.HasNewVersion,
            CurrentVersion: currentVersion,
            LatestVersion: outcome.Latest?.Version,
            HttpStatus: outcome.HttpStatus,
            ElapsedMilliseconds: outcome.ElapsedMilliseconds,
            ErrorCategory: outcome.ErrorCategory,
            ErrorMessage: outcome.ErrorMessage);
    }

    public async Task SkipVersionAsync(string version, CancellationToken ct = default)
    {
        // version 非空已迁到 SkipUpdateVersionRequest.Version 注解（Controller 绑定阶段校验后传入）。
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        Dictionary<string, SystemSetting> rows = await LoadUpdateRowsAsync(ctx, ct);
        rows[KeySkipped].Value = version.Trim();
        await ctx.SaveChangesAsync(ct);
    }

    // ============ 私有 ============

    /// <summary>批量加载 7 个 key（缺失时按默认值 upsert，保护 db 升级期）</summary>
    private async Task<Dictionary<string, SystemSetting>> LoadUpdateRowsAsync(PmmDbContext ctx, CancellationToken ct)
    {
        string[] keys = [KeyOwner, KeyRepo, KeyPat, KeyEnabled, KeyInterval, KeyLastCheck, KeySkipped];
        Dictionary<string, SystemSetting> dict = await ctx.SystemSettings
            .Where(s => keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, ct);

        // appsettings 仅作为新装库首次 seed 的默认值；已有库以 db 为准
        UpdateOptions opts = _options.Value;
        string defaultEnabled = opts.Enabled ? "true" : "false";
        string defaultInterval = Math.Clamp(opts.CheckIntervalHours, IntervalHoursMin, IntervalHoursMax)
            .ToString();

        EnsureRow(ctx, dict, KeyOwner, "mdjs147", "GitHub 仓库所有者（升级检查数据源）");
        EnsureRow(ctx, dict, KeyRepo, "PersonalMediaManager", "GitHub 仓库名（升级检查数据源）");
        EnsureRow(ctx, dict, KeyPat, value: null, "GitHub PAT（加密存储；空 = 匿名 60 req/h，配置 = 5000 req/h）");
        EnsureRow(ctx, dict, KeyEnabled, defaultEnabled, "自动检查启用开关（true / false）");
        EnsureRow(ctx, dict, KeyInterval, defaultInterval, "自动检查周期（小时，[1, 720]）");
        EnsureRow(ctx, dict, KeyLastCheck, value: null, "上次升级检查结果 JSON 快照（含 etag/error/latest）");
        EnsureRow(ctx, dict, KeySkipped, value: null, "用户跳过的版本号（命中后不再气泡提示）");

        // 兜底新增的行也会被 SaveChanges 写入；调用方在后续设置后一并 SaveChanges
        return dict;
    }

    private static void EnsureRow(PmmDbContext ctx, Dictionary<string, SystemSetting> dict, string key, string? value, string description)
    {
        if (dict.ContainsKey(key)) return;
        SystemSetting row = new()
        {
            Key = key,
            Value = value,
            Category = CategoryName,
            Description = description,
        };
        ctx.SystemSettings.Add(row);
        dict[key] = row;
    }

    private UpdateCheckSettingsResponse ToResponse(Dictionary<string, SystemSetting> rows)
    {
        return new UpdateCheckSettingsResponse(
            GitHubOwner: rows[KeyOwner].Value ?? string.Empty,
            GitHubRepo: rows[KeyRepo].Value ?? string.Empty,
            HasPat: !string.IsNullOrEmpty(rows[KeyPat].Value),
            Enabled: ParseEnabled(rows[KeyEnabled].Value),
            CheckIntervalHours: ParseInterval(rows[KeyInterval].Value),
            SkippedVersion: rows[KeySkipped].Value,
            LastCheck: TryParseSnapshot(rows[KeyLastCheck].Value));
    }

    /// <summary>解析 Update_Enabled（默认 true：seed 缺失或解析失败时不至于让用户「打不开自动检查」）</summary>
    internal static bool ParseEnabled(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return true;
        return string.Equals(raw.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>解析 Update_CheckIntervalHours 并钳位 [1, 720]</summary>
    internal static int ParseInterval(string? raw)
    {
        if (int.TryParse(raw, out int h))
        {
            return Math.Clamp(h, IntervalHoursMin, IntervalHoursMax);
        }
        return 24;
    }

    private LastUpdateCheckSnapshot? TryParseSnapshot(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<LastUpdateCheckSnapshot>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update_LastCheckJson 反序列化失败，按 null 处理（下次检查会覆盖）");
            return null;
        }
    }
}

