using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Infrastructure.Persistence;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>验证 TimestampInterceptor 在 Added / Modified 时自动写入 UTC 时间戳</summary>
public sealed class TimestampInterceptorTests : IClassFixture<PmmDbContextTestFixture>
{
    private readonly PmmDbContextTestFixture _fixture;

    public TimestampInterceptorTests(PmmDbContextTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Insert_AutoFillsCreatedAtAndUpdatedAt()
    {
        DateTimeOffset fixedNow = new(2026, 5, 16, 12, 0, 0, TimeSpan.Zero);

        using (PmmDbContext writer = _fixture.CreateContext(() => fixedNow))
        {
            MediaItem item = MediaItem.CreateDetected(@"F:\test\insert.mkv", "insert.mkv", 1024);
            writer.MediaItems.Add(item);
            await writer.SaveChangesAsync();
        }

        using PmmDbContext reader = _fixture.CreateContext();
        MediaItem? read = await reader.MediaItems.FirstOrDefaultAsync(x => x.SourcePath == @"F:\test\insert.mkv");
        read.Should().NotBeNull();
        read!.CreatedAt.Should().Be(fixedNow);
        read.UpdatedAt.Should().Be(fixedNow);
    }

    [Fact]
    public async Task Update_OnlyAdvancesUpdatedAt_NotCreatedAt()
    {
        DateTimeOffset createTime = new(2026, 5, 16, 8, 0, 0, TimeSpan.Zero);
        DateTimeOffset updateTime = new(2026, 5, 16, 9, 30, 0, TimeSpan.Zero);

        long id;
        using (PmmDbContext writer = _fixture.CreateContext(() => createTime))
        {
            MediaItem item = MediaItem.CreateDetected(@"F:\test\update.mkv", "update.mkv", 2048);
            writer.MediaItems.Add(item);
            await writer.SaveChangesAsync();
            id = item.Id;
        }

        using (PmmDbContext updater = _fixture.CreateContext(() => updateTime))
        {
            MediaItem? row = await updater.MediaItems.FirstAsync(x => x.Id == id);
            row.RecordError("触发 UPDATE");
            await updater.SaveChangesAsync();
        }

        using PmmDbContext reader = _fixture.CreateContext();
        MediaItem read = await reader.MediaItems.AsNoTracking().FirstAsync(x => x.Id == id);
        read.CreatedAt.Should().Be(createTime);
        read.UpdatedAt.Should().Be(updateTime);
    }
}
