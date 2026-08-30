using System.Text.Json;
using System.Text.Json.Serialization;

namespace PersonalMediaManager.Domain.Aggregates.MediaItems;

/// <summary>解析结果值对象（落到 MediaItem.ParsedInfo JSON 列）</summary>
/// <remarks>
/// 收口 JSON 序列化/反序列化与字段合并逻辑，避免 Service 层裸 JsonSerializer 拼对象、
/// 写入畸形 JSON 的风险（旧实现 ProcessFileService L226 / ReviewService.MergeOrCreateParsedInfo）。
///
/// Original{Season,Episode,EpisodeEnd}：翻译前的「源文件名命名空间」季集。仅剧集组（episode_group）
/// 翻译重排路径会写入——此时 Season/Episode 已是 TMDB 正典季集，而磁盘上的视频 / 字幕文件名仍带编组内
/// 原始集号。归档阶段用 Original* 做字幕等伴生文件归属匹配（与源文件名同一命名空间），落地重命名仍用正典
/// Season/Episode。普通路径三者均为 null，归档侧回退正典季集，行为与新增字段前完全一致。
/// </remarks>
public sealed record ParsedInfo(
    string? Title,
    int? Year,
    string Type,
    int? Season,
    int? Episode,
    int? EpisodeEnd,
    long? MatchedRuleId,
    string? SeasonTitle = null,
    // 源文件名命名空间的季集（翻译前原始值；仅剧集组重排路径非空，供字幕归属匹配，见类型 remarks）
    int? OriginalSeason = null,
    int? OriginalEpisode = null,
    int? OriginalEpisodeEnd = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>从旧 JSON 反序列化；损坏返回 null（由 caller 决定如何兜底）。旧记录无 episodeEnd 字段时自动为 null。</summary>
    public static ParsedInfo? FromJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            return JsonSerializer.Deserialize<ParsedInfo>(raw, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>用客户端覆盖字段并入既有 ParsedInfo；缺字段保留原值</summary>
    /// <remarks>
    /// Original*（源文件名命名空间季集）一律保留原值：客户端只覆盖正典季集，源文件名不会因人工改标而变，
    /// 故剧集组记录被人工审核 / 改字段后，字幕归属仍能按原始集号匹配（与 MatchedRuleId 同属「不被覆盖」字段）。
    /// </remarks>
    public ParsedInfo MergeWith(string? title, int? year, string mediaType, int? season, int? episode, int? episodeEnd = null, string? seasonTitle = null)
        => new(
            title ?? Title,
            year ?? Year,
            mediaType.ToLowerInvariant(),
            season ?? Season,
            episode ?? Episode,
            episodeEnd ?? EpisodeEnd,
            MatchedRuleId,
            seasonTitle ?? SeasonTitle,
            OriginalSeason,
            OriginalEpisode,
            OriginalEpisodeEnd);

    /// <summary>用客户端覆盖字段创建新 ParsedInfo（无原值兜底）</summary>
    public static ParsedInfo CreateFromOverride(string? title, int? year, string mediaType, int? season, int? episode, int? episodeEnd = null, long? matchedRuleId = null, string? seasonTitle = null)
        => new(title, year, mediaType.ToLowerInvariant(), season, episode, episodeEnd, matchedRuleId, seasonTitle);
}
