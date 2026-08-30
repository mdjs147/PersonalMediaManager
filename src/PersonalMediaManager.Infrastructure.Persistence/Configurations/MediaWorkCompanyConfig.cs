using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PersonalMediaManager.Domain.Aggregates.MediaWorks;
using PersonalMediaManager.Domain.Entities;

namespace PersonalMediaManager.Infrastructure.Persistence.Configurations;

/// <summary>Media_WorkCompany 连接表配置（复合 PK，FK 作品 CASCADE / FK 公司 RESTRICT）</summary>
internal sealed class MediaWorkCompanyConfig : IEntityTypeConfiguration<MediaWorkCompany>
{
    public void Configure(EntityTypeBuilder<MediaWorkCompany> b)
    {
        b.ToTable("Media_WorkCompany");
        b.HasKey(x => new { x.WorkId, x.CompanyId });

        b.HasOne<MediaWork>()
         .WithMany(w => w.Companies)
         .HasForeignKey(x => x.WorkId)
         .OnDelete(DeleteBehavior.Cascade)
         .HasConstraintName("FK_Media_WorkCompany_Media_Work_WorkId");

        b.HasOne<MediaCompany>()
         .WithMany()
         .HasForeignKey(x => x.CompanyId)
         .OnDelete(DeleteBehavior.Restrict)
         .HasConstraintName("FK_Media_WorkCompany_Media_Company_CompanyId");

        b.HasIndex(x => x.CompanyId).HasDatabaseName("IX_Media_WorkCompany_CompanyId");
    }
}
