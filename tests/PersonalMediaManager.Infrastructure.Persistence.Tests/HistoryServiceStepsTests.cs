using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.History;
using PersonalMediaManager.Application.Services.Archive;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Persistence.Interceptors;
using PersonalMediaManager.Infrastructure.Persistence.Services.History;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>HistoryService.GetDetailAsync — Process_Step 时间线返回</summary>
public sealed class HistoryServiceStepsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly HistoryService _sut;

    public HistoryServiceStepsTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbFactory = new TestDbContextFactory(_connection);
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ctx.Database.EnsureCreated();
        _sut = new HistoryService(_dbFactory, Substitute.For<IPendingFileQueue>(), new TaskCancellationManager(),
            Substitute.For<IArchiveService>(), Substitute.For<IFileMover>(), Substitute.For<IFileProbe>(),
            Substitute.For<PersonalMediaManager.Application.Services.Webhook.IWebhookEmitter>(),
            Substitute.For<PersonalMediaManager.Application.Services.Parse.IFolderSeriesCache>(),
            new NullTaskNotifier(),
            AppPaths.ForRoot(Path.Combine(Path.GetTempPath(), $"pmm-steps-{Guid.NewGuid():N}")), NullLogger<HistoryService>.Instance);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task GetDetail_Returns_Empty_Steps_When_No_Steps()
    {
        long id = SeedMediaItem();

        MediaItemDetailResponse resp = await _sut.GetDetailAsync(id);

        resp.Steps.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDetail_Returns_Steps_Ordered_By_StartedAt()
    {
        long id = SeedMediaItem();
        DateTimeOffset t = new(2026, 5, 24, 12, 0, 0, TimeSpan.Zero);
        SeedStep(id, MediaItemStatus.Parsing, t.AddSeconds(2), 200, """{"cleaned":"X"}""");
        SeedStep(id, MediaItemStatus.Detected, t, 0, null);
        SeedStep(id, MediaItemStatus.Queued, t.AddSeconds(1), 50, null);

        MediaItemDetailResponse resp = await _sut.GetDetailAsync(id);

        resp.Steps.Should().HaveCount(3);
        resp.Steps[0].Stage.Should().Be(MediaItemStatus.Detected);
        resp.Steps[1].Stage.Should().Be(MediaItemStatus.Queued);
        resp.Steps[2].Stage.Should().Be(MediaItemStatus.Parsing);
        resp.Steps[2].DurMs.Should().Be(200);
        resp.Steps[2].Detail.Should().Contain("cleaned");
    }

    [Fact]
    public async Task GetDetail_Step_Detail_Json_Roundtrips_Unchanged()
    {
        long id = SeedMediaItem();
        string detailJson = """{"attempts":[{"provider":"Qwen","success":true,"latencyMs":1240}]}""";
        SeedStep(id, MediaItemStatus.AiParsing, DateTimeOffset.UtcNow, 1240, detailJson);

        MediaItemDetailResponse resp = await _sut.GetDetailAsync(id);

        resp.Steps.Single().Detail.Should().Be(detailJson);
    }

    // ---------- helpers ----------

    private long SeedMediaItem()
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        MediaItem m = MediaItem.CreateDetected($"/tmp/{Guid.NewGuid():N}.mkv", "x.mkv", 1024);
        db.MediaItems.Add(m);
        db.SaveChanges();
        return m.Id;
    }

    private void SeedStep(long mediaItemId, MediaItemStatus stage, DateTimeOffset startedAt, long durMs, string? detail)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.ProcessSteps.Add(ProcessStep.CreateFixture(mediaItemId, stage, startedAt, durMs, detail));
        db.SaveChanges();
    }

    private sealed class TestDbContextFactory : IDbContextFactory<PmmDbContext>
    {
        private readonly SqliteConnection _connection;
        public TestDbContextFactory(SqliteConnection c) { _connection = c; }
        public PmmDbContext CreateDbContext()
        {
            DbContextOptionsBuilder<PmmDbContext> opts = new();
            opts.UseSqlite(_connection);
            opts.AddInterceptors(new TimestampInterceptor(), new RowVersionInterceptor());
            return new PmmDbContext(opts.Options);
        }
    }
}
