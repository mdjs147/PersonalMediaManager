using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Configurations;

/// <summary>Media_Item 表配置（聚合根 + 状态机 + 乐观并发）</summary>
internal sealed class MediaItemConfig : IEntityTypeConfiguration<MediaItem>
{
    public void Configure(EntityTypeBuilder<MediaItem> b)
    {
        b.ToTable("Media_Item");
        b.HasKey(x => x.Id);

        b.Property(x => x.SourcePath).HasMaxLength(1000).IsRequired();
        b.Property(x => x.FileName).HasMaxLength(500).IsRequired();
        b.Property(x => x.FileSize).HasDefaultValue(0L).IsRequired();
        b.Property(x => x.FileHash).HasMaxLength(64);

        // 状态枚举：默认走 int（Detected=0），数据库设计 §1.12 用 INTEGER；HasDefaultValue 必须传 CLR 枚举值匹配
        b.Property(x => x.Status).HasConversion<int>().HasDefaultValue(MediaItemStatus.Detected).IsRequired();
        b.Property(x => x.ParseSource).HasMaxLength(8).HasConversion<string>();
        // ReviewReason 列：可空字符串枚举，仅 AwaitingReview 时有值；HasMaxLength(32) 容纳最长 TmdbMultiCandidate
        b.Property(x => x.ReviewReason).HasConversion<string>().HasMaxLength(32);

        // ParsedInfo 列：CLR 类型已是 string?（Domain 层 ParsedInfo 值对象自行 ToJson()/FromJson()），
        // 不需要 Converter；显式声明 ValueComparer<string> 表明这是 JSON 列、ChangeTracking 按字符串值比较，
        // 避免未来误把 EF 默认引用比较套到该列上。
        b.Property(x => x.ParsedInfo)
         .HasMaxLength(2000)
         .Metadata.SetValueComparer(new ValueComparer<string?>(
             (a, b) => a == b,
             v => v == null ? 0 : v.GetHashCode(),
             v => v));
        b.Property(x => x.TmdbMediaType).HasMaxLength(8);
        // 音频探测结果（av3a 等不兼容轨识别，归档前 SetAudioProbe 写入）：codec 逗号分隔快照 + 不兼容标记
        b.Property(x => x.AudioCodecs).HasMaxLength(200);
        b.Property(x => x.HasIncompatibleAudio).HasDefaultValue(false).IsRequired();
        // AI 参与标记（「AI 参与度」统计口径）：AI 升级链真正发起调用即置位，与 Media_Item 同生命周期不清零
        b.Property(x => x.AiInvolved).HasDefaultValue(false).IsRequired();
        // TmdbCandidatesJson 列：同 ParsedInfo 为 JSON 字符串列，显式声明 ValueComparer 按值比较，避免引用比较误判未变更
        b.Property(x => x.TmdbCandidatesJson)
         .HasMaxLength(4000)
         .Metadata.SetValueComparer(new ValueComparer<string?>(
             (a, b) => a == b,
             v => v == null ? 0 : v.GetHashCode(),
             v => v));
        b.Property(x => x.TargetPath).HasMaxLength(1000);
        b.Property(x => x.ErrorMessage).HasMaxLength(2000);
        b.Property(x => x.AttemptCount).HasDefaultValue(0).IsRequired();
        b.Property(x => x.RowVersion).IsConcurrencyToken().HasDefaultValue(0L).IsRequired();

        b.HasIndex(x => x.SourcePath).IsUnique().HasDatabaseName("UQ_Media_Item_SourcePath");
        // r3 P2-r3.4 决策记录：不把 RowVersion 加进 IX_Media_Item_Status_CreatedAt
        //   1) SQLite 无 INCLUDE 列；扩成 (Status, CreatedAt, Id, RowVersion) 会让索引 b-tree 整体变大，写入分裂概率上升
        //   2) 乐观锁 UPDATE 走主键：服务层先 FirstOrDefault(Id==?) 加载（主键索引 O(log n)），EF 生成
        //      UPDATE Media_Item SET ... WHERE Id=? AND RowVersion=? —— 仍然主键定位单行后比较 RowVersion，
        //      RowVersion 不需要任何二级索引参与
        //   3) 列表读取（HistoryService / ReviewService / StartupRecoveryWorker 按 Status 过滤）走当前
        //      (Status, CreatedAt) 索引定位到 rowid 后回主表读全部列，RowVersion 字段自然带回，无额外 IO
        //   结论：当前索引已是最优形态，不动；后续若热路径变更（如 EF Update 改 ExecuteUpdate 走索引扫描），再重新评估
        b.HasIndex(x => new { x.Status, x.CreatedAt }).HasDatabaseName("IX_Media_Item_Status_CreatedAt");
        b.HasIndex(x => x.TmdbId).HasDatabaseName("IX_Media_Item_TmdbId");
        b.HasIndex(x => x.CategoryId).HasDatabaseName("IX_Media_Item_CategoryId");
        b.HasIndex(x => x.FileHash).HasDatabaseName("IX_Media_Item_FileHash");

        b.HasOne<CategoryDefinition>()
         .WithMany()
         .HasForeignKey(x => x.CategoryId)
         .OnDelete(DeleteBehavior.SetNull)
         .HasConstraintName("FK_Media_Item_Category_Definition_CategoryId");

        b.Ignore(x => x.DomainEvents);
    }
}
