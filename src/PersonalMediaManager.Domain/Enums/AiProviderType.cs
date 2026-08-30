namespace PersonalMediaManager.Domain.Enums;

/// <summary>AI 提供商协议类型（Parse_AiProvider.Type）</summary>
/// <remarks>
/// <b>值 = wire 协议，品牌降为预设</b>：枚举不再按厂商（Qwen / DeepSeek）拆分，而是按 HTTP 协议形状归类。
/// 同一协议下的不同厂商（如 Qwen / DeepSeek / OpenAI 均走 OpenAI 兼容协议）共用一个协议策略实现，
/// 厂商差异沉淀为「预设」（预填 BaseUrl / Model / 是否免费 等），不再各占一个枚举值与一个实现类。
///
/// 数据库以字符串存储（"Ollama" / "OpenAiCompatible" / "Anthropic" / "Gemini" / "AzureOpenAi"），EF 配置 HasConversion。
/// 新增协议时需同步：①Enums 加值 ②Infrastructure.External/Ai/Protocols/ 加 IAiProtocol 实现 ③协议工厂路由。
/// </remarks>
public enum AiProviderType
{
    /// <summary>Ollama 本地协议（POST /api/chat，format=json，无鉴权）</summary>
    Ollama = 1,

    /// <summary>OpenAI 兼容协议（POST /v1/chat/completions，覆盖 OpenAI / Qwen / DeepSeek / 第三方代理）</summary>
    OpenAiCompatible = 2,

    /// <summary>Anthropic Messages 协议</summary>
    Anthropic = 3,

    /// <summary>Google Gemini 协议</summary>
    Gemini = 4,

    /// <summary>Azure OpenAI 协议（deployment 路由 + api-version）</summary>
    AzureOpenAi = 5,
}
