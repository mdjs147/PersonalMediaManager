namespace PersonalMediaManager.Domain.Aggregates.MediaWorks;

/// <summary>作品-关键词连接（Media_WorkKeyword）— 复合主键 (WorkId, KeywordId)</summary>
/// <remarks>MediaWork 聚合内子实体，由 ReplaceKeywords 重建；KeywordId → Media_Keyword。</remarks>
public sealed class MediaWorkKeyword
{
    private MediaWorkKeyword() { }

    internal MediaWorkKeyword(long workId, int keywordId)
    {
        WorkId = workId;
        KeywordId = keywordId;
    }

    public long WorkId { get; private set; }

    public int KeywordId { get; private set; }
}
