using System.Globalization;
using System.Text.Json.Nodes;
using PersonalMediaManager.Application.Contracts;

namespace PersonalMediaManager.Infrastructure.External.Ai;

internal static partial class AiPromptHelpers
{
    /// <summary>详细字段仅保留输入可见的文本，不把模型自称来源当作证明</summary>
    private static AiMediaDetails? ParseMediaDetails(JsonNode? node, AiParseRequest request, List<AiSchemaIssue> issues)
    {
        if (node is not JsonObject details) return null;
        string[] sources = [request.FileName, .. request.RelativeSegments ??
            (request.ParentFolderName is { } parent ? [parent] : [])];
        void Discard(string path, string code, string expected, JsonNode? value) =>
            issues.Add(new(path, code, expected, SchemaNodeType(value)));
        string? StringValue(JsonNode? value, string path, int max = 160)
        {
            if (value is not JsonValue json || !json.TryGetValue<string>(out string? text)) return null;
            if (string.IsNullOrWhiteSpace(text))
            { Discard(path, "EmptyField", "nonempty string", value); return null; }
            string trimmed = text.Trim();
            string limited = RuneLimit(trimmed, max);
            if (limited != trimmed) Discard(path, "TruncatedField", $"string<={max} runes", value);
            return limited;
        }
        bool Visible(string value) => sources.Any(source => source.Contains(value, StringComparison.OrdinalIgnoreCase));
        string? Literal(string name)
        {
            string path = "$.details." + name;
            string? value = StringValue(details[name], path);
            if (value is not null && !Visible(value))
            { Discard(path, "UnsupportedLiteral", "input-visible string", details[name]); return null; }
            return value;
        }
        string[] Strings(string field, int count = 8, Func<string, bool>? allow = null, string code = "InvalidFieldValue")
        {
            if (details[field] is not JsonArray array) return [];
            List<string> values = [];
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < array.Count; i++)
            {
                string path = $"$.details.{field}[{i}]";
                string? value = StringValue(array[i], path);
                if (value is null) continue;
                if (values.Count >= count) Discard(path, "ItemLimitExceeded", $"at most {count} items", array[i]);
                else if (allow is not null && !allow(value)) Discard(path, code, "supported value", array[i]);
                else if (!seen.Add(value)) Discard(path, "DuplicateItem", "unique string", array[i]);
                else values.Add(value);
            }
            return values.ToArray();
        }

        List<AiTextEvidence> evidence = [];
        if (details["fieldEvidence"] is JsonArray array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                if (array[i] is not JsonObject field) continue;
                string path = $"$.details.fieldEvidence[{i}]";
                if (i >= 16) { Discard(path, "ItemLimitExceeded", "at most 16 items", field); continue; }
                string? key = StringValue(field["field"], path + ".field", 32);
                string? value = StringValue(field["value"], path + ".value");
                string? token = StringValue(field["token"], path + ".token");
                string? source = StringValue(field["source"], path + ".source", 32);
                int? index = ReadInt(field["segmentIndex"]);
                string? raw = source == "FileName" ? request.FileName
                    : source == "RelativeSegment" && index is >= 0 && index < (request.RelativeSegments?.Count ?? 0)
                        ? request.RelativeSegments![index.Value] : null;
                if (key is not ("title" or "seriesTitle" or "seasonTitle" or "year" or "season" or "episode"
                    or "episodeEnd" or "edition" or "fileDate"))
                { Discard(path + ".field", "InvalidFieldValue", "supported evidence field", field["field"]); continue; }
                if (value is null || token is null || raw is null || !raw.Contains(token, StringComparison.Ordinal)
                    || !token.Contains(value, StringComparison.OrdinalIgnoreCase))
                { Discard(path, "UnsupportedEvidence", "literal value and token in identified source", field); continue; }
                if (key is "year" or "season" or "episode" or "episodeEnd"
                    && (!int.TryParse(value, out int number)
                        || !AiParseResultGuard.HasGroundedNumericEvidence(key, number, raw)))
                { Discard(path, "UnsupportedNumericEvidence", "grounded numeric field", field); continue; }
                if (key == "year" && int.TryParse(value, out int evidenceYear)
                    && !MediaYearEvidence.ContainsYear(sources, evidenceYear))
                { Discard(path, "UnsupportedNumericEvidence", "work year outside date or technical tokens", field); continue; }
                evidence.Add(new(key, value, source!, token, index));
            }
        }
        List<AiTitleVariant> titles = [];
        if (details["titleVariants"] is JsonArray variants)
            for (int i = 0; i < variants.Count; i++)
            {
                if (variants[i] is not JsonObject variant) continue;
                string path = $"$.details.titleVariants[{i}]";
                if (i >= 6) { Discard(path, "ItemLimitExceeded", "at most 6 items", variant); continue; }
                string? title = StringValue(variant["title"], path + ".title");
                if (title is null || !Visible(title))
                { Discard(path + ".title", "UnsupportedLiteral", "input-visible title", variant["title"]); continue; }
                titles.Add(new(title, StringValue(variant["language"], path + ".language", 16), "InputLiteral"));
            }
        string? date = Literal("fileDate");
        if (date is not null && !DateOnly.TryParseExact(date,
                ["yyyyMMdd", "yyyy-MM-dd", "yyyy.MM.dd", "yyyy/MM/dd", "yyyy_MM_dd"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        { Discard("$.details.fileDate", "InvalidFieldValue", "calendar date", details["fileDate"]); date = null; }
        string? kind = StringValue(details["contentKind"], "$.details.contentKind", 16);
        if (kind is not null && kind is not ("episode" or "movie" or "special" or "ova" or "oad" or "recap" or "unknown"))
        { Discard("$.details.contentKind", "InvalidFieldValue", "episode|movie|special|ova|oad|recap|unknown", details["contentKind"]); kind = null; }
        string[] uncertain = Strings("uncertainFields", allow: field => field is "title" or "year"
            or "type" or "season" or "episode" or "episodeEnd" or "edition" or "seasonTitle");
        // 冲突说明是模型判断，只作人工可见的说明；不参与已知字段或候选身份裁决。
        return new(Literal("seriesTitle"), Literal("seasonTitle"), kind, titles,
            Strings("editionTags", allow: Visible, code: "UnsupportedLiteral"), evidence, uncertain,
            Strings("conflicts", 4), date);
    }
}
