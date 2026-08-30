using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Infrastructure.Persistence;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>验证 RowVersion 乐观并发：两个 DbContext 并发改同一行，第二个 SaveChanges 应抛 DbUpdateConcurrencyException</summary>
public sealed class RowVersionInterceptorTests : IClassFixture<PmmDbContextTestFixture>
{
    private readonly PmmDbContextTestFixture _fixture;

    public RowVersionInterceptorTests(PmmDbContextTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Update_BumpsRowVersionByOne()
    {
        long id;
        using (PmmDbContext writer = _fixture.CreateContext())
        {
            MediaItem item = MediaItem.CreateDetected(@"F:\test\rowver-1.mkv", "rowver-1.mkv", 1);
            writer.MediaItems.Add(item);
            await writer.SaveChangesAsync();
            id = item.Id;
        }

        using (PmmDbContext updater = _fixture.CreateContext())
        {
            MediaItem row = await updater.MediaItems.FirstAsync(x => x.Id == id);
            row.RecordError("改一次");
            await updater.SaveChangesAsync();
        }

        using PmmDbContext reader = _fixture.CreateContext();
        MediaItem read = await reader.MediaItems.AsNoTracking().FirstAsync(x => x.Id == id);
        read.RowVersion.Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentUpdate_OnSecondSave_ThrowsDbUpdateConcurrencyException()
    {
        long id;
        using (PmmDbContext writer = _fixture.CreateContext())
        {
            MediaItem item = MediaItem.CreateDetected(@"F:\test\concurrent.mkv", "concurrent.mkv", 1);
            writer.MediaItems.Add(item);
            await writer.SaveChangesAsync();
            id = item.Id;
        }

        using PmmDbContext ctxA = _fixture.CreateContext();
        using PmmDbContext ctxB = _fixture.CreateContext();

        MediaItem rowA = await ctxA.MediaItems.FirstAsync(x => x.Id == id);
        MediaItem rowB = await ctxB.MediaItems.FirstAsync(x => x.Id == id);

        rowA.RecordError("A 改");
        await ctxA.SaveChangesAsync();

        rowB.RecordError("B 改（基于旧 RowVersion，应失败）");
        Func<Task> act = async () => await ctxB.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }
}
