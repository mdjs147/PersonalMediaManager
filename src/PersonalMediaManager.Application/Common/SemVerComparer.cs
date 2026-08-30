namespace PersonalMediaManager.Application.Common;

/// <summary>极简 SemVer 数字段比较（升级检查与未来其它版本号场景共用）</summary>
/// <remarks>
/// 比较算法：
///   1. 两侧均剥 "v" / "V" 前缀
///   2. 按 "." 拆分；逐段 int.Parse
///   3. 任一段非纯数字 → 回退字符串严格相等（不等 = 视为新版本，避免「未知」漏判）
///   4. 段数不一致按补零比较（"0.2" vs "0.2.0" → 相等）
/// pre-release 后缀（"-rc1" / "+build" 等）当前忽略，落入回退路径。
/// </remarks>
public static class SemVerComparer
{
    /// <summary>candidate 是否严格更新于 baseline</summary>
    public static bool IsNewer(string candidate, string baseline)
    {
        string a = (candidate ?? string.Empty).TrimStart('v', 'V').Trim();
        string b = (baseline ?? string.Empty).TrimStart('v', 'V').Trim();
        if (a.Length == 0) return false;
        if (b.Length == 0) return true;

        string[] aParts = a.Split('.');
        string[] bParts = b.Split('.');
        int max = Math.Max(aParts.Length, bParts.Length);
        for (int i = 0; i < max; i++)
        {
            string ap = i < aParts.Length ? aParts[i] : "0";
            string bp = i < bParts.Length ? bParts[i] : "0";
            if (!int.TryParse(ap, out int ai) || !int.TryParse(bp, out int bi))
            {
                // 任一段非纯数字 → 回退字符串严格相等：不等 = 视为新版本
                return !string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
            if (ai > bi) return true;
            if (ai < bi) return false;
        }
        return false;
    }
}
