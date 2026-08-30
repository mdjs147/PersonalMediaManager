using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Infrastructure.Persistence.Conversions;

namespace PersonalMediaManager.Infrastructure.Persistence.Configurations;

/// <summary>Tmdb_MetadataCache 表配置（复合 PK + OriginCountry JSON 列）</summary>
internal sealed class TmdbMetadataCacheConfig : IEntityTypeConfiguration<TmdbMetadataCache>
{
    public void Configure(EntityTypeBuilder<TmdbMetadataCache> b)
    {
        b.ToTable("Tmdb_MetadataCache");
        b.HasKey(x => new { x.TmdbId, x.MediaType });

        b.Property(x => x.MediaType).HasMaxLength(8).IsRequired();
        b.Property(x => x.Title).HasMaxLength(300);
        b.Property(x => x.OriginalTitle).HasMaxLength(300);
        b.Property(x => x.PosterPath).HasMaxLength(200);
        b.Property(x => x.OriginalLanguage).HasMaxLength(16);
        // Genres 列：CLR 类型已是 string?（JSON 数组原文 [{"id":18,"name":"Drama"}]，Application 层自行解析），
        // 不需要 Converter；显式 ValueComparer<string> 锁定按值比较，符合 JSON 列契约。
        b.Property(x => x.Genres)
         .HasMaxLength(1000)
         .Metadata.SetValueComparer(new ValueComparer<string?>(
             (a, b) => a == b,
             v => v == null ? 0 : v.GetHashCode(),
             v => v));
        b.Property(x => x.Overview).HasMaxLength(4000);
        // RawJson 无长度限制；CachedAt 必填
        b.Property(x => x.CachedAt).IsRequired();

        // OriginCountry List<string>? → JSON TEXT 列（可空）。
        // JsonValueConverter<T> 即可空版（ValueConverter<T?, string?>；非空版另为 JsonValueConverterNonNull<T>），
        // 与实体 List<string>? 严格对称：DB NULL ↔ CLR null，不会强转 []。
        // （审计 2026-05-25-r3 P0-r3.1 复核为误报——当时把可空版误判为非空版；保留此注释防再次误标。）
        b.Property(x => x.OriginCountry)
         .HasMaxLength(200)
         .HasConversion(new JsonValueConverter<List<string>>())
         .Metadata.SetValueComparer(new JsonValueComparer<List<string>>());

        b.HasIndex(x => x.CachedAt).HasDatabaseName("IX_Tmdb_MetadataCache_CachedAt");
    }
}
