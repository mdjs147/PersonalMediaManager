namespace PersonalMediaManager.Application.Services.Parse;

/// <summary>同文件夹解析结果复用缓存</summary>
/// <remarks>
/// 同一剧集的多个文件通常落在同一目录：剧名 / 类型 / 年份 / TMDBid 是 series 级共享信息，
/// 仅季 / 集号逐文件不同。当某文件成功锁定 TMDB 匹配后，按「所在目录」缓存其 series 级信息；
/// 同目录后续文件可直接复用此结果跳过 TMDB 搜索（及 AI 兜底），季 / 集号仍由规则引擎逐文件提取。
///
/// 生命周期：本接口实现为进程级单例（L1），进程重启即清空。但 ProcessFileService 在 L1 未命中时会
/// 从持久库（Media_Item 同文件夹已归档兄弟集）还原 series 身份回填本缓存（L2 持久层），
/// 故重启后首集亦能复用、无需重新兜底 —— 详见 ProcessFileService.TryResolveSeriesFromDbAsync。
/// 介入边界：规则直查路径与 AI 兜底路径均先查本缓存；命中后由标题相似度守门把关防张冠李戴。
/// 线程安全：处理链路单消费者串行（TaskProcessorWorker Semaphore(1,1)），实现用 ConcurrentDictionary 即足够。
/// </remarks>
public interface IFolderSeriesCache
{
    /// <summary>查同目录已锁定的 series 信息；无则 null</summary>
    FolderSeriesEntry? TryGet(string folderPath);

    /// <summary>写入 / 覆盖同目录的 series 信息</summary>
    void Set(string folderPath, FolderSeriesEntry entry);

    /// <summary>移除同目录的 series 缓存条目（键与 Set / TryGet 同为目录完整路径）</summary>
    /// <remarks>
    /// 用户在审核页纠正错误匹配（confirm / bind-tmdb）后由 ReviewService 调用：L1 内存条目永远优先于
    /// L2 持久层，不失效则进程常驻期间同目录后续文件（典型如周更剧）持续复用旧错误 tmdbId；
    /// 失效后下次按 L2（已更正的本记录）重新回填。
    /// 默认空实现：写入方（ProcessFileService）与既有测试桩无失效语义、不必逐一补实现；
    /// 进程级单例实现（FolderSeriesCache）必须覆写为真删除。
    /// </remarks>
    void Remove(string folderPath) { }
}

/// <summary>文件夹级 series 缓存条目（TMDB 已锁定的 series 维度信息，不含季 / 集）</summary>
/// <param name="TmdbId">TMDB 条目 id</param>
/// <param name="MediaType">"movie" 或 "tv"</param>
/// <param name="Title">主标题（中文优先）</param>
/// <param name="Year">年份</param>
/// <param name="Confidence">原判定置信度（复用时沿用，仅用于落库展示）</param>
public sealed record FolderSeriesEntry(
    int TmdbId,
    string MediaType,
    string? Title,
    int? Year,
    double? Confidence);
