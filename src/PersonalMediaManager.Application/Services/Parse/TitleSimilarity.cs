namespace PersonalMediaManager.Application.Services.Parse;

/// <summary>标题相似度工具（归一化 Levenshtein）</summary>
/// <remarks>
/// Ratio(a,b) = 1 - Levenshtein(na,nb)/max(len)，na/nb 为「去空白 + 小写」归一化后的字符串。
/// 两者皆空 → 1.0；仅一方空 → 0.0；完全相同 → 1.0。
/// 放在 Application 层是为了让 Persistence（文件夹级 series 复用守门）与 External（TMDB 候选打分）
/// 两个 Infrastructure 子项目都能引用——红线禁止 Infra 子项目互引，公共算法只能上提到 Application。
/// </remarks>
public static class TitleSimilarity
{
    /// <summary>归一化 Levenshtein 相似度 [0,1]：大小写不敏感 + 去空白</summary>
    public static double Ratio(string? a, string? b)
    {
        string na = Normalize(a);
        string nb = Normalize(b);
        if (na.Length == 0 && nb.Length == 0) return 1.0;
        if (na.Length == 0 || nb.Length == 0) return 0.0;
        int dist = LevenshteinDistance(na, nb);
        int maxLen = Math.Max(na.Length, nb.Length);
        return 1.0 - (double)dist / maxLen;
    }

    private static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
    }

    /// <summary>Wagner-Fischer Levenshtein（O(n*m)，对短标题足够）</summary>
    private static int LevenshteinDistance(string a, string b)
    {
        int n = a.Length;
        int m = b.Length;
        int[,] dp = new int[n + 1, m + 1];
        for (int i = 0; i <= n; i++) dp[i, 0] = i;
        for (int j = 0; j <= m; j++) dp[0, j] = j;
        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                dp[i, j] = Math.Min(
                    Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1),
                    dp[i - 1, j - 1] + cost);
            }
        }
        return dp[n, m];
    }
}
