namespace PersonalMediaManager.Domain.Entities;

/// <summary>TMDB 人员维度（Media_Person，演员/剧组共享）</summary>
/// <remarks>
/// PK = TMDB person id（不自增，由富化服务显式 upsert）；不继承 Entity（无时间戳）。
/// 跨作品共享：同一演员在多部作品出现仅一行，支撑「按演员检索全库作品」的 join。
/// </remarks>
public sealed class MediaPerson
{
    /// <summary>TMDB 人员 ID（主键）</summary>
    public int Id { get; set; }

    public string Name { get; set; } = default!;

    /// <summary>头像路径（TMDB profile_path，相对路径，前端走图片代理）</summary>
    public string? ProfilePath { get; set; }

    /// <summary>主要部门（Acting / Directing / Writing…）</summary>
    public string? KnownForDepartment { get; set; }
}
