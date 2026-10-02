using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PersonalMediaManager.Application.Contracts;

namespace PersonalMediaManager.Infrastructure.External.Ai;

internal static partial class AiPromptHelpers
{
    /// <summary>原始 v2 字段先诊断再清洗，不从错层对象猜测值</summary>
    private static List<AiSchemaIssue> ValidateTaskSchema(JsonObject root, AiParseContext context)
    {
        List<AiSchemaIssue> issues = [];
        void Issue(string path, string code, string expected, JsonNode? node, bool blocks = false) =>
            issues.Add(new(path, code, expected, SchemaNodeType(node), blocks));
        void StringField(JsonObject obj, string field, string path, bool blocks = false)
        {
            if (obj[field] is null) return;
            if (obj[field] is JsonValue value && value.TryGetValue<string>(out _)) return;
            Issue(path, "InvalidFieldShape", "string|null", obj[field], blocks);
            obj[field] = null;
        }
        void IntegerField(JsonObject obj, string field, string path, int min, int max, bool blocks = false)
        {
            if (obj[field] is null) return;
            JsonNode? node = obj[field];
            int number;
            if (node is JsonValue value && value.TryGetValue<int>(out number)) { }
            else if (node is JsonValue textValue && textValue.TryGetValue<string>(out string? text)
                && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            {
                Issue(path, "CoercedNumericString", "integer|null", node);
                obj[field] = number;
            }
            else
            {
                Issue(path, "InvalidFieldShape", "integer|null", node, blocks);
                obj[field] = null;
                return;
            }
            if (number < min || number > max)
            {
                Issue(path, "InvalidFieldRange", $"integer[{min},{max}]|null", node, blocks);
                obj[field] = null;
            }
        }
        void UnknownFields(JsonObject obj, string path, params string[] known)
        {
            foreach (string key in obj.Select(pair => pair.Key).Where(key => !known.Contains(key)).ToArray())
            {
                Issue(path + "." + RuneLimit(key, 80), "UnknownExtension", "known field", obj[key]);
                obj.Remove(key);
            }
        }
        void ArrayField(JsonObject obj, string field, string path, bool objects)
        {
            if (obj[field] is null) return;
            if (obj[field] is not JsonArray array)
            {
                Issue(path, "InvalidFieldShape", objects ? "object[]|null" : "string[]|null", obj[field]);
                obj[field] = null;
                return;
            }
            for (int i = 0; i < array.Count; i++)
            {
                bool valid = objects ? array[i] is JsonObject
                    : array[i] is JsonValue value && value.TryGetValue<string>(out _);
                if (!valid)
                {
                    Issue($"{path}[{i}]", "InvalidFieldShape", objects ? "object" : "string", array[i]);
                    array[i] = null;
                }
            }
        }

        UnknownFields(root, "$", "title", "year", "type", "season", "episode", "episodeEnd", "confidence", "aliases", "selectedCandidateId", "abstain", "details");
        StringField(root, "title", "$.title", true);
        StringField(root, "type", "$.type", true);
        if (root["type"] is JsonValue typeValue && typeValue.TryGetValue<string>(out string? type)
            && type is not ("movie" or "tv" or "unknown"))
        {
            Issue("$.type", "InvalidFieldValue", "movie|tv|unknown|null", root["type"], true);
            root["type"] = null;
        }
        if (root.ContainsKey("abstain") && !(root["abstain"] is JsonValue flag && flag.TryGetValue<bool>(out _)))
        {
            Issue("$.abstain", "InvalidFieldShape", "boolean", root["abstain"], true);
            root["abstain"] = null;
        }
        if (root.ContainsKey("confidence"))
        {
            if (!(root["confidence"] is JsonValue confidence && confidence.GetValueKind() == JsonValueKind.Number
                && confidence.TryGetValue<double>(out double number) && double.IsFinite(number)))
            {
                Issue("$.confidence", "InvalidFieldShape", "finite number[0,1]", root["confidence"], true);
                root["confidence"] = 0;
            }
            else if (number is < 0 or > 1)
            {
                Issue("$.confidence", "InvalidConfidence", "number[0,1]", root["confidence"], true);
                root["confidence"] = 0;
            }
        }
        IntegerField(root, "year", "$.year", 1900, 2100);
        IntegerField(root, "season", "$.season", 0, 99);
        IntegerField(root, "episode", "$.episode", 0, 9999);
        IntegerField(root, "episodeEnd", "$.episodeEnd", 0, 9999);
        if (ReadInt(root["episodeEnd"]) is int end && ReadInt(root["episode"]) is int start && end < start)
        {
            Issue("$.episodeEnd", "InvalidEpisodeRange", "integer>=episode|null", root["episodeEnd"]);
            root["episodeEnd"] = null;
        }
        IntegerField(root, "selectedCandidateId", "$.selectedCandidateId", 1, int.MaxValue,
            context.TaskType == AiParseTaskType.DisambiguateCandidates);
        if (root["selectedCandidateId"] is not null && context.TaskType == AiParseTaskType.IdentifyWork
            && context.LockedBinding is null && (context.Candidates?.Count ?? 0) == 0)
        {
            Issue("$.selectedCandidateId", "UnrequestedField", "null without candidate shortlist", root["selectedCandidateId"]);
            root["selectedCandidateId"] = null;
        }
        ArrayField(root, "aliases", "$.aliases", false);
        if (root["aliases"] is JsonArray aliases)
        {
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            if (root["title"] is JsonValue title && title.TryGetValue<string>(out string? titleText)
                && !string.IsNullOrWhiteSpace(titleText)) seen.Add(titleText.Trim());
            int kept = 0;
            for (int i = 0; i < aliases.Count; i++)
            {
                if (aliases[i] is not JsonValue alias || !alias.TryGetValue<string>(out string? text)) continue;
                string path = $"$.aliases[{i}]";
                string trimmed = text?.Trim() ?? "";
                string limited = RuneLimit(trimmed, 200);
                string? code = trimmed.Length == 0 ? "EmptyField" : !seen.Add(limited) ? "DuplicateItem"
                    : kept >= 3 ? "ItemLimitExceeded" : null;
                if (code is not null) { Issue(path, code, "at most 3 unique nonempty aliases", alias); aliases[i] = null; }
                else
                {
                    if (limited != trimmed) Issue(path, "TruncatedField", "string<=200 runes", alias);
                    aliases[i] = limited;
                    kept++;
                }
            }
        }
        if (root["details"] is not null && root["details"] is not JsonObject)
        {
            Issue("$.details", "InvalidFieldShape", "object|null", root["details"]);
            root["details"] = null;
        }
        if (root["details"] is JsonObject details)
        {
            UnknownFields(details, "$.details", "seriesTitle", "seasonTitle", "contentKind", "titleVariants", "editionTags", "fieldEvidence", "uncertainFields", "conflicts", "fileDate");
            foreach (string field in new[] { "seriesTitle", "seasonTitle", "contentKind", "fileDate" })
                StringField(details, field, "$.details." + field);
            foreach (string field in new[] { "editionTags", "uncertainFields", "conflicts" })
                ArrayField(details, field, "$.details." + field, false);
            foreach (string field in new[] { "titleVariants", "fieldEvidence" })
                ArrayField(details, field, "$.details." + field, true);
            if (details["titleVariants"] is JsonArray titles)
                for (int i = 0; i < titles.Count; i++)
                    if (titles[i] is JsonObject title)
                    {
                        string path = $"$.details.titleVariants[{i}]";
                        UnknownFields(title, path, "title", "language", "source");
                        foreach (string field in new[] { "title", "language", "source" }) StringField(title, field, path + "." + field);
                    }
            if (details["fieldEvidence"] is JsonArray evidence)
                for (int i = 0; i < evidence.Count; i++)
                    if (evidence[i] is JsonObject entry)
                    {
                        string path = $"$.details.fieldEvidence[{i}]";
                        UnknownFields(entry, path, "field", "value", "source", "token", "segmentIndex");
                        foreach (string field in new[] { "field", "value", "source", "token" }) StringField(entry, field, path + "." + field);
                        IntegerField(entry, "segmentIndex", path + ".segmentIndex", 0, int.MaxValue);
                    }
        }
        return issues;
    }

    private static string SchemaNodeType(JsonNode? node) => node?.GetValueKind() switch
    {
        JsonValueKind.Object => "object", JsonValueKind.Array => "array", JsonValueKind.String => "string",
        JsonValueKind.Number => "number", JsonValueKind.True or JsonValueKind.False => "boolean", _ => "null"
    };
}
