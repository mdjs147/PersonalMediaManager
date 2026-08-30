namespace PersonalMediaManager.Domain.Aggregates.MediaWorks;

/// <summary>作品-类型连接（Media_WorkGenre）— 复合主键 (WorkId, GenreId)</summary>
/// <remarks>MediaWork 聚合内子实体，由 ReplaceGenres 重建；GenreId → Media_Genre。</remarks>
public sealed class MediaWorkGenre
{
    private MediaWorkGenre() { }

    internal MediaWorkGenre(long workId, int genreId)
    {
        WorkId = workId;
        GenreId = genreId;
    }

    public long WorkId { get; private set; }

    public int GenreId { get; private set; }
}
