using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Configurations;

/// <summary>Watch_IgnoreRule 表配置（含 16 条默认 Extension 种子）</summary>
internal sealed class WatchIgnoreRuleConfig : IEntityTypeConfiguration<WatchIgnoreRule>
{
    public void Configure(EntityTypeBuilder<WatchIgnoreRule> b)
    {
        b.ToTable("Watch_IgnoreRule");
        b.HasKey(x => x.Id);

        b.Property(x => x.Type).HasMaxLength(16).HasConversion<string>().IsRequired();
        b.Property(x => x.Pattern).HasMaxLength(200).IsRequired();
        b.Property(x => x.Description).HasMaxLength(200);
        b.Property(x => x.Enabled).HasDefaultValue(true).IsRequired();

        b.HasIndex(x => new { x.Type, x.Enabled }).HasDatabaseName("IX_Watch_IgnoreRule_Type_Enabled");
        b.HasIndex(x => new { x.Type, x.Pattern }).IsUnique().HasDatabaseName("UQ_Watch_IgnoreRule_Type_Pattern");

        // 默认种子（数据库设计 §1.4）：下载器临时 / 辅助文件扩展名。
        // Id 1~7 为初版；Id 8~16 为各主流下载器中间文件补充（顺序固定，勿调整已有项，否则会改动已落库行）。
        DateTimeOffset seed = new(2026, 5, 16, 0, 0, 0, TimeSpan.Zero);
        (string Pattern, string Description)[] extensions =
        [
            (".part", "默认忽略下载中临时文件"),
            (".tmp", "默认忽略下载中临时文件"),
            (".crdownload", "默认忽略下载中临时文件"),
            (".download", "默认忽略下载中临时文件"),
            (".!qb", "默认忽略下载中临时文件"),
            (".downloading", "默认忽略下载中临时文件"),
            (".complete", "默认忽略下载中临时文件"),
            (".torrent", "BT 种子描述文件"),
            (".xltd", "迅雷下载临时文件"),
            (".td", "迅雷下载临时文件"),
            (".!ut", "uTorrent 未完成文件"),
            (".bc!", "BitComet 未完成文件"),
            (".aria2", "aria2 下载控制文件"),
            (".partial", "下载中临时文件（Edge / IDM 等）"),
            (".opdownload", "Opera 下载中临时文件"),
            (".fdmdownload", "Free Download Manager 下载临时文件"),
        ];
        b.HasData(extensions.Select((e, i) => new WatchIgnoreRule
        {
            Id = i + 1,
            Type = IgnoreRuleType.Extension,
            Pattern = e.Pattern,
            Description = e.Description,
            Enabled = true,
            CreatedAt = seed,
            UpdatedAt = seed,
        }).ToArray());
    }
}
