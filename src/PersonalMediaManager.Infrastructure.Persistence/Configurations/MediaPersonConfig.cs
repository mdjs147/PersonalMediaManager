using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PersonalMediaManager.Domain.Entities;

namespace PersonalMediaManager.Infrastructure.Persistence.Configurations;

/// <summary>Media_Person 表配置（人员维度，PK=TMDB id 不自增）</summary>
internal sealed class MediaPersonConfig : IEntityTypeConfiguration<MediaPerson>
{
    public void Configure(EntityTypeBuilder<MediaPerson> b)
    {
        b.ToTable("Media_Person");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.ProfilePath).HasMaxLength(300);
        b.Property(x => x.KnownForDepartment).HasMaxLength(50);
    }
}
