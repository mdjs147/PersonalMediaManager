namespace PersonalMediaManager.Domain.Aggregates.MediaWorks;

/// <summary>作品-人员关联（Media_WorkCredit）— MediaWork 聚合内子实体</summary>
/// <remarks>
/// 演员与剧组合一表，CreditType 区分：cast 用 Character + Ord（演员表序号）；crew 用 Job + Department。
/// 合并的好处：「按某人检索全库作品」只需 Where(PersonId==x) 单条件，同时命中其出演与参与制作。
/// 由 MediaWork.ReplaceCredits 重建，禁止应用层直接 new（聚合纪律，参照 ProcessStep）。
/// </remarks>
public sealed class MediaWorkCredit
{
    /// <summary>EF Core 反射构造</summary>
    private MediaWorkCredit() { }

    /// <summary>仅由 MediaWork.ReplaceCredits 内部调用</summary>
    internal MediaWorkCredit(long workId, int personId, string creditType, string? character, int? ord, string? job, string? department)
    {
        WorkId = workId;
        PersonId = personId;
        CreditType = creditType;
        Character = character;
        Ord = ord;
        Job = job;
        Department = department;
    }

    public long Id { get; private set; }

    /// <summary>所属作品（FK CASCADE）</summary>
    public long WorkId { get; private set; }

    /// <summary>关联人员（FK → Media_Person）</summary>
    public int PersonId { get; private set; }

    /// <summary>cast / crew</summary>
    public string CreditType { get; private set; } = default!;

    /// <summary>角色名（cast）</summary>
    public string? Character { get; private set; }

    /// <summary>演员表序号（cast，越小越靠前）</summary>
    public int? Ord { get; private set; }

    /// <summary>职务（crew，如 Director）</summary>
    public string? Job { get; private set; }

    /// <summary>部门（crew，如 Directing）</summary>
    public string? Department { get; private set; }
}
