using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Common.Archiving;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.Subtitles;
using PersonalMediaManager.Application.Services.Subtitles;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Subtitles;

/// <summary>字幕下载服务实现（搜索 → 文件清单 → 下载落盘 → 记录查询）</summary>
/// <remarks>
/// 守门：搜索 / 清单 / 下载要求媒体已归档完成（Completed + TargetPath 文件仍在）且已配置 Assrt Token；
/// 记录查询（ListDownloads）仅要求媒体存在 —— 历史回看不应被「当前是否仍归档完成」卡住。
///
/// SSRF / 路径穿越防护：
/// - 下载绝不信任前端传 url，按 SubtitleId + FileIndex 服务端重取直链（响应 DTO 本就不暴露 Url）；
/// - 落地文件名完全自构（视频基名 + 语言码 + 白名单扩展名），绝不把外部文件名拼进路径，仅取其扩展名且先过
///   SubtitleRenamer.SupportedExtensions 白名单。
///
/// 失败语义：SubtitleProviderException（外部源失败）统一转 BusinessException ——
/// ExceptionHandlerMiddleware 只认 BusinessException / DomainException 走 1000，其余一律 9000 且吞掉原 message；
/// 外部源失败属业务可见错误，转译后保留中文消息返前端。
/// </remarks>
internal sealed class SubtitleDownloadService : ISubtitleDownloadService
{
    private const long SettingSingletonId = 1;

    /// <summary>同语言序号避让上限（.2.{lang} … .9.{lang}）</summary>
    private const int MaxOrdinal = 9;

    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly IProtectedFieldService _protector;
    private readonly ISubtitleProviderRegistry _registry;
    private readonly ILogger<SubtitleDownloadService> _logger;

    public SubtitleDownloadService(
        IDbContextFactory<PmmDbContext> dbFactory,
        IProtectedFieldService protector,
        ISubtitleProviderRegistry registry,
        ILogger<SubtitleDownloadService> logger)
    {
        _dbFactory = dbFactory;
        _protector = protector;
        _registry = registry;
        _logger = logger;
    }

    public async Task<SubtitleSearchResponse> SearchAsync(long mediaItemId, string? keyword, CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        MediaItem media = await LoadMediaOrThrowAsync(ctx, mediaItemId, ct);
        SubtitleProviderEndpoint endpoint = await LoadEndpointOrThrowAsync(ctx, ct);

        // keyword 空白时缺省用原始文件名去扩展名（最贴近源站命名的检索词）
        string effectiveKeyword = string.IsNullOrWhiteSpace(keyword)
            ? Path.GetFileNameWithoutExtension(media.FileName)
            : keyword.Trim();

        IReadOnlyList<SubtitleSearchEntry> entries = await InvokeProviderAsync(
            p => p.SearchAsync(effectiveKeyword, endpoint, ct));

        List<SubtitleSearchItem> items = entries
            .Select(e => new SubtitleSearchItem(
                e.SubtitleId, e.Name, e.VideoName, e.LanguageHint, e.Format, e.UploadTime, e.VoteScore))
            .ToList();
        return new SubtitleSearchResponse(effectiveKeyword, items);
    }

    public async Task<SubtitleFilesResponse> GetFilesAsync(long mediaItemId, string subtitleId, CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        await LoadMediaOrThrowAsync(ctx, mediaItemId, ct);
        SubtitleProviderEndpoint endpoint = await LoadEndpointOrThrowAsync(ctx, ct);

        SubtitleDetailEntry detail = await InvokeProviderAsync(p => p.GetFilesAsync(subtitleId, endpoint, ct));

        // 刻意丢弃 Url（防 SSRF）：前端仅拿 Index + 文件名 + 大小，下载时服务端按 SubtitleId + Index 重取直链
        List<SubtitleFileItem> items = detail.Files
            .Select(f => new SubtitleFileItem(f.Index, f.FileName, f.SizeBytes))
            .ToList();
        return new SubtitleFilesResponse(detail.SubtitleId, items);
    }

    public async Task<SubtitleDownloadResponse> DownloadAsync(long mediaItemId, DownloadSubtitleRequest request, CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        MediaItem media = await LoadMediaOrThrowAsync(ctx, mediaItemId, ct);
        SubtitleProviderEndpoint endpoint = await LoadEndpointOrThrowAsync(ctx, ct);

        // 服务端按 SubtitleId 重取文件清单（不信任前端传 url），再按 FileIndex 精确匹配
        SubtitleDetailEntry detail = await InvokeProviderAsync(p => p.GetFilesAsync(request.SubtitleId, endpoint, ct));
        SubtitleFileEntry? file = detail.Files.FirstOrDefault(f => f.Index == request.FileIndex);
        if (file is null)
            throw new BusinessException("所选字幕文件不存在，请重新选择");

        // 扩展名白名单：外部文件名只取扩展名（落地名完全自构，外部名绝不拼进路径，防穿越）
        string ext = Path.GetExtension(file.FileName);
        if (string.IsNullOrEmpty(ext) || !SubtitleRenamer.SupportedExtensions.Contains(ext))
            throw new BusinessException($"不支持的字幕格式：{(string.IsNullOrEmpty(ext) ? "（无扩展名）" : ext)}");

        byte[] bytes = await InvokeProviderAsync(p => p.DownloadAsync(file.Url, endpoint, ct));

        // 语言识别：文件名子串优先（DetectLanguage 内部自行去扩展名，整名传入保住「xxx.chs.srt」点分语言段）；
        // 识别不出（und）回退源站语言描述（LanguageHint）映射
        string language = SubtitleRenamer.DetectLanguage(file.FileName);
        if (language == "und")
            language = MapLanguageHint(detail.LanguageHint);

        (string savedFileName, string savedPath) = ResolveTargetPath(media.TargetPath!, language, ext);

        await File.WriteAllBytesAsync(savedPath, bytes, ct);

        SubtitleDownload record = new()
        {
            MediaItemId = media.Id,
            Provider = SubtitleProviderType.Assrt,
            SubtitleName = request.SubtitleName,
            FileName = savedFileName,
            TargetPath = savedPath,
            Language = language,
            FileSize = bytes.LongLength,
        };
        ctx.SubtitleDownloads.Add(record);
        await ctx.SaveChangesAsync(ct);

        _logger.LogInformation("字幕下载完成：MediaItemId={MediaItemId} → {SavedPath}（语言 {Language}，{Size} 字节）",
            media.Id, savedPath, language, bytes.LongLength);
        return new SubtitleDownloadResponse(savedFileName, savedPath, language);
    }

    public async Task<SubtitleDownloadListResponse> ListDownloadsAsync(long mediaItemId, CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        // 仅校验媒体存在：下载记录属历史回看，撤销归档 / 退回重改后仍应可查
        bool exists = await ctx.MediaItems.AsNoTracking().AnyAsync(m => m.Id == mediaItemId, ct);
        if (!exists)
            throw new BusinessException("媒体记录不存在");

        List<SubtitleDownload> rows = await ctx.SubtitleDownloads.AsNoTracking()
            .Where(d => d.MediaItemId == mediaItemId)
            .OrderByDescending(d => d.CreatedAt)
            .ThenByDescending(d => d.Id)
            .ToListAsync(ct);

        List<SubtitleDownloadItem> items = rows
            .Select(d => new SubtitleDownloadItem(
                d.Id, d.Provider.ToString(), d.SubtitleName, d.FileName, d.Language, d.FileSize, d.CreatedAt))
            .ToList();
        return new SubtitleDownloadListResponse(items);
    }

    // ---------- 守门 ----------

    /// <summary>读媒体记录并校验「已归档完成 + 归档文件仍在」（字幕落点 = 归档文件旁）</summary>
    private static async Task<MediaItem> LoadMediaOrThrowAsync(PmmDbContext ctx, long mediaItemId, CancellationToken ct)
    {
        MediaItem? media = await ctx.MediaItems.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mediaItemId, ct);
        if (media is null)
            throw new BusinessException("媒体记录不存在");
        if (media.Status != MediaItemStatus.Completed || string.IsNullOrEmpty(media.TargetPath))
            throw new BusinessException("仅已归档完成的记录可下载字幕");
        if (!File.Exists(media.TargetPath))
            throw new BusinessException("归档目标文件不存在，无法定位字幕落点");
        return media;
    }

    /// <summary>读字幕设置单例并解密 Token，封 SubtitleProviderEndpoint（未配 Token 抛业务异常）</summary>
    private async Task<SubtitleProviderEndpoint> LoadEndpointOrThrowAsync(PmmDbContext ctx, CancellationToken ct)
    {
        SubtitleSetting? setting = await ctx.SubtitleSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == SettingSingletonId, ct);
        if (setting is null || string.IsNullOrEmpty(setting.AssrtTokenEncrypted))
            throw new BusinessException("尚未配置 Assrt Token，请前往 设置→字幕 配置");

        string token = _protector.Unprotect(setting.AssrtTokenEncrypted);
        return new SubtitleProviderEndpoint(token, setting.AssrtBaseUrl, setting.TimeoutSeconds, setting.UseProxy);
    }

    // ---------- 私有工具 ----------

    /// <summary>调用 Assrt 字幕源策略，SubtitleProviderException 转译为 BusinessException（保留中文消息走 1000）</summary>
    private async Task<T> InvokeProviderAsync<T>(Func<ISubtitleProvider, Task<T>> action)
    {
        try
        {
            return await action(_registry.Resolve(SubtitleProviderType.Assrt));
        }
        catch (SubtitleProviderException ex)
        {
            // 外部源失败属业务可见错误；不转译则 ExceptionHandlerMiddleware 按未预期异常归 9000 并吞掉原因
            throw new BusinessException(ex.Message, ex);
        }
    }

    /// <summary>源站语言描述（如「简体」「繁体&amp;简体」「双语」）回退映射为语言码（文件名识别不出时兜底）</summary>
    private static string MapLanguageHint(string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint)) return "und";
        // 繁体优先判定（与 SubtitleRenamer 语言 token 表「繁先于简」同序）；cht 大小写不敏感
        if (hint.Contains('繁') || hint.Contains("cht", StringComparison.OrdinalIgnoreCase)) return "zh-TW";
        if (hint.Contains('简') || hint.Contains("双语", StringComparison.Ordinal) || hint.Contains('中')) return "zh";
        if (hint.Contains('英')) return "en";
        if (hint.Contains('日')) return "ja";
        return "und";
    }

    /// <summary>计算落地文件名与完整路径：{视频基名}.{lang}{ext}，已存在则 .2.{lang} … .9.{lang} 序号避让</summary>
    /// <remarks>落地名完全自构（基名取自归档视频、扩展名已过白名单）；序号格式与 SubtitleRenamer.Plan 产物一致（语言段紧贴扩展名，Plex 才能识别语言）。</remarks>
    private static (string FileName, string FullPath) ResolveTargetPath(string videoTargetPath, string language, string ext)
    {
        string? dir = Path.GetDirectoryName(videoTargetPath);
        if (string.IsNullOrEmpty(dir))
            throw new BusinessException("归档目标路径异常，无法定位字幕落点");
        string stem = Path.GetFileNameWithoutExtension(videoTargetPath);
        string lowerExt = ext.ToLowerInvariant();

        string fileName = $"{stem}.{language}{lowerExt}";
        string fullPath = Path.Combine(dir, fileName);
        if (!File.Exists(fullPath)) return (fileName, fullPath);

        for (int n = 2; n <= MaxOrdinal; n++)
        {
            fileName = $"{stem}.{n}.{language}{lowerExt}";
            fullPath = Path.Combine(dir, fileName);
            if (!File.Exists(fullPath)) return (fileName, fullPath);
        }
        throw new BusinessException("同语言字幕过多，请清理后重试");
    }
}
