namespace PersonalMediaManager.Domain.Common;

/// <summary>带 CreatedAt + UpdatedAt 的实体标记接口</summary>
/// <remarks>
/// TimestampInterceptor 通过接口判断哪些实体需要自动维护时间戳；
/// 含 Id 的常规实体由 Entity 基类实现；非 Id 主键的特殊表（SystemSetting）单独显式实现。
/// 仅有 CachedAt 的缓存表（TmdbMetadataCache / TmdbSearchCache）不实现本接口，由各自配置类管理。
/// </remarks>
public interface IHasTimestamps
{
    DateTimeOffset CreatedAt { get; set; }
    DateTimeOffset UpdatedAt { get; set; }
}
