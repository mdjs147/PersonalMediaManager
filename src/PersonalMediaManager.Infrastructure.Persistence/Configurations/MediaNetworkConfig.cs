using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PersonalMediaManager.Domain.Entities;

namespace PersonalMediaManager.Infrastructure.Persistence.Configurations;

/// <summary>Media_Network 表配置（电视台维度，PK=TMDB id 不自增）</summary>
internal sealed class MediaNetworkConfig : IEntityTypeConfiguration<MediaNetwork>
{
    public void Configure(EntityTypeBuilder<MediaNetwork> b)
    {
        b.ToTable("Media_Network");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.LogoPath).HasMaxLength(300);
        b.Property(x => x.OriginCountry).HasMaxLength(8);
    }
}
