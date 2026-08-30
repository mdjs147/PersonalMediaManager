using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PersonalMediaManager.Domain.Entities;

namespace PersonalMediaManager.Infrastructure.Persistence.Configurations;

/// <summary>Tmdb_SearchCache 表配置（PK=QueryHash 字符串）</summary>
internal sealed class TmdbSearchCacheConfig : IEntityTypeConfiguration<TmdbSearchCache>
{
    public void Configure(EntityTypeBuilder<TmdbSearchCache> b)
    {
        b.ToTable("Tmdb_SearchCache");
        b.HasKey(x => x.QueryHash);

        b.Property(x => x.QueryHash).HasMaxLength(64).IsRequired();
        b.Property(x => x.QueryRaw).HasMaxLength(500);
        // Results 列：CLR 类型已是 string（JSON 数组原文，TmdbSearchService 自行序列化），
        // 不需要 Converter；显式 ValueComparer<string> 锁定按值比较，避免未来误用引用比较导致刷新缓存时不写库。
        b.Property(x => x.Results)
         .IsRequired()
         .Metadata.SetValueComparer(new ValueComparer<string>(
             (a, b) => a == b,
             v => v.GetHashCode(),
             v => v));
        b.Property(x => x.CachedAt).IsRequired();

        b.HasIndex(x => x.CachedAt).HasDatabaseName("IX_Tmdb_SearchCache_CachedAt");
    }
}
