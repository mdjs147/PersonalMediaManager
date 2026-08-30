using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PersonalMediaManager.Domain.Entities;

namespace PersonalMediaManager.Infrastructure.Persistence.Configurations;

/// <summary>Tmdb_Setting 表配置（单例 Id=1 + 默认权重种子）</summary>
internal sealed class TmdbSettingConfig : IEntityTypeConfiguration<TmdbSetting>
{
    public void Configure(EntityTypeBuilder<TmdbSetting> b)
    {
        b.ToTable("Tmdb_Setting");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever(); // 单例 PK 固定 1

        b.Property(x => x.ApiKeyEncrypted).HasMaxLength(4000);
        b.Property(x => x.Language).HasMaxLength(16).HasDefaultValue("zh-CN").IsRequired();
        b.Property(x => x.FallbackLanguage).HasMaxLength(16).HasDefaultValue("en-US").IsRequired();
        b.Property(x => x.CandidateThreshold).HasDefaultValue(3).IsRequired();
        b.Property(x => x.RateLimitPerSecond).HasDefaultValue(40).IsRequired();
        b.Property(x => x.MetadataCacheHours).HasDefaultValue(24).IsRequired();
        b.Property(x => x.SearchCacheMinutes).HasDefaultValue(60).IsRequired();
        b.Property(x => x.ScoreWeightTitle).HasDefaultValue(0.5).IsRequired();
        b.Property(x => x.ScoreWeightYear).HasDefaultValue(0.3).IsRequired();
        b.Property(x => x.ScoreWeightPopularity).HasDefaultValue(0.1).IsRequired();
        b.Property(x => x.ScoreWeightLanguage).HasDefaultValue(0.1).IsRequired();

        // 单例 Id=1 种子（数据库设计 §1.7）
        var seed = new DateTimeOffset(2026, 5, 16, 0, 0, 0, TimeSpan.Zero);
        b.HasData(new TmdbSetting
        {
            Id = 1,
            Language = "zh-CN",
            FallbackLanguage = "en-US",
            CandidateThreshold = 3,
            RateLimitPerSecond = 40,
            MetadataCacheHours = 24,
            SearchCacheMinutes = 60,
            ScoreWeightTitle = 0.5,
            ScoreWeightYear = 0.3,
            ScoreWeightPopularity = 0.1,
            ScoreWeightLanguage = 0.1,
            CreatedAt = seed,
            UpdatedAt = seed,
        });
    }
}
