using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PersonalMediaManager.Domain.Entities;

namespace PersonalMediaManager.Infrastructure.Persistence.Configurations;

/// <summary>Media_Keyword 表配置（关键词/标签维度，PK=TMDB id 不自增）</summary>
internal sealed class MediaKeywordConfig : IEntityTypeConfiguration<MediaKeyword>
{
    public void Configure(EntityTypeBuilder<MediaKeyword> b)
    {
        b.ToTable("Media_Keyword");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
    }
}
