using PersonalMediaManager.Infrastructure.External.Ai;

namespace PersonalMediaManager.Infrastructure.External.Tests.Ai;

/// <summary>AiPromptHelpers.JoinUrl 智能去重单测 —— 容忍用户把 BaseUrl 多填 /v1 / 整条端点 / 大小写 / 尾斜杠</summary>
/// <remarks>
/// 核心诉求（用户反馈）：BaseUrl 到底写不写 /v1 都不该报错。JoinUrl 按「路径段重叠」去重，
/// 兼容只填域名、带 /v1、带整条端点、/V1 大小写、尾斜杠，并保留中转代理的前缀路径；
/// 非合法绝对 URL 退回朴素拼接（行为不变）。
/// </remarks>
public sealed class AiUrlJoinTests
{
    [Theory]
    // ── Anthropic：/v1/messages ──
    [InlineData("https://api.anthropic.com", "/v1/messages", "https://api.anthropic.com/v1/messages")]
    [InlineData("https://api.anthropic.com/", "/v1/messages", "https://api.anthropic.com/v1/messages")]          // 尾斜杠
    [InlineData("https://api.anthropic.com/v1", "/v1/messages", "https://api.anthropic.com/v1/messages")]        // 多填 /v1
    [InlineData("https://api.anthropic.com/v1/", "/v1/messages", "https://api.anthropic.com/v1/messages")]       // /v1 + 尾斜杠
    [InlineData("https://api.anthropic.com/v1/messages", "/v1/messages", "https://api.anthropic.com/v1/messages")] // 整条端点
    [InlineData("https://api.anthropic.com/V1", "/v1/messages", "https://api.anthropic.com/v1/messages")]        // 大小写纠正
    [InlineData("http://host/relay/v1", "/v1/messages", "http://host/relay/v1/messages")]                       // 中转前缀保留
    [InlineData("http://host/proxy/anthropic", "/v1/messages", "http://host/proxy/anthropic/v1/messages")]      // 无重叠全保留
    // ── OpenAI 兼容：/v1/chat/completions ──
    [InlineData("https://api.deepseek.com", "/v1/chat/completions", "https://api.deepseek.com/v1/chat/completions")]
    [InlineData("https://api.deepseek.com/v1", "/v1/chat/completions", "https://api.deepseek.com/v1/chat/completions")]
    // ── Ollama：/api/chat ──
    [InlineData("http://localhost:11434", "/api/chat", "http://localhost:11434/api/chat")]
    [InlineData("http://localhost:11434/api", "/api/chat", "http://localhost:11434/api/chat")]                  // 多填 /api
    // ── Gemini：/v1beta/models/{model}:generateContent ──
    [InlineData("https://generativelanguage.googleapis.com", "/v1beta/models/gemini-1.5-flash:generateContent", "https://generativelanguage.googleapis.com/v1beta/models/gemini-1.5-flash:generateContent")]
    [InlineData("https://generativelanguage.googleapis.com/v1beta", "/v1beta/models/gemini-1.5-flash:generateContent", "https://generativelanguage.googleapis.com/v1beta/models/gemini-1.5-flash:generateContent")]
    // ── 非合法绝对 URL：退回朴素拼接（不冒险改写） ──
    [InlineData("not-a-url", "/v1/messages", "not-a-url/v1/messages")]
    public void JoinUrl_DedupesOverlappingSegments(string baseUrl, string path, string expected)
    {
        AiPromptHelpers.JoinUrl(baseUrl, path).Should().Be(expected);
    }

    [Theory]
    // 版本槽合并：两端都是版本号段但不同 → 保留用户显式版本，仅追加 path 操作段（绝不强塞 /v1）
    [InlineData("https://relay.com/v2", "/v1/chat/completions", "https://relay.com/v2/chat/completions")]
    [InlineData("https://relay.com/v3", "/v1/chat/completions", "https://relay.com/v3/chat/completions")]
    [InlineData("https://host/openai/v2", "/v1/chat/completions", "https://host/openai/v2/chat/completions")] // 前缀 + 版本一起保留
    // Gemini：用户显式 /v1 覆盖默认 /v1beta（/v1/models/... 是有效 Gemini 端点，用户版本优先）
    [InlineData("https://generativelanguage.googleapis.com/v1", "/v1beta/models/m:generateContent", "https://generativelanguage.googleapis.com/v1/models/m:generateContent")]
    // 非版本号前缀（openai / messages 等）不参与版本槽合并 → 照常追加 path 规范段（含默认 /v1）
    [InlineData("https://host/openai", "/v1/chat/completions", "https://host/openai/v1/chat/completions")]
    public void JoinUrl_VersionSlotMerge_PreservesUserVersion(string baseUrl, string path, string expected)
    {
        AiPromptHelpers.JoinUrl(baseUrl, path).Should().Be(expected);
    }
}
