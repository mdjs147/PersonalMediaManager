namespace PersonalMediaManager.Domain.Aggregates.MediaWorks;

/// <summary>作品-制作公司连接（Media_WorkCompany）— 复合主键 (WorkId, CompanyId)</summary>
/// <remarks>MediaWork 聚合内子实体，由 ReplaceCompanies 重建；CompanyId → Media_Company。</remarks>
public sealed class MediaWorkCompany
{
    private MediaWorkCompany() { }

    internal MediaWorkCompany(long workId, int companyId)
    {
        WorkId = workId;
        CompanyId = companyId;
    }

    public long WorkId { get; private set; }

    public int CompanyId { get; private set; }
}
