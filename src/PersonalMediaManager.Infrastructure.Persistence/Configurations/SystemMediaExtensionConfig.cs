using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PersonalMediaManager.Domain.Entities;

namespace PersonalMediaManager.Infrastructure.Persistence.Configurations;

/// <summary>System_MediaExtension 表配置（含 14 条内置种子）</summary>
internal sealed class SystemMediaExtensionConfig : IEntityTypeConfiguration<SystemMediaExtension>
{
    public void Configure(EntityTypeBuilder<SystemMediaExtension> b)
    {
        b.ToTable("System_MediaExtension");
        b.HasKey(x => x.Id);

        b.Property(x => x.Extension).HasMaxLength(32).IsRequired();
        b.Property(x => x.Description).HasMaxLength(200);
        b.Property(x => x.Enabled).HasDefaultValue(true).IsRequired();

        b.HasIndex(x => x.Extension).IsUnique().HasDatabaseName("UQ_System_MediaExtension_Extension");
        b.HasIndex(x => x.Enabled).HasDatabaseName("IX_System_MediaExtension_Enabled");

        // 14 条内置种子（与 MediaFileExtensions.Video 静态白名单同源）
        DateTimeOffset seed = new(2026, 5, 31, 0, 0, 0, TimeSpan.Zero);
        string[] extensions =
        [
            ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".flv", ".webm",
            ".m4v", ".ts", ".mpg", ".mpeg", ".m2ts", ".rmvb", ".rm",
        ];
        b.HasData(extensions.Select((ext, i) => new SystemMediaExtension
        {
            Id = i + 1,
            Extension = ext,
            Description = "内置默认",
            Enabled = true,
            CreatedAt = seed,
            UpdatedAt = seed,
        }).ToArray());
    }
}
