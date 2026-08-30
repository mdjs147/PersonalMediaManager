namespace PersonalMediaManager.Domain.Aggregates.MediaWorks;

/// <summary>作品-电视台连接（Media_WorkNetwork）— 复合主键 (WorkId, NetworkId)</summary>
/// <remarks>MediaWork 聚合内子实体，由 ReplaceNetworks 重建；NetworkId → Media_Network；仅剧集有。</remarks>
public sealed class MediaWorkNetwork
{
    private MediaWorkNetwork() { }

    internal MediaWorkNetwork(long workId, int networkId)
    {
        WorkId = workId;
        NetworkId = networkId;
    }

    public long WorkId { get; private set; }

    public int NetworkId { get; private set; }
}
