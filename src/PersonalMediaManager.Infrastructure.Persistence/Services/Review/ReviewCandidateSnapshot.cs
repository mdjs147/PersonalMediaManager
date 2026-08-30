using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Review;

/// <summary>审核候选快照（落 Media_Item.TmdbCandidatesJson 列）</summary>
/// <remarks>
/// 解析阶段（ProcessFileService）转 AwaitingReview 前把 TMDB 候选全集序列化进此结构；
/// 审核列表（ReviewService.ListAsync）反序列化还原为候选供人工单选。
/// 字段为前端展示所需最小集：PosterPath 存 TMDB 相对路径，由 ListAsync 拼成完整 URL。
/// 序列化/反序列化收口本类型，保证两侧 JSON 形状一致（camelCase + 省 null + CJK 不转义）。
/// </remarks>
internal sealed record ReviewCandidateSnapshot(
    int TmdbId,
    string MediaType,
    string? Title,
    string? OriginalTitle,
    int? Year,
    string? PosterPath)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>序列化候选列表为 JSON；空列表返回 null（不落空数组字符串）</summary>
    public static string? Serialize(IReadOnlyList<ReviewCandidateSnapshot> items)
        => items.Count == 0 ? null : JsonSerializer.Serialize(items, JsonOptions);

    /// <summary>反序列化；空 / 损坏一律返回空列表（由调用方回退旧逻辑）</summary>
    public static IReadOnlyList<ReviewCandidateSnapshot> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<ReviewCandidateSnapshot>();
        try
        {
            return JsonSerializer.Deserialize<List<ReviewCandidateSnapshot>>(json, JsonOptions)
                ?? (IReadOnlyList<ReviewCandidateSnapshot>)Array.Empty<ReviewCandidateSnapshot>();
        }
        catch (JsonException)
        {
            return Array.Empty<ReviewCandidateSnapshot>();
        }
    }
}
