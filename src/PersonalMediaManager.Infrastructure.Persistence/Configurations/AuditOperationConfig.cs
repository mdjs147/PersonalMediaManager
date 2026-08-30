using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PersonalMediaManager.Domain.Entities;

namespace PersonalMediaManager.Infrastructure.Persistence.Configurations;

/// <summary>Audit_Operation 表配置（FK UserId SET NULL）</summary>
internal sealed class AuditOperationConfig : IEntityTypeConfiguration<AuditOperation>
{
    public void Configure(EntityTypeBuilder<AuditOperation> b)
    {
        b.ToTable("Audit_Operation");
        b.HasKey(x => x.Id);

        b.Property(x => x.Username).HasMaxLength(64);
        b.Property(x => x.Action).HasMaxLength(64).IsRequired();
        b.Property(x => x.Target).HasMaxLength(200);
        // Detail 列：CLR 类型已是 string?（JSON 变更前后或附加信息，写入侧自行序列化），
        // 不需要 Converter；显式 ValueComparer<string> 锁定按值比较，符合 JSON 列契约。
        b.Property(x => x.Detail)
         .HasMaxLength(2000)
         .Metadata.SetValueComparer(new ValueComparer<string?>(
             (a, b) => a == b,
             v => v == null ? 0 : v.GetHashCode(),
             v => v));
        b.Property(x => x.Ip).HasMaxLength(45);
        b.Property(x => x.UserAgent).HasMaxLength(500);
        b.Property(x => x.Timestamp).IsRequired();

        b.HasOne<UserAccount>()
         .WithMany()
         .HasForeignKey(x => x.UserId)
         .OnDelete(DeleteBehavior.SetNull)
         .HasConstraintName("FK_Audit_Operation_User_Account_UserId");

        b.HasIndex(x => new { x.UserId, x.Timestamp }).HasDatabaseName("IX_Audit_Operation_UserId_Timestamp");
        b.HasIndex(x => new { x.Action, x.Timestamp }).HasDatabaseName("IX_Audit_Operation_Action_Timestamp");
    }
}
