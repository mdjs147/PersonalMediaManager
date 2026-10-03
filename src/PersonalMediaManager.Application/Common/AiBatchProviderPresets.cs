using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PersonalMediaManager.Application.Dtos.Ai;
using PersonalMediaManager.Domain.Enums;
namespace PersonalMediaManager.Application.Common;

/// <summary>仅匹配已核实的官方模型能力</summary>
public static class AiBatchProviderPresets
{
    public static string ConfigurationKey(AiProviderType protocol, string baseUrl, string model) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { protocol, baseUrl, model }))));

    public static AiBatchProviderInfoDto Describe(long providerId, string name, AiProviderType protocol, string baseUrl, string model)
    {
        string key = ConfigurationKey(protocol, baseUrl, model);
        bool official = Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri)
            && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase)
            && uri.Port == 443 && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
            && protocol == AiProviderType.OpenAiCompatible && (uri.AbsolutePath.TrimEnd('/') is "" or "/v1")
            && model == "deepseek-flash";
        // 官方模型表：context_window=1048576、max_output_tokens=393216；产品输出硬界更低。
        // 来源：https://api-docs.deepseek.com/api/list-models/ （2026-10-03 核查）
        return new(providerId, name, model, key,
            official ? new() { ProviderId = providerId, ConfigurationKey = key, BatchSize = 32, DisableThinking = true,
                ContextTokenBudget = 65536, MaxOutputTokens = 32768, MaxResponseBytes = 1048576 } : null,
            official ? new() { ProviderId = providerId, ConfigurationKey = key, BatchSize = 128, DisableThinking = true,
                ContextTokenBudget = 131072, MaxOutputTokens = 65536, MaxResponseBytes = 2097152 } : null);
    }
}
