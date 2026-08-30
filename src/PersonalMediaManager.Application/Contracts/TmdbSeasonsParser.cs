using System.Globalization;
using System.Text.Json;

namespace PersonalMediaManager.Application.Contracts;

/// <summary>TMDB 剧集每季集数解析器</summary>
/// <remarks>
/// 从 TMDB 剧集详情原始 JSON 的 seasons[] 提取每季 {season_number, episode_count}，供「绝对集号 → 季/集」换算。
/// HTTP 详情解析（TmdbClient）与元数据缓存命中（TmdbSearchService）两条路径共用，保证口径一致。
/// 无 seasons 字段 / JSON 损坏一律返回空集合，调用方降级为手填季号。
/// </remarks>
public static class TmdbSeasonsParser
{
    /// <summary>从原始 JSON 字符串解析（内部自行 Parse；空或损坏返回空集合）</summary>
    public static IReadOnlyList<TmdbSeasonInfo> Parse(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return Array.Empty<TmdbSeasonInfo>();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawJson);
            return Parse(doc.RootElement);
        }
        catch (JsonException)
        {
            return Array.Empty<TmdbSeasonInfo>();
        }
    }

    /// <summary>从已解析的 JSON 根元素解析（复用调用方已 Parse 的文档，避免二次解析）</summary>
    public static IReadOnlyList<TmdbSeasonInfo> Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("seasons", out JsonElement seasonsEl)
            || seasonsEl.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<TmdbSeasonInfo>();
        }

        List<TmdbSeasonInfo> list = new();
        foreach (JsonElement s in seasonsEl.EnumerateArray())
        {
            if (s.ValueKind != JsonValueKind.Object) continue;
            if (!s.TryGetProperty("season_number", out JsonElement snEl) || !snEl.TryGetInt32(out int sn)) continue;
            int ec = s.TryGetProperty("episode_count", out JsonElement ecEl) && ecEl.TryGetInt32(out int ecv) ? ecv : 0;
            string? name = s.TryGetProperty("name", out JsonElement nmEl) && nmEl.ValueKind == JsonValueKind.String
                ? nmEl.GetString()
                : null;
            // 季首播年：seasons[].air_date（"YYYY-MM-DD"）取年份；未播季为空串 / 缺字段 → null
            int? year = s.TryGetProperty("air_date", out JsonElement adEl)
                && adEl.ValueKind == JsonValueKind.String
                && DateOnly.TryParse(adEl.GetString(), CultureInfo.InvariantCulture, out DateOnly ad)
                ? ad.Year
                : null;
            list.Add(new TmdbSeasonInfo(sn, ec, name, year));
        }
        return list;
    }
}
