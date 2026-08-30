namespace PersonalMediaManager.Domain.Entities;

/// <summary>TMDB 电视台维度（Media_Network，剧集播出网络）</summary>
/// <remarks>PK = TMDB network id；不继承 Entity；仅剧集有；与 Media_Company 分表（TMDB id 命名空间不同）。</remarks>
public sealed class MediaNetwork : IMediaDimension
{
    /// <summary>TMDB 电视台 ID（主键）</summary>
    public int Id { get; set; }

    public string Name { get; set; } = default!;

    /// <summary>电视台 Logo 路径（TMDB logo_path）</summary>
    public string? LogoPath { get; set; }

    /// <summary>所属国家（ISO 3166-1）</summary>
    public string? OriginCountry { get; set; }
}
