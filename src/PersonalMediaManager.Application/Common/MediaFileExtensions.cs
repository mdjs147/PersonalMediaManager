namespace PersonalMediaManager.Application.Common;

/// <summary>受支持视频扩展名白名单（入队 / 扫描共用单一来源）</summary>
/// <remarks>
/// 文件录入只认这 14 种常见视频后缀；下载中临时格式（如 .part / .crdownload / .mp4.bt.xltd）的
/// 真实扩展名不在表内，天然被拒于入队之外（修复「实时监控放行临时文件」缺陷）。
/// FileIntakeService（监控 / 全扫 / 手动整理统一入口）与 ScanService 共用此表，避免两处白名单漂移。
/// 扩展名比较大小写不敏感，含前导点。
/// </remarks>
public static class MediaFileExtensions
{
    /// <summary>视频扩展名集合（含前导点，OrdinalIgnoreCase）</summary>
    public static readonly IReadOnlySet<string> Video = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".ts", ".mpg", ".mpeg", ".m2ts", ".rmvb",".rm",
    };

    /// <summary>判断文件名（或路径）末段扩展名是否为受支持视频格式</summary>
    /// <remarks>多重后缀（如 .mp4.bt.xltd）按 Path.GetExtension 取最后一段（.xltd）判定，故不被误收。</remarks>
    public static bool IsVideo(string fileNameOrPath) =>
        !string.IsNullOrEmpty(fileNameOrPath) && Video.Contains(Path.GetExtension(fileNameOrPath));
}
