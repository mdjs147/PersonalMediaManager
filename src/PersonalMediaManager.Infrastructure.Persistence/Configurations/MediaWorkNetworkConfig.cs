using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PersonalMediaManager.Domain.Aggregates.MediaWorks;
using PersonalMediaManager.Domain.Entities;

namespace PersonalMediaManager.Infrastructure.Persistence.Configurations;

/// <summary>Media_WorkNetwork 连接表配置（复合 PK，FK 作品 CASCADE / FK 电视台 RESTRICT）</summary>
internal sealed class MediaWorkNetworkConfig : IEntityTypeConfiguration<MediaWorkNetwork>
{
    public void Configure(EntityTypeBuilder<MediaWorkNetwork> b)
    {
        b.ToTable("Media_WorkNetwork");
        b.HasKey(x => new { x.WorkId, x.NetworkId });

        b.HasOne<MediaWork>()
         .WithMany(w => w.Networks)
         .HasForeignKey(x => x.WorkId)
         .OnDelete(DeleteBehavior.Cascade)
         .HasConstraintName("FK_Media_WorkNetwork_Media_Work_WorkId");

        b.HasOne<MediaNetwork>()
         .WithMany()
         .HasForeignKey(x => x.NetworkId)
         .OnDelete(DeleteBehavior.Restrict)
         .HasConstraintName("FK_Media_WorkNetwork_Media_Network_NetworkId");

        b.HasIndex(x => x.NetworkId).HasDatabaseName("IX_Media_WorkNetwork_NetworkId");
    }
}
