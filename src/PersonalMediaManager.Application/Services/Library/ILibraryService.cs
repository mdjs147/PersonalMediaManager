using PersonalMediaManager.Application.Dtos.Library;

namespace PersonalMediaManager.Application.Services.Library;

/// <summary>媒体库服务契约 — 浏览已归档作品（按 TMDB 作品聚合）+ 富化关联 + 库内搜索</summary>
/// <remarks>
/// 列表作品全集来自 Status=Completed 且有 TmdbId 的 MediaItem（按 TmdbId+MediaType 聚合）；
/// 富化字段（评分/类型/演职员/公司/关键词/季集）LEFT JOIN Media_Work（详情打开惰性富化，手动整库刷新批量回填）。
/// 维度过滤（genre/person/company/network/keyword）走连接表 SQL；年份/国家/语言在合并元数据后内存过滤。只读。
/// </remarks>
public interface ILibraryService
{
    /// <summary>分页查询已归档作品（支持类型/分类/关键词/维度/年份/国家/语言过滤 + 排序）</summary>
    Task<LibraryListPage> ListAsync(LibraryListQuery query, CancellationToken ct = default);

    /// <summary>作品详情：富化元数据 + 演职员/公司/类型/关键词/季摘要 + 计数汇总 + 全部文件记录（含实时存在性）</summary>
    /// <remarks>打开时惰性富化；TMDB 失败降级返回已有数据。无任何文件记录抛 BusinessException。</remarks>
    Task<LibraryWorkDetailResponse> GetWorkDetailAsync(int tmdbId, string mediaType, CancellationToken ct = default);

    /// <summary>某剧集季的分集（每集简介/剧照 + 本地文件映射）；首次展开惰性拉取分集</summary>
    Task<LibrarySeasonResponse> GetSeasonAsync(int tmdbId, int seasonNumber, string mediaType, CancellationToken ct = default);

    /// <summary>相关作品（按共享 类型/演职员/公司 计分，仅库内已归档作品）</summary>
    Task<IReadOnlyList<LibraryRelatedItem>> GetRelatedAsync(int tmdbId, string mediaType, CancellationToken ct = default);

    /// <summary>媒体库可用筛选项（库内已富化作品出现过的 类型/演职员/公司/电视台/关键词/国家/语言）</summary>
    Task<LibraryFacets> GetFacetsAsync(CancellationToken ct = default);

    /// <summary>整库文件存在性扫描：对全部已完成记录 File.Exists(TargetPath) 落 FileMissing/FileCheckedAt</summary>
    Task<ScanExistenceResult> ScanExistenceAsync(CancellationToken ct = default);

    /// <summary>存量音频重扫：对未探测过的已完成记录批量 ffprobe 探测 TargetPath，回写 AudioCodecs/HasIncompatibleAudio（仅打标不重混）</summary>
    Task<AudioRescanResult> RescanAudioAsync(CancellationToken ct = default);

    /// <summary>孤儿文件扫描：遍历各分类归档根，列出磁盘有、DB 无记录的媒体文件（删了记录但文件留在库里的找回入口）；只读</summary>
    Task<OrphanScanResult> ScanOrphansAsync(CancellationToken ct = default);

    /// <summary>孤儿认领：把选中孤儿文件重新投入处理管线重新登记入库；已在规范位的由归档层识别「已就位」直接登记，不搬动</summary>
    Task<ClaimOrphansResult> ClaimOrphansAsync(ClaimOrphansRequest req, CancellationToken ct = default);

    /// <summary>解析某 TMDB 作品的本地缓存海报路径；无缓存返回 null</summary>
    string? GetPosterPath(int tmdbId);
}
