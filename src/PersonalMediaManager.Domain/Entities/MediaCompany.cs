namespace PersonalMediaManager.Domain.Entities;

/// <summary>TMDB 制作公司维度（Media_Company，厂家）</summary>
/// <remarks>PK = TMDB company id；不继承 Entity；跨作品共享，支撑按公司检索。</remarks>
public sealed class MediaCompany : IMediaDimension
{
    /// <summary>TMDB 公司 ID（主键）</summary>
    public int Id { get; set; }

    public string Name { get; set; } = default!;

    /// <summary>公司 Logo 路径（TMDB logo_path）</summary>
    public string? LogoPath { get; set; }

    /// <summary>所属国家（ISO 3166-1）</summary>
    public string? OriginCountry { get; set; }
}
