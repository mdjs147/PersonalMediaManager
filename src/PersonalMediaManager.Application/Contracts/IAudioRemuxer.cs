namespace PersonalMediaManager.Application.Contracts;

/// <summary>音频重混契约 / Remux dropping tracks</summary>
/// <remarks>
/// 流复制丢弃指定音轨（去 av3a 等不兼容轨，保留视频 / 字幕 / 兼容音轨）。
/// 实现位于 Infrastructure.Platform/FileSystem/FfmpegAudioRemuxer.cs，内部 Process 调外部 ffmpeg：
///   ffmpeg -i source -map 0 -map -0:{idx}... -c copy target
/// 全程 stream copy（不重编码，故无需 av3a 解码器，耗时取决于文件大小与盘速而非 CPU 编码）。
/// ffmpeg 路径由调用方传入（同 <see cref="IMediaAudioProbe"/>，Platform 不读 DB）。
/// 直接输出到归档目标路径（就近目标盘，避免「源盘重混一遍 + 跨盘再拷一遍」的大文件双倍 IO）：
///   先写 target 的 .tmp 临时文件，成功后原子 rename 到 target；失败清理 .tmp、绝不留半成品、绝不删源。
/// deleteSourceAfter=true（Move 语义）重混成功后删源；false（Copy 语义）保留源。
/// 失败抛异常（与「归档落地前失败 → ProcessFileService 转 Failed、源未动可 Rescan」语义一致）。
/// </remarks>
public interface IAudioRemuxer
{
    /// <summary>流复制 source 到 target 并丢弃 dropStreamIndexes 指定音轨</summary>
    /// <param name="ffmpegPath">ffmpeg 可执行文件绝对路径</param>
    /// <param name="sourcePath">源媒体文件绝对路径</param>
    /// <param name="targetPath">归档目标绝对路径（直接输出落点）</param>
    /// <param name="dropStreamIndexes">要丢弃的全局流索引（不兼容音轨）</param>
    /// <param name="deleteSourceAfter">true=Move 语义重混后删源；false=Copy 语义保留源</param>
    /// <exception cref="FileConflictException">目标已存在</exception>
    Task RemuxAsync(
        string ffmpegPath,
        string sourcePath,
        string targetPath,
        IReadOnlyList<int> dropStreamIndexes,
        bool deleteSourceAfter,
        CancellationToken ct = default);
}

/// <summary>归档层重混计划（编排层据探测结果 + 设置构造，随 ArchiveAsync 传入）</summary>
/// <remarks>非 null 即表示「本次归档需重混丢轨」，由 ArchiveService 第 6 步用 <see cref="IAudioRemuxer"/> 替代纯文件移动。</remarks>
/// <param name="FfmpegPath">ffmpeg 可执行文件绝对路径</param>
/// <param name="DropStreamIndexes">要丢弃的全局流索引（不兼容音轨）</param>
public sealed record AudioRemuxPlan(string FfmpegPath, IReadOnlyList<int> DropStreamIndexes);
