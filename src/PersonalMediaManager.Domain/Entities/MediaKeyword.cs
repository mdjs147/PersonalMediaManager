namespace PersonalMediaManager.Domain.Entities;

/// <summary>TMDB 关键词/标签维度（Media_Keyword）</summary>
/// <remarks>PK = TMDB keyword id；不继承 Entity；跨作品共享，支撑按标签检索与关联。</remarks>
public sealed class MediaKeyword : IMediaDimension
{
    /// <summary>TMDB 关键词 ID（主键）</summary>
    public int Id { get; set; }

    public string Name { get; set; } = default!;
}
