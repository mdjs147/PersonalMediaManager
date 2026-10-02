using System.Text.Json;
using PersonalMediaManager.Application.Contracts;

namespace PersonalMediaManager.Infrastructure.External.Ai;

internal static partial class AiPromptHelpers
{
    /// <summary>重复键拒绝为可预期结构失败，避免 JsonObject 延迟物化抛异常或静默取末值</summary>
    private static void RejectDuplicateProperties(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new AiProviderLogicalException("AI 返回必须是 JSON 对象");
        Visit(document.RootElement);

        static void Visit(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                HashSet<string> names = new(StringComparer.Ordinal);
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new AiProviderLogicalException("AI 返回 JSON 含重复属性，拒绝歧义结构");
                    Visit(property.Value);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (JsonElement item in value.EnumerateArray()) Visit(item);
        }
    }
}
