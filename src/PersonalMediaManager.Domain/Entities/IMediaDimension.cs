namespace PersonalMediaManager.Domain.Entities;

/// <summary>媒体维度公共形态（Id + Name）— 供筛选项统计泛型复用</summary>
/// <remarks>Media_Genre / Media_Company / Media_Network / Media_Keyword 共同实现，使 FacetJoin 泛型可直接读 Id/Name（避免 EF.Property 反射式投影）。</remarks>
public interface IMediaDimension
{
    int Id { get; }
    string Name { get; }
}
