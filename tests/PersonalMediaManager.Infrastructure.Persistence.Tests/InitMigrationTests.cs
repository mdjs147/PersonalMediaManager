using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Infrastructure.Persistence;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>验证 Init Migration 模型能在 SQLite 上正确建库 + 种子数据写入</summary>
public sealed class InitMigrationTests : IClassFixture<PmmDbContextTestFixture>
{
    private readonly PmmDbContextTestFixture _fixture;

    public InitMigrationTests(PmmDbContextTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task EnsureCreated_BuildsAll16Tables()
    {
        using PmmDbContext ctx = _fixture.CreateContext();

        // 16 张表全部可查询（即使空表也不应抛 SQLite error: no such table）
        Assert.NotNull(await ctx.SystemSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == "Web.Port"));
        await ctx.UserAccounts.AsNoTracking().CountAsync();
        await ctx.WatchFolders.AsNoTracking().CountAsync();
        await ctx.WatchIgnoreRules.AsNoTracking().CountAsync();
        await ctx.ParseRules.AsNoTracking().CountAsync();
        await ctx.ParseAiProviders.AsNoTracking().CountAsync();
        await ctx.TmdbSettings.AsNoTracking().CountAsync();
        await ctx.TmdbMetadataCaches.AsNoTracking().CountAsync();
        await ctx.TmdbSearchCaches.AsNoTracking().CountAsync();
        await ctx.CategoryDefinitions.AsNoTracking().CountAsync();
        await ctx.CategoryMatchRules.AsNoTracking().CountAsync();
        await ctx.MediaItems.AsNoTracking().CountAsync();
        await ctx.WebhookSubscriptions.AsNoTracking().CountAsync();
        await ctx.WebhookDeliveries.AsNoTracking().CountAsync();
        await ctx.AuditOperations.AsNoTracking().CountAsync();
        await ctx.AuditAiCalls.AsNoTracking().CountAsync();
        await ctx.SubtitleSettings.AsNoTracking().CountAsync();
        await ctx.SubtitleDownloads.AsNoTracking().CountAsync();
    }

    [Fact]
    public async Task SeedData_SystemSetting_HasExactly37Rows()
    {
        // 9 条基础 KV 配置（含 Webhook_Enabled 总开关；原 Tmdb.CandidateThreshold 候选阈值 + Webhook.DeliveryRetainCount 投递保留两个死键已移除）
        // + 5 条升级检查配置（D9.1：Update_GitHubOwner / Repo / Pat / LastCheckJson / SkippedVersion）
        // + 2 条升级检查可改配置（D9.2：Update_Enabled / Update_CheckIntervalHours，从 appsettings 迁移到 db）
        // + 5 条代理设置（Proxy_Enabled / HttpUrl / BypassList / UseForTmdb / UseForUpdateCheck）
        // + 2 条 AI 调用日志留存（监控增强：Audit_AiCallRetentionDays / Audit_AiCallMaxRowsPerProvider）
        // + 2 条自动备份（Backup_Enabled / Backup_RetainCount）
        // + 3 条健康告警（Archive_DiskWarnPercent / Archive_DiskCriticalPercent / System_AlertSuppressMinutes）
        // + 1 条空目录清理可忽略扩展名清单（File.CleanEmptyDirIgnoreExts，配合 File.CleanEmptyDir 总开关）
        // + 4 条音频不兼容处理（Audio_IncompatibleCheckEnabled / FfmpegPath / IncompatibleCodecs / AutoRemux）
        // + 4 条归档命名模板（Archive_MovieNameTemplate / TvShowFolderTemplate / TvEpisodeNameTemplate / SeasonFolderIncludeTitle）
        using PmmDbContext ctx = _fixture.CreateContext();
        int count = await ctx.SystemSettings.AsNoTracking().CountAsync();
        count.Should().Be(37);
    }

    [Fact]
    public async Task SeedData_WatchIgnoreRule_Has16DefaultExtensions()
    {
        // 7 条下载中临时文件 + 9 条各下载器中间/辅助文件（.torrent / .xltd / .td / .!ut / .bc! / .aria2 / .partial / .opdownload / .fdmdownload）
        using PmmDbContext ctx = _fixture.CreateContext();
        int count = await ctx.WatchIgnoreRules.AsNoTracking().CountAsync();
        count.Should().Be(16);
    }

    [Fact]
    public async Task SeedData_TmdbSetting_IsSingletonWithId1()
    {
        using PmmDbContext ctx = _fixture.CreateContext();
        TmdbSetting? row = await ctx.TmdbSettings.AsNoTracking().FirstOrDefaultAsync();
        row.Should().NotBeNull();
        row!.Id.Should().Be(1);
        row.Language.Should().Be("zh-CN");
        row.CandidateThreshold.Should().Be(3);
    }

    [Fact]
    public async Task SeedData_SubtitleSetting_IsSingletonWithId1()
    {
        using PmmDbContext ctx = _fixture.CreateContext();
        SubtitleSetting? row = await ctx.SubtitleSettings.AsNoTracking().FirstOrDefaultAsync();
        row.Should().NotBeNull();
        row!.Id.Should().Be(1);
        row.AssrtBaseUrl.Should().Be("https://api.assrt.net");
        row.TimeoutSeconds.Should().Be(30);
    }
}
