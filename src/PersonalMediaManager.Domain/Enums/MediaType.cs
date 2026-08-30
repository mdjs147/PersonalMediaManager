namespace PersonalMediaManager.Domain.Enums;

/// <summary>媒体类型</summary>
/// <remarks>
/// 数据库表 Media_Item.TmdbMediaType / Category_Definition.MediaType 使用字符串存储（"movie" / "tv" / "Both"），
/// EF 配置 HasConversion 将枚举 ↔ string 互转，保留人类可读性。
/// Both 仅 Category_Definition 使用（如纪录片可既是电影也是剧集），Media_Item 不允许 Both。
/// </remarks>
public enum MediaType
{
    /// <summary>电影</summary>
    Movie = 1,

    /// <summary>剧集（含动漫）</summary>
    Tv = 2,

    /// <summary>电影 + 剧集（仅 Category_Definition 使用）</summary>
    Both = 3,
}
