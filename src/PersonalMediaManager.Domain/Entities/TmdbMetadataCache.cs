namespace PersonalMediaManager.Domain.Entities;

/// <summary>TMDB 元数据缓存（Tmdb_MetadataCache）</summary>
/// <remarks>
/// 复合 PK (TmdbId, MediaType)；不继承 Entity（无 Id 列）；CachedAt 由 TmdbSearchService 设置，不走 TimestampInterceptor。
/// RawJson 无长度限制（SQLite TEXT），保留 TMDB 原始响应避免字段丢失。
/// Genres / OriginCountry 列存 JSON 字符串，EF 走 ValueConverter + ValueComparer（A4.5）。
/// </remarks>
public sealed class TmdbMetadataCache
{
    /// <summary>TMDB 资源 ID</summary>
    public int TmdbId { get; set; }

    /// <summary>movie / tv（小写 TMDB 原值）</summary>
    public string MediaType { get; set; } = default!;

    public string? Title { get; set; }
    public string? OriginalTitle { get; set; }
    public int? Year { get; set; }
    public int? TotalSeasons { get; set; }
    public string? PosterPath { get; set; }

    /// <summary>JSON 数组 ["CN","HK"]</summary>
    public List<string>? OriginCountry { get; set; }

    /// <summary>ISO 639-1</summary>
    public string? OriginalLanguage { get; set; }

    /// <summary>JSON 数组 [{"id":18,"name":"Drama"}]</summary>
    public string? Genres { get; set; }

    public string? Overview { get; set; }

    /// <summary>原始 TMDB 响应（无长度限制）</summary>
    public string? RawJson { get; set; }

    /// <summary>缓存写入时间（UTC，用于 TTL 判定）</summary>
    public DateTimeOffset CachedAt { get; set; }
}
