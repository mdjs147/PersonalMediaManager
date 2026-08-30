using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PersonalMediaManager.Domain.Aggregates.MediaWorks;
using PersonalMediaManager.Domain.Entities;

namespace PersonalMediaManager.Infrastructure.Persistence.Configurations;

/// <summary>Media_WorkCredit 表配置（演职员合一，FK 作品 CASCADE / FK 人员 RESTRICT）</summary>
internal sealed class MediaWorkCreditConfig : IEntityTypeConfiguration<MediaWorkCredit>
{
    public void Configure(EntityTypeBuilder<MediaWorkCredit> b)
    {
        b.ToTable("Media_WorkCredit");
        b.HasKey(x => x.Id);

        b.Property(x => x.CreditType).HasMaxLength(8).IsRequired();
        b.Property(x => x.Character).HasMaxLength(300);
        b.Property(x => x.Job).HasMaxLength(100);
        b.Property(x => x.Department).HasMaxLength(100);

        b.HasOne<MediaWork>()
         .WithMany(w => w.Credits)
         .HasForeignKey(x => x.WorkId)
         .OnDelete(DeleteBehavior.Cascade)
         .HasConstraintName("FK_Media_WorkCredit_Media_Work_WorkId");

        b.HasOne<MediaPerson>()
         .WithMany()
         .HasForeignKey(x => x.PersonId)
         .OnDelete(DeleteBehavior.Restrict)
         .HasConstraintName("FK_Media_WorkCredit_Media_Person_PersonId");

        b.HasIndex(x => x.WorkId).HasDatabaseName("IX_Media_WorkCredit_WorkId");
        // 反查：按人员检索全库作品（同时命中出演与制作）
        b.HasIndex(x => x.PersonId).HasDatabaseName("IX_Media_WorkCredit_PersonId");
    }
}
