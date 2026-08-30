using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PersonalMediaManager.Domain.Entities;

namespace PersonalMediaManager.Infrastructure.Persistence.Configurations;

/// <summary>Subtitle_Setting 表配置（单例 Id=1 + 默认种子）</summary>
internal sealed class SubtitleSettingConfig : IEntityTypeConfiguration<SubtitleSetting>
{
    public void Configure(EntityTypeBuilder<SubtitleSetting> b)
    {
        b.ToTable("Subtitle_Setting");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever(); // 单例 PK 固定 1

        b.Property(x => x.AssrtTokenEncrypted).HasMaxLength(4000);
        b.Property(x => x.AssrtBaseUrl).HasMaxLength(200).IsRequired();
        b.Property(x => x.TimeoutSeconds).HasDefaultValue(30).IsRequired();
        b.Property(x => x.UseProxy).HasDefaultValue(false).IsRequired();
        b.Property(x => x.RowVersion).IsConcurrencyToken().HasDefaultValue(0L).IsRequired();

        b.Ignore(x => x.DomainEvents);

        // 单例 Id=1 种子（照 TmdbSettingConfig 固定时间戳模式，HasData 必须显式给 CreatedAt/UpdatedAt）
        var seed = new DateTimeOffset(2026, 6, 12, 0, 0, 0, TimeSpan.Zero);
        b.HasData(new SubtitleSetting
        {
            Id = 1,
            AssrtBaseUrl = "https://api.assrt.net",
            TimeoutSeconds = 30,
            UseProxy = false,
            CreatedAt = seed,
            UpdatedAt = seed,
        });
    }
}
