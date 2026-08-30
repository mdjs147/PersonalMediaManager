namespace PersonalMediaManager.Infrastructure.Persistence.Services.Parse;

/// <summary>音频不兼容处理共享支撑 / Audio probe shared helpers</summary>
/// <remarks>
/// 设置 key + ffmpeg 路径解析 + codec 清单解析，供 ProcessFileService(归档前探测 / 重混决策)
/// 与 LibraryService(存量批量重扫打标)共用，避免两处逻辑漂移。纯静态、无状态、不碰 DB。
/// </remarks>
internal static class AudioProbeSupport
{
    /// <summary>音频不兼容检查总开关 key（须与 SystemSettingConfig 种子 1:1 对齐）</summary>
    public const string CheckEnabledKey = "Audio_IncompatibleCheckEnabled";

    /// <summary>ffmpeg / ffprobe 路径 key（目录或 exe 全路径）</summary>
    public const string FfmpegPathKey = "Audio_FfmpegPath";

    /// <summary>不兼容 codec 清单 key（逗号分隔，小写）</summary>
    public const string IncompatibleCodecsKey = "Audio_IncompatibleCodecs";

    /// <summary>自动重混开关 key</summary>
    public const string AutoRemuxKey = "Audio_AutoRemux";

    /// <summary>解析 ffprobe / ffmpeg 可执行文件全路径（配置可为安装目录或某个 exe 全路径；缺失返回 null）</summary>
    public static (string? Ffprobe, string? Ffmpeg) ResolveFfmpegTools(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return (null, null);
        configured = configured.Trim();

        string? dir;
        if (Directory.Exists(configured))
            dir = configured;
        else if (File.Exists(configured))
            dir = Path.GetDirectoryName(configured);
        else
            return (null, null);
        if (string.IsNullOrEmpty(dir))
            return (null, null);

        string probeExe = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";
        string mpegExe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        string ffprobe = Path.Combine(dir, probeExe);
        string ffmpeg = Path.Combine(dir, mpegExe);
        return (File.Exists(ffprobe) ? ffprobe : null, File.Exists(ffmpeg) ? ffmpeg : null);
    }

    /// <summary>解析不兼容 codec 清单（逗号 / 分号 / 空白分隔，小写化，OrdinalIgnoreCase）</summary>
    public static IReadOnlySet<string> ParseCodecList(string? raw)
    {
        HashSet<string> set = new(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
            return set;
        foreach (string t in raw.Split([',', ';', '\n', '\r', '\t', ' '],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            set.Add(t.ToLowerInvariant());
        return set;
    }

    /// <summary>设置值是否为 true（缺失 / 非 "true" 一律 false）</summary>
    public static bool IsSettingTrue(IReadOnlyDictionary<string, string?> settings, string key)
        => settings.TryGetValue(key, out string? v) && string.Equals(v?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>读设置值（缺失返回 null）</summary>
    public static string? GetSetting(IReadOnlyDictionary<string, string?> settings, string key)
        => settings.TryGetValue(key, out string? v) ? v : null;
}
