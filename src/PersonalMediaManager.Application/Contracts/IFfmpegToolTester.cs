namespace PersonalMediaManager.Application.Contracts;

/// <summary>ffmpeg / ffprobe 工具可用性自检契约</summary>
/// <remarks>
/// 实现位于 Infrastructure.Platform/FileSystem/FfmpegToolTester.cs，内部 Process 跑 <c>-version</c> 验证可执行文件能启动。
/// exe 路径由调用方（编排层从 Audio_FfmpegPath 设置解析）传入——守 Platform 不读 DB / 不读设置红线，本契约只按给定路径执行。
/// 任何不可用情形（路径空 / 文件不存在 / 非零退出 / 启动异常）一律归 Runnable=false + Error，绝不抛（仅「真取消」上抛），
/// 与 <see cref="IMediaAudioProbe"/> / <see cref="IAudioRemuxer"/> 的「缺工具优雅降级」风格一致。
/// </remarks>
public interface IFfmpegToolTester
{
    /// <summary>跑 {exe} -version 验证工具可启动并取版本首行</summary>
    /// <param name="exePath">ffprobe / ffmpeg 可执行文件绝对路径</param>
    Task<FfmpegToolProbe> ProbeAsync(string exePath, CancellationToken ct = default);
}

/// <summary>单个 ffmpeg 系工具自检结果</summary>
/// <param name="Runnable">能成功启动并以退出码 0 返回</param>
/// <param name="Version">版本首行（如 ffmpeg version 6.1.1-...；不可用为 null）</param>
/// <param name="Error">不可用原因（Runnable=true 时为 null）</param>
public sealed record FfmpegToolProbe(bool Runnable, string? Version, string? Error);
