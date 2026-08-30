using PersonalMediaManager.Application.Dtos.Subtitles;

namespace PersonalMediaManager.Application.Services.Subtitles;

/// <summary>字幕下载服务契约（搜索 → 文件清单 → 下载落盘 → 记录查询）</summary>
/// <remarks>
/// 编排流程（实现位于 Infrastructure.Persistence/Services/Subtitles，组合 ISubtitleProviderRegistry + 设置服务 + 媒体记录）：
/// 1. SearchAsync：keyword 为空时按 mediaItemId 对应媒体的标题推导默认关键词，再走 ISubtitleProvider.SearchAsync；
/// 2. GetFilesAsync：读候选字幕的文件清单；响应刻意不暴露源站直链 url（防 SSRF），仅回 Index + 文件名 + 大小；
/// 3. DownloadAsync：服务端按 SubtitleId + FileIndex 重取直链再下载（不信任前端传 url），扩展名须命中
///    SubtitleRenamer.SupportedExtensions 白名单，语言码按 SubtitleRenamer.DetectLanguage 识别（zh/zh-TW/en/ja/und），
///    落盘到媒体归档目录并写下载记录；
/// 4. ListDownloadsAsync：查该媒体已下载字幕的记录列表。
/// 失败语义：业务校验失败抛 BusinessException；字幕源调用失败（SubtitleProviderException）由实现转译为业务错误。
/// </remarks>
public interface ISubtitleDownloadService
{
    /// <summary>按媒体搜索外部字幕源（keyword 空 = 服务端按媒体标题推导）</summary>
    Task<SubtitleSearchResponse> SearchAsync(long mediaItemId, string? keyword, CancellationToken ct = default);

    /// <summary>读候选字幕的文件清单（不暴露源站直链）</summary>
    Task<SubtitleFilesResponse> GetFilesAsync(long mediaItemId, string subtitleId, CancellationToken ct = default);

    /// <summary>下载选中字幕文件并落盘 + 写下载记录</summary>
    Task<SubtitleDownloadResponse> DownloadAsync(long mediaItemId, DownloadSubtitleRequest request, CancellationToken ct = default);

    /// <summary>查该媒体的字幕下载记录</summary>
    Task<SubtitleDownloadListResponse> ListDownloadsAsync(long mediaItemId, CancellationToken ct = default);
}
