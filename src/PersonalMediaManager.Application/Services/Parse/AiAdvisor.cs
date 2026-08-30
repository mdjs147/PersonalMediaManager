using System.Globalization;
using System.Text;
using System.Text.Json;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Application.Services.Parse;

/// <summary>测试集 AI 顾问实现（C1：Triage）</summary>
/// <remarks>
/// 拼 Triage prompt（system 约束仅输出 JSON） → IAiChatClient.CompleteAsync → 解析 JSON 到 TriageAdvice。
/// IAiChatClient 不可用（无 provider / 全失败）时抛 AiChatUnavailableException，本类转 BusinessException(1000)。
/// </remarks>
internal sealed class AiAdvisor : IAiAdvisor
{
    private const string TriageSystemPrompt =
        "你是媒体解析测试集管理助手。给定一个待判定的视频文件样本（含路径），以及现有测试集已有样本列表，" +
        "判断该样本是否值得作为「解析回归测试用例」纳入测试集。判断标准：样本具备独特命名模式、能暴露规则盲区 → 值得纳入；" +
        "与已有样本高度雷同（命名套路、剧名、来源组相同）→ 不值得（重复覆盖无增量价值）。" +
        "仅输出 JSON，禁止解释/Markdown/代码块：" +
        "{\"worthAdding\":true|false,\"reason\":\"简短中文理由\",\"similarity\":0.0-1.0," +
        "\"title\":\"标题\"|null,\"year\":YYYY|null,\"type\":\"movie\"|\"tv\"|null,\"season\":N|null,\"episode\":N|null}。" +
        "similarity 是与现有样本的相似度（越高越雷同）；title/year/type/season/episode 是你对该样本的解析建议，供人工确认期望基线时参考。";

    private const string SuggestRuleSystemPrompt =
        "你是 .NET 正则表达式专家，为媒体文件解析规则引擎生成一条解析规则。给定一个视频文件样本（路径）与期望解析结果，" +
        "生成一条 .NET 正则，使用命名捕获组 title / year / season / episode / episodeEnd 提取对应字段（按需，不必全有）。" +
        "正则要尽量通用——匹配同一套命名风格的一类文件，而非仅此一个文件名；避免把具体剧名写死进正则。" +
        "仅输出 JSON，禁止解释/Markdown/代码块：" +
        "{\"pattern\":\".NET正则\",\"scope\":\"FileName\"|\"ParentFolder\"|\"FullPath\"|\"AllAncestors\"|\"RelativePath\"," +
        "\"defaultType\":\"movie\"|\"tv\"|null,\"explanation\":\"简短中文说明这条正则如何匹配\"}。" +
        "scope 选择正则作用的输入范围：仅文件名→FileName；信息在父目录→ParentFolder；跨目录段→AllAncestors/RelativePath。";

    private readonly IAiChatClient _chat;

    public AiAdvisor(IAiChatClient chat)
    {
        _chat = chat;
    }

    public async Task<TriageAdvice> TriageAsync(TriageInput input, CancellationToken ct = default)
    {
        string userPrompt = BuildTriageUserPrompt(input);
        AiChatResult result;
        try
        {
            result = await _chat.CompleteAsync(new AiChatRequest(TriageSystemPrompt, userPrompt), ct);
        }
        catch (AiChatUnavailableException ex)
        {
            throw new BusinessException($"AI 暂不可用：{ex.Message}");
        }

        try
        {
            return ParseTriageAdvice(result.Content);
        }
        catch (JsonException ex)
        {
            throw new BusinessException($"AI 返回 JSON 无法解析：{ex.Message}");
        }
    }

    public async Task<RuleSuggestion> SuggestRuleAsync(SuggestRuleInput input, CancellationToken ct = default)
    {
        string userPrompt = BuildSuggestRuleUserPrompt(input);
        AiChatResult result;
        try
        {
            result = await _chat.CompleteAsync(new AiChatRequest(SuggestRuleSystemPrompt, userPrompt), ct);
        }
        catch (AiChatUnavailableException ex)
        {
            throw new BusinessException($"AI 暂不可用：{ex.Message}");
        }

        try
        {
            return ParseRuleSuggestion(result.Content);
        }
        catch (JsonException ex)
        {
            throw new BusinessException($"AI 返回 JSON 无法解析：{ex.Message}");
        }
    }

    private static string BuildSuggestRuleUserPrompt(SuggestRuleInput input)
    {
        StringBuilder sb = new();
        sb.Append("样本路径：").Append(input.SamplePath).Append('\n');
        if (!string.IsNullOrWhiteSpace(input.WatchRootPath))
            sb.Append("监控根：").Append(input.WatchRootPath).Append('\n');

        List<string> expects = new();
        if (!string.IsNullOrWhiteSpace(input.ExpectedTitle)) expects.Add($"title={input.ExpectedTitle}");
        if (input.ExpectedYear is int y) expects.Add($"year={y}");
        if (input.ExpectedMediaType is MediaType mt) expects.Add($"type={mt.ToString().ToLowerInvariant()}");
        if (input.ExpectedSeason is int s) expects.Add($"season={s}");
        if (input.ExpectedEpisode is int e) expects.Add($"episode={e}");
        sb.Append(expects.Count > 0
            ? "期望解析结果：" + string.Join(" ", expects)
            : "（无期望基线，请按你对该样本的判断生成正则）");
        return sb.ToString();
    }

    private static RuleSuggestion ParseRuleSuggestion(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        string? pattern = ReadString(root, "pattern");
        if (string.IsNullOrWhiteSpace(pattern))
            throw new BusinessException("AI 未返回正则 pattern");

        ParseScope scope = ReadString(root, "scope") switch
        {
            "ParentFolder" => ParseScope.ParentFolder,
            "FullPath" => ParseScope.FullPath,
            "AllAncestors" => ParseScope.AllAncestors,
            "RelativePath" => ParseScope.RelativePath,
            _ => ParseScope.FileName,
        };
        string? defaultType = ReadString(root, "defaultType")?.ToLowerInvariant() switch
        {
            "movie" => "movie",
            "tv" => "tv",
            _ => null,
        };
        string explanation = ReadString(root, "explanation") ?? "（AI 未给出说明）";
        if (explanation.Length > 480) explanation = explanation[..480];

        return new RuleSuggestion(pattern!, scope, defaultType, explanation);

        static string? ReadString(JsonElement r, string name) =>
            r.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static string BuildTriageUserPrompt(TriageInput input)
    {
        StringBuilder sb = new();
        sb.Append("待判定样本：").Append(input.SamplePath).Append('\n');
        if (!string.IsNullOrWhiteSpace(input.WatchRootPath))
            sb.Append("监控根：").Append(input.WatchRootPath).Append('\n');
        if (!string.IsNullOrWhiteSpace(input.FailureReason))
            sb.Append("历史失败原因：").Append(input.FailureReason).Append('\n');

        if (input.ExistingSamplePaths.Count == 0)
        {
            sb.Append("现有测试集：空（首批样本）");
        }
        else
        {
            sb.Append("现有测试集样本（节选，用于判重）：\n");
            foreach (string p in input.ExistingSamplePaths)
                sb.Append("- ").Append(p).Append('\n');
        }
        return sb.ToString();
    }

    private static TriageAdvice ParseTriageAdvice(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        bool worthAdding = root.TryGetProperty("worthAdding", out JsonElement w)
            && (w.ValueKind == JsonValueKind.True || (w.ValueKind == JsonValueKind.String && bool.TryParse(w.GetString(), out bool b) && b));
        string reason = ReadString(root, "reason") ?? "（AI 未给出理由）";
        if (reason.Length > 300) reason = reason[..300];
        double similarity = ReadDouble(root, "similarity") ?? 0;
        string? title = ReadString(root, "title");
        int? year = ReadInt(root, "year");
        int? season = ReadInt(root, "season");
        int? episode = ReadInt(root, "episode");
        MediaType? mediaType = ReadString(root, "type")?.ToLowerInvariant() switch
        {
            "movie" => MediaType.Movie,
            "tv" => MediaType.Tv,
            _ => null,
        };
        // movie 清空季集（与解析 helper 一致）
        if (mediaType == MediaType.Movie)
        {
            season = null;
            episode = null;
        }

        return new TriageAdvice(
            worthAdding,
            reason,
            Math.Clamp(similarity, 0, 1),
            string.IsNullOrWhiteSpace(title) ? null : title,
            year,
            mediaType,
            season,
            episode);

        static string? ReadString(JsonElement r, string name) =>
            r.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static int? ReadInt(JsonElement r, string name)
        {
            if (!r.TryGetProperty(name, out JsonElement v)) return null;
            return v.ValueKind switch
            {
                JsonValueKind.Number when v.TryGetInt32(out int i) => i,
                JsonValueKind.String when int.TryParse(v.GetString(), out int i2) => i2,
                _ => null,
            };
        }
        static double? ReadDouble(JsonElement r, string name)
        {
            if (!r.TryGetProperty(name, out JsonElement v)) return null;
            return v.ValueKind switch
            {
                JsonValueKind.Number when v.TryGetDouble(out double d) => d,
                JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d2) => d2,
                _ => null,
            };
        }
    }
}
