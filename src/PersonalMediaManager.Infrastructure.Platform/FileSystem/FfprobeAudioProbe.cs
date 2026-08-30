using System.Text.Json;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Contracts;

namespace PersonalMediaManager.Infrastructure.Platform.FileSystem;

/// <summary>音频探测实现 / ffprobe-based</summary>
/// <remarks>
/// 调外部 ffprobe（<c>-print_format json -show_streams -select_streams a</c>）读音轨编解码器并按清单标记不兼容轨。
/// 任何不可用情形（路径无效 / ffprobe 缺失 / 非零退出 / 非媒体文件 / 异常）一律返回
/// <see cref="AudioProbeResult.Unavailable"/>，绝不抛——缺工具不阻断入库主流程（仅「真取消」上抛）。
/// </remarks>
internal sealed class FfprobeAudioProbe : IMediaAudioProbe
{
    private const int ProbeTimeoutSeconds = 120;
    private readonly ILogger<FfprobeAudioProbe> _logger;

    public FfprobeAudioProbe(ILogger<FfprobeAudioProbe> logger) => _logger = logger;

    public async Task<AudioProbeResult> ProbeAsync(
        string ffprobePath,
        string mediaFilePath,
        IReadOnlySet<string> incompatibleCodecs,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ffprobePath) || !File.Exists(ffprobePath))
            return AudioProbeResult.Unavailable($"ffprobe 不存在：{ffprobePath}");
        if (!File.Exists(mediaFilePath))
            return AudioProbeResult.Unavailable($"媒体文件不存在：{mediaFilePath}");

        try
        {
            string[] args =
            {
                "-v", "error",
                "-print_format", "json",
                "-show_streams",
                "-select_streams", "a",
                "-i", mediaFilePath,
            };
            ProcessRunResult run = await FfmpegProcessRunner.RunAsync(ffprobePath, args, ProbeTimeoutSeconds, ct);
            if (!run.Success)
            {
                _logger.LogWarning("ffprobe 非零退出（{Code}）：{File} — {Err}",
                    run.ExitCode, mediaFilePath, Truncate(run.StdErr));
                return AudioProbeResult.Unavailable($"ffprobe 退出码 {run.ExitCode}");
            }
            IReadOnlyList<AudioStreamInfo> streams = ParseAudioStreams(run.StdOut, incompatibleCodecs);
            return AudioProbeResult.Ok(streams);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ffprobe 探测异常：{File}", mediaFilePath);
            return AudioProbeResult.Unavailable($"ffprobe 执行异常：{ex.Message}");
        }
    }

    /// <summary>解析 ffprobe -show_streams JSON 的音频流数组</summary>
    /// <remarks>容错：根无 streams / 字段缺失一律降级（codec 空串、index=-1），不抛 —— 上层据 Available 已确保是成功输出。</remarks>
    internal static IReadOnlyList<AudioStreamInfo> ParseAudioStreams(string json, IReadOnlySet<string> incompatibleCodecs)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<AudioStreamInfo>();

        using JsonDocument doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("streams", out JsonElement streamsEl)
            || streamsEl.ValueKind != JsonValueKind.Array)
            return Array.Empty<AudioStreamInfo>();

        List<AudioStreamInfo> list = new();
        foreach (JsonElement s in streamsEl.EnumerateArray())
        {
            int index = s.TryGetProperty("index", out JsonElement idxEl) && idxEl.TryGetInt32(out int idx) ? idx : -1;
            string codec = s.TryGetProperty("codec_name", out JsonElement cEl) && cEl.ValueKind == JsonValueKind.String
                ? (cEl.GetString() ?? string.Empty)
                : string.Empty;

            string? language = null, title = null;
            if (s.TryGetProperty("tags", out JsonElement tagsEl) && tagsEl.ValueKind == JsonValueKind.Object)
            {
                if (tagsEl.TryGetProperty("language", out JsonElement lEl) && lEl.ValueKind == JsonValueKind.String)
                    language = lEl.GetString();
                if (tagsEl.TryGetProperty("title", out JsonElement tEl) && tEl.ValueKind == JsonValueKind.String)
                    title = tEl.GetString();
            }
            int? channels = s.TryGetProperty("channels", out JsonElement chEl) && chEl.TryGetInt32(out int ch) ? ch : null;

            bool incompatible = codec.Length > 0 && incompatibleCodecs.Contains(codec);
            list.Add(new AudioStreamInfo(index, codec, language, channels, title, incompatible));
        }
        return list;
    }

    private static string Truncate(string? s, int max = 400)
        => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s[..max] + "…");
}
