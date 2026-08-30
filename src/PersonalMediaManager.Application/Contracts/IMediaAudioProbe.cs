namespace PersonalMediaManager.Application.Contracts;

/// <summary>媒体音频探测契约 / Probe audio tracks</summary>
/// <remarks>
/// 读媒体文件音轨编解码器，用于识别不兼容编码（如 av3a Audio Vivid 菁彩声，Plex 等多数客户端无法解码）。
/// 实现位于 Infrastructure.Platform/FileSystem/FfprobeAudioProbe.cs，内部 Process 调外部 ffprobe。
/// ffprobe 可执行文件路径由调用方（编排层 ProcessFileService 从 Audio_FfmpegPath 设置解析）传入——
/// 守 Infrastructure 子项目互不引用红线：Platform 不读 DB / 不读设置，只按给定工具路径执行。
/// 探测失败（路径无效 / ffprobe 缺失 / 超时 / 非媒体文件）一律返回 <see cref="AudioProbeResult.Unavailable"/>，绝不抛——
/// 调用方据此优雅降级（跳过音频处理，归档照常），不让缺工具阻断入库主流程。
/// </remarks>
public interface IMediaAudioProbe
{
    /// <summary>探测全部音轨并按清单标记不兼容轨</summary>
    /// <param name="ffprobePath">ffprobe 可执行文件绝对路径</param>
    /// <param name="mediaFilePath">待探测的媒体文件绝对路径</param>
    /// <param name="incompatibleCodecs">不兼容编解码器名清单（小写，如 av3a），命中即标记该轨不兼容</param>
    Task<AudioProbeResult> ProbeAsync(
        string ffprobePath,
        string mediaFilePath,
        IReadOnlySet<string> incompatibleCodecs,
        CancellationToken ct = default);
}

/// <summary>单条音轨信息（ffprobe stream 投影）</summary>
/// <param name="Index">全局流索引（ffprobe streams[].index；重混丢轨按此 -map -0:index 排除）</param>
/// <param name="Codec">编解码器名（codec_name，小写，如 av3a / aac / dts / truehd）</param>
/// <param name="Language">语言标签（tags.language，如 chi / eng；缺失为 null）</param>
/// <param name="Channels">声道数（缺失为 null）</param>
/// <param name="Title">轨道标题（tags.title；缺失为 null）</param>
/// <param name="IsIncompatible">codec 命中不兼容清单</param>
public sealed record AudioStreamInfo(
    int Index,
    string Codec,
    string? Language,
    int? Channels,
    string? Title,
    bool IsIncompatible);

/// <summary>音频探测结果</summary>
/// <remarks>
/// <see cref="Available"/>=false 表示探测未成功执行（工具不可用 / 失败），此时 <see cref="Streams"/> 为空，
/// 不应据此做任何重混决策（避免「探不到 = 没有不兼容轨」的误判）。
/// </remarks>
public sealed record AudioProbeResult
{
    /// <summary>探测是否成功执行（false=ffprobe 不可用 / 执行失败 → 降级跳过）</summary>
    public required bool Available { get; init; }

    /// <summary>全部音轨（Available=false 时为空）</summary>
    public IReadOnlyList<AudioStreamInfo> Streams { get; init; } = Array.Empty<AudioStreamInfo>();

    /// <summary>探测不可用时的原因（供日志 / 时间线 detail）</summary>
    public string? Error { get; init; }

    /// <summary>存在不兼容音轨</summary>
    public bool HasIncompatible => Streams.Any(s => s.IsIncompatible);

    /// <summary>存在兼容音轨（重混丢不兼容轨的前提：否则丢光会变无声，只能标记不能重混）</summary>
    public bool HasCompatible => Streams.Any(s => !s.IsIncompatible);

    /// <summary>不兼容音轨的全局流索引（重混丢轨用）</summary>
    public IReadOnlyList<int> IncompatibleStreamIndexes =>
        Streams.Where(s => s.IsIncompatible).Select(s => s.Index).ToArray();

    /// <summary>探测不可用结果工厂</summary>
    public static AudioProbeResult Unavailable(string error) => new() { Available = false, Error = error };

    /// <summary>探测成功结果工厂</summary>
    public static AudioProbeResult Ok(IReadOnlyList<AudioStreamInfo> streams) =>
        new() { Available = true, Streams = streams };
}
