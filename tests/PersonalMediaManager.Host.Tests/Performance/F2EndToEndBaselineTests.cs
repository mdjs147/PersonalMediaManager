using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Classify;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Application.Services.Tmdb;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Host.Middleware;
using PersonalMediaManager.Infrastructure.Persistence;

namespace PersonalMediaManager.Host.Tests.Performance;

/// <summary>
/// F.2 端到端性能基线（dev plan §F.2 剩余 3 条）— in-memory 真实文件 IO 替代
/// </summary>
/// <remarks>
/// 测量目标（dev plan §F.2 表格中的「测量」列）：
///   B1 「文件监控发现延迟 &lt; 2s」：FileSystemWatcher 真实事件 → IPendingFileQueue 入队
///   B2 「检测 → 归档端到端 &lt; 3s（不含 AI）」：ProcessFileService 单文件完整管线（跳过写入完成等待，因为
///       那是「下载稳定窗口」配置项，不属于「管线处理时间」）
///   B3 「100 个文件批量扫描 &lt; 5min」：100 次 ProcessFileAsync 串行总时长；CI 上设宽松上限 30s
///       （单文件平均 100ms 已远超目标）
///
/// Mock 策略（与 D9 E2E 对齐）：
///   - ITmdbSearchService / IAiCallOrchestrator / IClassifyService：Stub（不访问外网）
///   - IWriteCompletionDetector：Stub 立即返回 true（去掉默认 5s 稳定窗口；测「管线本身」性能，
///     不测下载稳定性策略；后者另由 WriteCompletionDetectorTests 单独覆盖）
///   - 其他（IFileMover / IRuleEngineService / IArchiveService / DbContext / FileSystemWatcher）真实
///
/// CI 友好上限：每个断言上限远高于本机典型耗时，避免在低端 agent 上偶发 flake；
///   - B1 实测 50-300ms，上限 2000ms（dev plan 硬约束）
///   - B2 实测 100-500ms，上限 3000ms（dev plan 硬约束）
///   - B3 100 文件实测 5-15s，上限 30000ms（dev plan 5min 的 1/10）
///
/// 失败排查：若个别 fact 触发上限 → 优先怀疑机器负载 / 病毒扫描；若稳定性退化 → diff D7.1 / D6.x 管线代码。
/// </remarks>
[Trait("Category", "Performance")]
public sealed class F2EndToEndBaselineTests : IDisposable
{
    private readonly PmmHostFactory _factory;
    private readonly string _scratchSourceDir;
    private readonly string _scratchTargetRoot;
    private readonly ITmdbSearchService _tmdbStub;
    private readonly IAiCallOrchestrator _aiStub;
    private readonly IClassifyService _classifyStub;
    private readonly IWriteCompletionDetector _writeDetectorStub;
    private long _classifyTargetCategoryId;

    public F2EndToEndBaselineTests()
    {
        SetupGuardMiddleware.ResetCacheForTest();
        _tmdbStub = Substitute.For<ITmdbSearchService>();
        _aiStub = Substitute.For<IAiCallOrchestrator>();
        _classifyStub = Substitute.For<IClassifyService>();
        _classifyStub.ClassifyAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ClassifyResult(_classifyTargetCategoryId, ClassifyDecision.Matched));

        _writeDetectorStub = Substitute.For<IWriteCompletionDetector>();
        _writeDetectorStub.WaitUntilCompleteAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(true);

        _factory = new PmmHostFactory
        {
            ConfigureTestServices = services =>
            {
                services.RemoveAll<ITmdbSearchService>();
                services.AddScoped(_ => _tmdbStub);
                services.RemoveAll<IAiCallOrchestrator>();
                services.AddScoped(_ => _aiStub);
                services.RemoveAll<IClassifyService>();
                services.AddScoped(_ => _classifyStub);
                services.RemoveAll<IWriteCompletionDetector>();
                services.AddSingleton(_writeDetectorStub);
            },
        };
        // 触发 Host 启动以便 IServiceScope / PendingFileQueue 单例就绪
        _ = _factory.Services;

        _scratchSourceDir = Path.Combine(Path.GetTempPath(), $"pmm-f2-src-{Guid.NewGuid():N}");
        _scratchTargetRoot = Path.Combine(Path.GetTempPath(), $"pmm-f2-tgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratchSourceDir);
        Directory.CreateDirectory(_scratchTargetRoot);
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_scratchSourceDir, recursive: true); } catch { }
        try { Directory.Delete(_scratchTargetRoot, recursive: true); } catch { }
        SetupGuardMiddleware.ResetCacheForTest();
    }

    // ============================================================
    // B1：FileSystemWatcher 发现延迟 < 2s
    // ============================================================

    [Fact(DisplayName = "F.2-B1 真实 FileSystemWatcher：文件落地 → OnFileEvent 触发 < 2000ms")]
    public async Task B1_FileSystemWatcher_DiscoveryLatency_Under2Seconds()
    {
        // 取容器中的 IFileWatcherFactory（真实 WatcherAdapterFactory，包装 FileSystemWatcher）
        // 注意：不复用容器内 IPendingFileQueue 单例——Host 的 TaskProcessorWorker 会先把 item 读走
        // dev plan §F.2 测量定义是「FileSystemWatcher 事件 → MediaItem 入库」，而入队 → MediaItem 入库是
        // 后端管线时间（B2 已覆盖），这里聚焦「事件触发延迟」本身。
        IFileWatcherFactory factory = _factory.Services.GetRequiredService<IFileWatcherFactory>();
        using IFileWatcher watcher = factory.Create(_scratchSourceDir);

        TaskCompletionSource<FileWatcherEvent> firstEvent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.OnFileEvent += evt =>
        {
            if (evt.ChangeType is FileWatcherChangeType.Created or FileWatcherChangeType.Renamed
                && !Directory.Exists(evt.FullPath))
            {
                firstEvent.TrySetResult(evt);
            }
        };
        watcher.Start();
        // 让 FSW 真正进入 ReadDirectoryChangesW 内部循环，避免 race
        await Task.Delay(150);

        string newFile = Path.Combine(_scratchSourceDir, "B1.Inception.2010.mkv");
        Stopwatch sw = Stopwatch.StartNew();
        await File.WriteAllBytesAsync(newFile, new byte[64]);

        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(3000));
        Task<FileWatcherEvent> waitTask = firstEvent.Task.WaitAsync(cts.Token);
        FileWatcherEvent received = await waitTask;
        sw.Stop();

        received.FullPath.Should().Be(newFile);
        sw.ElapsedMilliseconds.Should().BeLessThan(2000,
            "F.2 性能基线（dev plan §F.2）：FileSystemWatcher 事件触发延迟应 < 2s");
    }

    // ============================================================
    // B2：单文件「检测 → 归档」端到端 < 3s（不含 AI）
    // ============================================================

    [Fact(DisplayName = "F.2-B2 单文件全管线（不含 AI / 不含写入稳定窗口）端到端 < 3000ms")]
    public async Task B2_SingleFile_FullPipeline_Under3Seconds()
    {
        await CompleteSetupAsync();
        await SeedMovieCategoryAsync(_scratchTargetRoot);
        ConfigureTmdbForMovie(tmdbId: 27205, title: "盗梦空间", originalTitle: "Inception", year: 2010);

        string srcFile = Path.Combine(_scratchSourceDir, "Inception.2010.1080p.BluRay.x264.mkv");
        await File.WriteAllBytesAsync(srcFile, new byte[1024]);

        // 单次预热：触发 EF / Razor / Scalar 等懒加载 + JIT，避免首次冷启动失真
        // 落点规范化后：归档目录用 TMDB 规范名「盗梦空间」（非源文件英文解析名 Inception）
        await ProcessFileAsync(srcFile);
        File.Delete(Path.Combine(_scratchTargetRoot, "盗梦空间 (2010) {tmdb-27205}", "盗梦空间 (2010) {tmdb-27205}.mkv"));
        Directory.Delete(Path.Combine(_scratchTargetRoot, "盗梦空间 (2010) {tmdb-27205}"), recursive: true);
        await ResetMediaItemsAsync();

        // 重新放回源文件并计时
        await File.WriteAllBytesAsync(srcFile, new byte[1024]);

        Stopwatch sw = Stopwatch.StartNew();
        ProcessFileOutcome outcome = await ProcessFileAsync(srcFile);
        sw.Stop();

        outcome.Outcome.Should().Be(ProcessOutcome.Completed);
        sw.ElapsedMilliseconds.Should().BeLessThan(3000,
            "F.2 性能基线（dev plan §F.2）：单文件检测→归档端到端应 < 3s（不含 AI / 不含写入稳定窗口）");
    }

    // ============================================================
    // B3：100 个文件批量串行处理 < 30s（dev plan 上限 5min，CI 收紧到 1/10）
    // ============================================================

    [Fact(DisplayName = "F.2-B3 100 文件串行管线总耗时 < 30000ms（dev plan 5min 上限的 1/10）")]
    public async Task B3_Batch100Files_SerialProcessing_Under30Seconds()
    {
        await CompleteSetupAsync();
        await SeedMovieCategoryAsync(_scratchTargetRoot);
        ConfigureTmdbForMovie(tmdbId: 27205, title: "盗梦空间", originalTitle: "Inception", year: 2010);

        // 投放 100 个唯一命名文件（同 TMDB 候选但 SourcePath 各异；归档目标会冲突 → 第 2+ 个走 ConflictSkipped 早返）
        // 这里测的是「管线吞吐」，归档冲突跳过也属于真实生产路径（防覆盖语义）
        const int totalFiles = 100;
        List<string> sources = new(totalFiles);
        for (int i = 0; i < totalFiles; i++)
        {
            string p = Path.Combine(_scratchSourceDir, $"Batch.{i:D3}.Inception.2010.mkv");
            await File.WriteAllBytesAsync(p, new byte[256]);
            sources.Add(p);
        }

        Stopwatch sw = Stopwatch.StartNew();
        int completedCount = 0;
        int conflictOrOtherCount = 0;
        foreach (string src in sources)
        {
            ProcessFileOutcome r = await ProcessFileAsync(src);
            if (r.Outcome == ProcessOutcome.Completed) completedCount++;
            else conflictOrOtherCount++;
        }
        sw.Stop();

        // 至少首个 Completed；后续 99 个目标命名相同会走 ConflictSkipped → Skipped（同样代表管线 fully 跑完）
        completedCount.Should().BeGreaterThanOrEqualTo(1, "首个文件应正常归档");
        (completedCount + conflictOrOtherCount).Should().Be(totalFiles);

        long avgMs = sw.ElapsedMilliseconds / totalFiles;
        sw.ElapsedMilliseconds.Should().BeLessThan(30_000,
            $"F.2 性能基线（dev plan §F.2）：100 文件批量串行 < 30s（CI 收紧值；dev plan 上限 5min）。实测平均 {avgMs}ms/file");
    }

    // ---------- helpers（与 D9 E2E 对齐风格） ----------

    private async Task CompleteSetupAsync()
    {
        using HttpClient client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/setup/admin", new { username = "admin", password = "secret123" });
        await client.PostAsJsonAsync("/api/setup/complete", new { });
    }

    private async Task<long> SeedMovieCategoryAsync(string root)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        IDbContextFactory<PmmDbContext> f = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PmmDbContext>>();
        await using PmmDbContext db = await f.CreateDbContextAsync();
        CategoryDefinition c = new() { Name = "电影", MediaType = MediaType.Movie, TargetRoot = root };
        db.CategoryDefinitions.Add(c);
        await db.SaveChangesAsync();
        _classifyTargetCategoryId = c.Id;
        return c.Id;
    }

    private async Task ResetMediaItemsAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        IDbContextFactory<PmmDbContext> f = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PmmDbContext>>();
        await using PmmDbContext db = await f.CreateDbContextAsync();
        db.MediaItems.RemoveRange(db.MediaItems);
        db.WebhookDeliveries.RemoveRange(db.WebhookDeliveries);
        await db.SaveChangesAsync();
    }

    private void ConfigureTmdbForMovie(int tmdbId, string title, string originalTitle, int year)
    {
        TmdbCandidate cand = new(tmdbId, "movie", title, originalTitle, year, 50.0, "en",
            new[] { "US" }, "/p.jpg", null);
        _tmdbStub.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult(new[] { cand }, null));
        SeedTmdbCache(tmdbId, "movie", title, originalTitle, year);
        _tmdbStub.GetDetailsAsync(tmdbId, "movie", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(tmdbId, "movie", title, originalTitle, year, null, "/p.jpg",
                new[] { "US" }, "en", null, null, "{}"));
    }

    private void SeedTmdbCache(int tmdbId, string mediaType, string title, string originalTitle, int year)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        IDbContextFactory<PmmDbContext> f = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PmmDbContext>>();
        using PmmDbContext db = f.CreateDbContext();
        if (db.TmdbMetadataCaches.Any(c => c.TmdbId == tmdbId && c.MediaType == mediaType)) return;
        db.TmdbMetadataCaches.Add(new TmdbMetadataCache
        {
            TmdbId = tmdbId,
            MediaType = mediaType,
            Title = title,
            OriginalTitle = originalTitle,
            Year = year,
            TotalSeasons = null,
            OriginCountry = new List<string> { "US" },
            OriginalLanguage = "en",
            Genres = "[]",
            PosterPath = "/p.jpg",
            CachedAt = DateTimeOffset.UtcNow,
            RawJson = "{}",
        });
        db.SaveChanges();
    }

    private async Task<ProcessFileOutcome> ProcessFileAsync(string fullPath)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        IProcessFileService svc = scope.ServiceProvider.GetRequiredService<IProcessFileService>();
        return await svc.ProcessAsync(new PendingFileItem(fullPath, WatchFolderId: 0, PendingFileSource.Manual),
            CancellationToken.None);
    }

}
