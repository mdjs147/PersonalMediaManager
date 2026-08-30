namespace PersonalMediaManager.Domain.Entities;

/// <summary>TMDB 类型维度（Media_Genre，如剧情/动作）</summary>
/// <remarks>PK = TMDB genre id；不继承 Entity；跨作品共享，支撑按类型检索。</remarks>
public sealed class MediaGenre : IMediaDimension
{
    /// <summary>TMDB 类型 ID（主键）</summary>
    public int Id { get; set; }

    public string Name { get; set; } = default!;
}
