using System.Text.Json;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Classify;

/// <summary>Category_MatchRule.Conditions JSON 条件树评估器</summary>
/// <remarks>
/// JSON 结构（数据库设计 §1.11）：
/// {
///   "all": [
///     { "field": "type", "op": "eq", "value": "tv" },
///     { "field": "originCountry", "op": "in", "value": ["CN","HK","TW"] }
///   ]
/// }
/// 或 "any" 表示任一命中。叶子 op ∈ {eq, in, contains, notEq, notIn}。
/// 顶层为单个叶子时也支持（视为 {all:[leaf]}）。
///
/// 字段语义：
///   - type：MediaType（movie/tv），大小写不敏感
///   - originalLanguage：ISO 639-1（zh/en/ja），大小写不敏感
///   - originCountry：列表，op=in 时 value 是列表，任一元素属于 OriginCountries 即匹配
///   - genres：列表（TMDB 类型名，如 Animation / Documentary），op=contains 时 value 是单值
///
/// 容错：JSON 解析失败 / 字段未知 / op 未知 → 整条规则视为不命中（false），上层继续下一条。
/// </remarks>
internal static class CategoryRuleEvaluator
{
    public static bool Evaluate(string conditionsJson, ClassifyContext ctx)
    {
        if (string.IsNullOrWhiteSpace(conditionsJson)) return false;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(conditionsJson); }
        catch (JsonException) { return false; }

        using (doc)
        {
            return EvaluateNode(doc.RootElement, ctx);
        }
    }

    private static bool EvaluateNode(JsonElement node, ClassifyContext ctx)
    {
        if (node.ValueKind != JsonValueKind.Object) return false;

        if (node.TryGetProperty("all", out JsonElement allArr) && allArr.ValueKind == JsonValueKind.Array)
        {
            if (allArr.GetArrayLength() == 0) return false;
            foreach (JsonElement child in allArr.EnumerateArray())
            {
                if (!EvaluateNode(child, ctx)) return false;
            }
            return true;
        }
        if (node.TryGetProperty("any", out JsonElement anyArr) && anyArr.ValueKind == JsonValueKind.Array)
        {
            if (anyArr.GetArrayLength() == 0) return false;
            foreach (JsonElement child in anyArr.EnumerateArray())
            {
                if (EvaluateNode(child, ctx)) return true;
            }
            return false;
        }

        // 叶子
        if (!node.TryGetProperty("field", out JsonElement fieldEl) || fieldEl.ValueKind != JsonValueKind.String) return false;
        if (!node.TryGetProperty("op", out JsonElement opEl) || opEl.ValueKind != JsonValueKind.String) return false;
        if (!node.TryGetProperty("value", out JsonElement valueEl)) return false;

        string field = fieldEl.GetString()!;
        string op = opEl.GetString()!;
        return EvaluateLeaf(field, op, valueEl, ctx);
    }

    private static bool EvaluateLeaf(string field, string op, JsonElement value, ClassifyContext ctx)
    {
        // 拿到字段当前值（单值 or 列表）
        switch (field.ToLowerInvariant())
        {
            case "type":
                return EvalSingle(op, value, ctx.MediaType);
            case "originallanguage":
                return EvalSingle(op, value, ctx.OriginalLanguage);
            case "origincountry":
                return EvalList(op, value, ctx.OriginCountries);
            case "genres":
                return EvalList(op, value, ctx.Genres);
            default:
                return false;
        }
    }

    /// <summary>单值字段：op=eq/notEq/in/notIn</summary>
    private static bool EvalSingle(string op, JsonElement value, string? current)
    {
        switch (op.ToLowerInvariant())
        {
            case "eq":
                return value.ValueKind == JsonValueKind.String
                    && EqualsIgnoreCase(current, value.GetString());
            case "noteq":
                return value.ValueKind == JsonValueKind.String
                    && !EqualsIgnoreCase(current, value.GetString());
            case "in":
                return value.ValueKind == JsonValueKind.Array
                    && AnyEqual(value, current);
            case "notin":
                return value.ValueKind == JsonValueKind.Array
                    && !AnyEqual(value, current);
            default:
                return false;
        }
    }

    /// <summary>列表字段：op=contains/eq/in/notIn</summary>
    private static bool EvalList(string op, JsonElement value, IReadOnlyList<string> current)
    {
        switch (op.ToLowerInvariant())
        {
            case "contains":
                // value 是单值，current 是列表，列表包含 value 即命中
                return value.ValueKind == JsonValueKind.String
                    && current.Any(c => EqualsIgnoreCase(c, value.GetString()));
            case "in":
                // value 是列表，current 列表中任一元素出现在 value 列表 → 命中
                return value.ValueKind == JsonValueKind.Array
                    && current.Any(c => AnyEqual(value, c));
            case "notin":
                return value.ValueKind == JsonValueKind.Array
                    && !current.Any(c => AnyEqual(value, c));
            case "eq":
                // 与单值同语义：列表只有一个元素且等于 value
                return current.Count == 1 && value.ValueKind == JsonValueKind.String
                    && EqualsIgnoreCase(current[0], value.GetString());
            default:
                return false;
        }
    }

    private static bool AnyEqual(JsonElement arr, string? needle)
    {
        foreach (JsonElement el in arr.EnumerateArray())
        {
            if (el.ValueKind == JsonValueKind.String && EqualsIgnoreCase(el.GetString(), needle))
                return true;
        }
        return false;
    }

    private static bool EqualsIgnoreCase(string? a, string? b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>分类规则评估上下文</summary>
/// <param name="MediaType">movie / tv（来自 MediaItem.TmdbMediaType）</param>
/// <param name="OriginalLanguage">ISO 639-1（来自 Tmdb_MetadataCache）</param>
/// <param name="OriginCountries">来源国列表（never null，未知为空 list）</param>
/// <param name="Genres">类型名列表（never null，未知为空 list）</param>
public sealed record ClassifyContext(
    string MediaType,
    string? OriginalLanguage,
    IReadOnlyList<string> OriginCountries,
    IReadOnlyList<string> Genres);
