using PersonalMediaManager.Domain.Common;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Domain.Entities;

/// <summary>忽略规则（Watch_IgnoreRule）</summary>
/// <remarks>
/// Type = Extension：含点扩展名（如 .part / .tmp）；Type = Keyword：子串匹配，小写归一化。
/// 默认种子（Extension）共 16 条，通过 HasData 注入：下载中临时文件（.part / .tmp / .crdownload / .download / .!qb / .downloading / .complete）
/// + 各下载器中间/辅助文件（.torrent / .xltd / .td / .!ut / .bc! / .aria2 / .partial / .opdownload / .fdmdownload）。
/// 同类型下 Pattern 不重复（UQ_Watch_IgnoreRule_Type_Pattern）。
/// </remarks>
public sealed class WatchIgnoreRule : Entity
{
    public IgnoreRuleType Type { get; set; }

    /// <summary>扩展名（含点）或关键词</summary>
    public string Pattern { get; set; } = default!;

    public string? Description { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>判断给定文件名是否命中本忽略规则（未启用 / 空模式恒不命中）</summary>
    /// <remarks>
    /// Pattern 已由服务层归一化为小写（Extension 含前导点），此处把文件名一并小写后比对：
    ///   - Extension：文件名以 Pattern 结尾即命中，故多重后缀临时文件（如 .mp4.bt.xltd 命中 .xltd）也覆盖；
    ///   - Keyword：文件名包含 Pattern 子串即命中。
    /// 入参应传文件名（非完整路径），避免目录名里的关键词误伤。
    /// </remarks>
    public bool Matches(string fileName)
    {
        if (!Enabled || string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(Pattern))
            return false;

        string lower = fileName.ToLowerInvariant();
        return Type switch
        {
            IgnoreRuleType.Extension => lower.EndsWith(Pattern, StringComparison.Ordinal),
            IgnoreRuleType.Keyword => lower.Contains(Pattern, StringComparison.Ordinal),
            _ => false,
        };
    }
}
