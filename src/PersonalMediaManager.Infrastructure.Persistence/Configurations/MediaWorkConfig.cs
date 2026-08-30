using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PersonalMediaManager.Domain.Aggregates.MediaWorks;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Infrastructure.Persistence.Conversions;

namespace PersonalMediaManager.Infrastructure.Persistence.Configurations;

/// <summary>Media_Work 表配置（作品聚合根 + 富化标量 + 乐观并发 + 排序索引）</summary>
internal sealed class MediaWorkConfig : IEntityTypeConfiguration<MediaWork>
{
    public void Configure(EntityTypeBuilder<MediaWork> b)
    {
        b.ToTable("Media_Work");
        b.HasKey(x => x.Id);

        b.Property(x => x.MediaType).HasMaxLength(8).IsRequired();
        b.Property(x => x.Title).HasMaxLength(300);
        b.Property(x => x.OriginalTitle).HasMaxLength(300);
        b.Property(x => x.Overview).HasMaxLength(4000);
        b.Property(x => x.Tagline).HasMaxLength(500);
        b.Property(x => x.PosterPath).HasMaxLength(300);
        b.Property(x => x.BackdropPath).HasMaxLength(300);
        b.Property(x => x.TmdbStatus).HasMaxLength(32);
        b.Property(x => x.OriginalLanguage).HasMaxLength(16);
        b.Property(x => x.Homepage).HasMaxLength(500);

        // OriginCountry List<string>? → JSON TEXT 列（复用既有可空 Converter + Comparer，参照 TmdbMetadataCacheConfig）
        b.Property(x => x.OriginCountry)
         .HasMaxLength(200)
         .HasConversion(new JsonValueConverter<List<string>>())
         .Metadata.SetValueComparer(new JsonValueComparer<List<string>>());

        b.Property(x => x.RowVersion).IsConcurrencyToken().HasDefaultValue(0L).IsRequired();

        b.HasIndex(x => new { x.TmdbId, x.MediaType }).IsUnique().HasDatabaseName("UQ_Media_Work_TmdbId_MediaType");
        // 排序/过滤索引：媒体库按年份/评分排序、按分类过滤
        b.HasIndex(x => x.Year).HasDatabaseName("IX_Media_Work_Year");
        b.HasIndex(x => x.VoteAverage).HasDatabaseName("IX_Media_Work_VoteAverage");
        b.HasIndex(x => x.CategoryId).HasDatabaseName("IX_Media_Work_CategoryId");

        b.HasOne<CategoryDefinition>()
         .WithMany()
         .HasForeignKey(x => x.CategoryId)
         .OnDelete(DeleteBehavior.SetNull)
         .HasConstraintName("FK_Media_Work_Category_Definition_CategoryId");

        b.Ignore(x => x.DomainEvents);
    }
}
