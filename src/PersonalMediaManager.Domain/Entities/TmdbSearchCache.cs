namespace PersonalMediaManager.Domain.Entities;

/// <summary>TMDB 搜索结果缓存（Tmdb_SearchCache，短期 60 分钟）</summary>
/// <remarks>
/// PK = QueryHash（SHA256(query + type + year + language)）；不继承 Entity；Results 列存 JSON 数组。
/// QueryRaw 仅排错可读；生产环境通过 QueryHash 等值命中即可。
/// </remarks>
public sealed class TmdbSearchCache
{
    /// <summary>查询哈希（PK，SHA256 十六进制 64 字符）</summary>
    public string QueryHash { get; set; } = default!;

    /// <summary>原始查询字符串（排错可读）</summary>
    public string? QueryRaw { get; set; }

    /// <summary>JSON 数组（TMDB 候选列表）</summary>
    public string Results { get; set; } = default!;

    public DateTimeOffset CachedAt { get; set; }
}
