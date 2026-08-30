using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PersonalMediaManager.Domain.Entities;

namespace PersonalMediaManager.Infrastructure.Persistence.Configurations;

/// <summary>Audit_ScheduledTaskRun 表配置（StartedAt 主索引 + JobKey 复合索引）</summary>
internal sealed class AuditScheduledTaskRunConfig : IEntityTypeConfiguration<AuditScheduledTaskRun>
{
    public void Configure(EntityTypeBuilder<AuditScheduledTaskRun> b)
    {
        b.ToTable("Audit_ScheduledTaskRun");
        b.HasKey(x => x.Id);

        b.Property(x => x.JobKey).HasMaxLength(64).IsRequired();
        b.Property(x => x.FireInstanceId).HasMaxLength(128);
        b.Property(x => x.StartedAt).IsRequired();
        b.Property(x => x.FinishedAt);
        b.Property(x => x.DurationMs);
        b.Property(x => x.Outcome).HasMaxLength(16).HasDefaultValue(ScheduledTaskOutcome.Running).IsRequired();
        b.Property(x => x.ProcessedCount);
        b.Property(x => x.ErrorMessage).HasMaxLength(2000);
        // DetailJson 列：CLR 类型已是 string?（附加上下文 JSON，Recorder 自行序列化），
        // 不需要 Converter；显式 ValueComparer<string> 锁定按值比较，符合 JSON 列契约。
        b.Property(x => x.DetailJson)
         .HasMaxLength(2000)
         .Metadata.SetValueComparer(new ValueComparer<string?>(
             (a, b) => a == b,
             v => v == null ? 0 : v.GetHashCode(),
             v => v));

        // Dashboard 24h 时间轴主索引：按 StartedAt 倒序拉前 N 条
        b.HasIndex(x => x.StartedAt).HasDatabaseName("IX_Audit_ScheduledTaskRun_StartedAt");
        // 按 Job 过滤的复合索引
        b.HasIndex(x => new { x.JobKey, x.StartedAt }).HasDatabaseName("IX_Audit_ScheduledTaskRun_JobKey_StartedAt");
    }
}
