using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Application.Services.Classify;
using PersonalMediaManager.Application.Services.Tmdb;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Host.Middleware;
using PersonalMediaManager.Infrastructure.Persistence;

namespace PersonalMediaManager.Host.Tests.E2E;

/// <summary>D.9 端到端：电影 / 剧集 / 人工兜底 三条 E2E 用例</summary>
/// <remarks>
/// 全链路覆盖：写真实文件 → ProcessFileService 编排 → 真实 IFileMover 移动 → DB 落 MediaItem +
/// Webhook_Delivery + ArchivedAt 等字段。
///
/// Mock 策略：
///   - ITmdbSearchService：预设候选 / 详情（避免真打 TMDB API）
///   - IAiCallOrchestrator：预设 AI 结果（避免真打 AI 提供商）
///   - 其余（IFileMover / IRuleEngineService / IClassifyService / IArchiveService / DbContext）真实
///
/// 简化：直接走 IServiceScope → IProcessFileService.ProcessAsync 跳过 FileSystemWatcher + Channel
/// 异步层（这两层已在 D.6.1/D.6.2 单独覆盖）。这样 E2E 时序确定，无 flake。
/// </remarks>
public sealed class D9MediaPipelineE2ETests : IDisposable
{
    private readonly PmmHostFactory _factory;
    private readonly HttpClient _client;
    private readonly string _scratchSourceDir;
    private readonly string _scratchTargetRoot;
    private readonly ITmdbSearchService _tmdbStub;
    private readonly IAiCallOrchestrator _aiStub;
    private readonly IClassifyService _classifyStub;
    private long _classifyTargetCategoryId; // 测试运行中由 SeedXxxCategoryAsync 写入

    public D9MediaPipelineE2ETests()
    {
        SetupGuardMiddleware.ResetCacheForTest();
        _tmdbStub = Substitute.For<ITmdbSearchService>();
        _aiStub = Substitute.For<IAiCallOrchestrator>();
        _classifyStub = Substitute.For<IClassifyService>();
        // 默认 Matched，CategoryId 由 _classifyTargetCategoryId 闭包提供
        _classifyStub.ClassifyAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ClassifyResult(_classifyTargetCategoryId, ClassifyDecision.Matched));

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
            },
        };
        _client = _factory.CreateClient();

        // RuleEngine 会扫描完整路径中的 4 位年份；十六进制 GUID 偶含 2026 等片段会污染解析结果。
        // 临时目录后缀固定为纯字母，既保留随机隔离，又确保路径本身不可能伪装成年份线索。
        _scratchSourceDir = Path.Combine(Path.GetTempPath(), $"pmm-e2e-src-{NewAlphabeticToken()}");
        _scratchTargetRoot = Path.Combine(Path.GetTempPath(), $"pmm-e2e-tgt-{NewAlphabeticToken()}");
        Directory.CreateDirectory(_scratchSourceDir);
        Directory.CreateDirectory(_scratchTargetRoot);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        try { Directory.Delete(_scratchSourceDir, recursive: true); } catch { }
        try { Directory.Delete(_scratchTargetRoot, recursive: true); } catch { }
        SetupGuardMiddleware.ResetCacheForTest();
    }

    private static string NewAlphabeticToken()
    {
        byte[] bytes = Guid.NewGuid().ToByteArray();
        char[] chars = new char[bytes.Length];
        for (int i = 0; i < bytes.Length; i++)
            chars[i] = (char)('a' + bytes[i] % 26);
        return new string(chars);
    }

    // ---------- E2E1：电影自动归档 ----------

    [Fact]
    public async Task E2E1_Movie_Inception_Auto_Archives_To_Plex_Path_With_Nfo_And_Webhook()
    {
        // 1. Setup
        await CompleteSetupAsync();
        long catId = await SeedMovieCategoryAsync(_scratchTargetRoot);
        long subId = await SeedWebhookSubscriptionAsync("e2e-sub", ["media.archived"]);
        // 25377f4 起 Webhook 全局总开关默认关（关闭时归档不产生任何 Webhook_Delivery）；
        // 本 E2E 断言投递落库，须先经真实端点把总开关打开
        await LoginAsAdminAsync();
        HttpResponseMessage enableResp = await _client.PostAsJsonAsync(
            "/api/settings/webhooks/enabled", new { enabled = true });
        enableResp.EnsureSuccessStatusCode();
        (await enableResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("code").GetInt32().Should().Be(0);

        // 2. Mock TMDB：单候选 + 详情；规则引擎能解析出 Inception 2010 movie
        ConfigureTmdbForMovie(tmdbId: 27205, title: "盗梦空间", originalTitle: "Inception", year: 2010,
            originCountry: ["US"], genres: """[{"id":28,"name":"动作"}]""");

        // 3. 写真实文件
        string srcFile = Path.Combine(_scratchSourceDir, "Inception.2010.1080p.BluRay.x264.mkv");
        await File.WriteAllBytesAsync(srcFile, new byte[1024]);

        // 4. 走 ProcessFileService（跳过 FileWatcher 异步层）
        ProcessFileOutcome outcome = await ProcessFileAsync(srcFile);

        // 5. 断言
        outcome.Outcome.Should().Be(ProcessOutcome.Completed);

        MediaItem media = await ReadMediaItemByIdAsync(outcome.MediaItemId);
        media.Status.Should().Be(MediaItemStatus.Completed);
        media.TmdbId.Should().Be(27205);
        media.CategoryId.Should().Be(catId);
        media.ArchivedAt.Should().NotBeNull();

        // 落点规范化后：文件夹与文件标题段统一用 TMDB 规范名（中文「盗梦空间」），不再跟随 RuleEngine 的英文解析名，
        // 保证同一 tmdbId 不因中英标题差异散落多文件夹（与 nfo 标题来源一致）。
        string expectedTarget = Path.Combine(_scratchTargetRoot, "盗梦空间 (2010) {tmdb-27205}", "盗梦空间 (2010) {tmdb-27205}.mkv");
        File.Exists(expectedTarget).Should().BeTrue();
        File.Exists(srcFile).Should().BeFalse("Move 已搬走源文件");

        string expectedNfo = Path.ChangeExtension(expectedTarget, ".nfo");
        File.Exists(expectedNfo).Should().BeTrue();
        (await File.ReadAllTextAsync(expectedNfo)).Should().Contain("<movie>").And.Contain("27205").And.Contain("盗梦空间");

        // Webhook_Delivery 行已写
        IReadOnlyList<WebhookDelivery> deliveries = await ReadWebhookDeliveriesAsync();
        deliveries.Should().ContainSingle().Which.SubscriptionId.Should().Be(subId);
        deliveries[0].Event.Should().Be("media.archived");
        deliveries[0].Payload.Should().Contain("\"tmdbId\":27205");
    }

    // ---------- E2E2：剧集多季归档（含字幕） ----------

    [Fact]
    public async Task E2E2_Tv_BreakingBad_S01E01_Archives_With_Subtitle()
    {
        await CompleteSetupAsync();
        long catId = await SeedTvCategoryAsync(_scratchTargetRoot);
        ConfigureTmdbForTv(tmdbId: 1396, title: "绝命毒师", originalTitle: "Breaking Bad", year: 2008,
            totalSeasons: 5, originCountry: ["US"], genres: """[{"id":18,"name":"剧情"}]""");

        string srcVideo = Path.Combine(_scratchSourceDir, "BreakingBad.S01E01.1080p.mkv");
        string srcSub = Path.Combine(_scratchSourceDir, "BreakingBad.S01E01.1080p.zh.srt");
        await File.WriteAllBytesAsync(srcVideo, new byte[1024]);
        await File.WriteAllTextAsync(srcSub, "1\n00:00:01,000 --> 00:00:02,000\n你好\n");

        ProcessFileOutcome outcome = await ProcessFileAsync(srcVideo);

        outcome.Outcome.Should().Be(ProcessOutcome.Completed);
        // 同 E2E1：落点规范化后文件夹与文件标题段用 TMDB 规范名（中文「绝命毒师」）
        string expectedTarget = Path.Combine(_scratchTargetRoot, "绝命毒师 (2008) {tmdb-1396}", "Season 01",
            "绝命毒师 (2008) - S01E01.mkv");

        // E2E2 此前偶发 flaky（File.Exists 报 false 但 outcome=Completed）；预先收集诊断上下文，下次失败可立刻定位
        // ParseSource / ParsedInfo / TargetPath 三字段决定文件实际归档位置，对比期望路径即可看出 fallback 路径分流
        string diag = await BuildArchiveDiagnosticsAsync(outcome.MediaItemId, expectedTarget);
        File.Exists(expectedTarget).Should().BeTrue(diag);

        string expectedSub = Path.Combine(_scratchTargetRoot, "绝命毒师 (2008) {tmdb-1396}", "Season 01",
            "绝命毒师 (2008) - S01E01.zh.srt");
        File.Exists(expectedSub).Should().BeTrue("同 stem 字幕应被同步重命名 + 移动 — " + diag);

        // 剧集根 nfo + 单集 nfo 双生成
        File.Exists(Path.Combine(_scratchTargetRoot, "绝命毒师 (2008) {tmdb-1396}", "tvshow.nfo")).Should().BeTrue();
        File.Exists(Path.ChangeExtension(expectedTarget, ".nfo")).Should().BeTrue();

        MediaItem media = await ReadMediaItemByIdAsync(outcome.MediaItemId);
        media.TmdbId.Should().Be(1396);
        media.TmdbMediaType.Should().Be("tv");
    }

    // ---------- E2E3：人工兜底 ----------

    [Fact]
    public async Task E2E3_RandomGarbage_Goes_To_AwaitingReview_Then_BindTmdb_Then_Confirm_Completes()
    {
        await CompleteSetupAsync();
        long catId = await SeedMovieCategoryAsync(_scratchTargetRoot);

        // TMDB 首次零结果；AI 失败 → AwaitingReview
        _tmdbStub.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult(Array.Empty<TmdbCandidate>(), null));
        _aiStub.ExecuteAsync(Arg.Any<AiParseRequest>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new AiCallOutcome(Success: false, Result: null, WinningProviderId: null,
                ProvidersAttempted: 2, FailureSummary: "all providers failed"));

        string src = Path.Combine(_scratchSourceDir, "RandomGarbage123.mkv");
        await File.WriteAllBytesAsync(src, new byte[1024]);

        ProcessFileOutcome firstRun = await ProcessFileAsync(src);
        firstRun.Outcome.Should().Be(ProcessOutcome.AwaitingReview);
        MediaItem media = await ReadMediaItemByIdAsync(firstRun.MediaItemId);
        media.Status.Should().Be(MediaItemStatus.AwaitingReview);

        // bind-tmdb：用户手动绑定 → 不改 status
        ConfigureTmdbForMovie(tmdbId: 27205, title: "盗梦空间", originalTitle: "Inception", year: 2010,
            originCountry: ["US"], genres: "[]");

        await LoginAsAdminAsync();
        HttpResponseMessage bindResp = await _client.PostAsJsonAsync(
            $"/api/review/{firstRun.MediaItemId}/bind-tmdb",
            new { tmdbId = 27205, mediaType = "movie", rowVersion = media.RowVersion });
        bindResp.EnsureSuccessStatusCode();
        JsonElement bindBody = await bindResp.Content.ReadFromJsonAsync<JsonElement>();
        bindBody.GetProperty("code").GetInt32().Should().Be(0);
        long bindRv = bindBody.GetProperty("data").GetProperty("rowVersion").GetInt64();

        MediaItem afterBind = await ReadMediaItemByIdAsync(firstRun.MediaItemId);
        afterBind.Status.Should().Be(MediaItemStatus.AwaitingReview, "bind-tmdb 不改 status");
        afterBind.TmdbId.Should().Be(27205);

        // confirm：触发归档（同步等）→ Completed
        HttpResponseMessage confResp = await _client.PostAsJsonAsync(
            $"/api/review/{firstRun.MediaItemId}/confirm",
            new { tmdbId = 27205, mediaType = "movie", categoryId = catId, title = "盗梦空间", year = 2010, rowVersion = bindRv });
        confResp.EnsureSuccessStatusCode();
        JsonElement confBody = await confResp.Content.ReadFromJsonAsync<JsonElement>();
        confBody.GetProperty("code").GetInt32().Should().Be(0);

        MediaItem final = await ReadMediaItemByIdAsync(firstRun.MediaItemId);
        final.Status.Should().Be(MediaItemStatus.Completed, "ArchiveService 同步成功后转 Completed");
        // confirm 时 client 传入 title="盗梦空间"，MergeOrCreateParsedInfo 用客户端值，所以归档目录走中文
        File.Exists(Path.Combine(_scratchTargetRoot, "盗梦空间 (2010) {tmdb-27205}", "盗梦空间 (2010) {tmdb-27205}.mkv")).Should().BeTrue();
    }

    // ---------- helpers ----------

    private async Task CompleteSetupAsync()
    {
        await _client.PostAsJsonAsync("/api/setup/admin", new { username = "admin", password = "secret123" });
        await _client.PostAsJsonAsync("/api/setup/complete", new { });
    }

    private async Task LoginAsAdminAsync()
    {
        HttpResponseMessage resp = await _client.PostAsJsonAsync("/api/auth/login",
            new { username = "admin", password = "secret123" });
        JsonElement body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        string token = body.GetProperty("data").GetProperty("token").GetString()!;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
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

    private async Task<long> SeedTvCategoryAsync(string root)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        IDbContextFactory<PmmDbContext> f = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PmmDbContext>>();
        await using PmmDbContext db = await f.CreateDbContextAsync();
        CategoryDefinition c = new() { Name = "剧集", MediaType = MediaType.Tv, TargetRoot = root };
        db.CategoryDefinitions.Add(c);
        await db.SaveChangesAsync();
        _classifyTargetCategoryId = c.Id;
        return c.Id;
    }

    private async Task<long> SeedWebhookSubscriptionAsync(string name, string[] events)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        IDbContextFactory<PmmDbContext> f = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PmmDbContext>>();
        await using PmmDbContext db = await f.CreateDbContextAsync();
        var sub = new PersonalMediaManager.Domain.Aggregates.WebhookSubscriptions.WebhookSubscription
        {
            Name = name,
            Url = "https://hook.test/x",
            Events = events.ToList(),
            Enabled = true,
            TimeoutSeconds = 10,
        };
        db.WebhookSubscriptions.Add(sub);
        await db.SaveChangesAsync();
        return sub.Id;
    }

    private void ConfigureTmdbForMovie(int tmdbId, string title, string originalTitle, int year,
        string[] originCountry, string genres)
    {
        TmdbCandidate cand = new(tmdbId, "movie", title, originalTitle, year, 50.0, "en", originCountry, "/p.jpg", null);
        _tmdbStub.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult(new[] { cand }, null));
        // 把 details 落到缓存，让 ClassifyService / ArchiveService 能读到
        SeedTmdbCache(tmdbId, "movie", title, originalTitle, year, originCountry, genres, totalSeasons: null);
        _tmdbStub.GetDetailsAsync(tmdbId, "movie", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(tmdbId, "movie", title, originalTitle, year, null, "/p.jpg", originCountry, "en", null, null, "{}"));
    }

    private void ConfigureTmdbForTv(int tmdbId, string title, string originalTitle, int year,
        int totalSeasons, string[] originCountry, string genres)
    {
        TmdbCandidate cand = new(tmdbId, "tv", title, originalTitle, year, 50.0, "en", originCountry, "/p.jpg", null);
        _tmdbStub.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult(new[] { cand }, null));
        SeedTmdbCache(tmdbId, "tv", title, originalTitle, year, originCountry, genres, totalSeasons);
        _tmdbStub.GetDetailsAsync(tmdbId, "tv", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(tmdbId, "tv", title, originalTitle, year, totalSeasons, "/p.jpg", originCountry, "en", null, null, "{}"));
    }

    private void SeedTmdbCache(int tmdbId, string mediaType, string title, string originalTitle, int year,
        string[] originCountry, string genres, int? totalSeasons)
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
            TotalSeasons = totalSeasons,
            OriginCountry = originCountry.ToList(),
            OriginalLanguage = "en",
            Genres = genres,
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
        return await svc.ProcessAsync(new PendingFileItem(fullPath, WatchFolderId: 0, PendingFileSource.Manual), CancellationToken.None);
    }

    private async Task<MediaItem> ReadMediaItemByIdAsync(long id)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        IDbContextFactory<PmmDbContext> f = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PmmDbContext>>();
        await using PmmDbContext db = await f.CreateDbContextAsync();
        return await db.MediaItems.AsNoTracking().SingleAsync(m => m.Id == id);
    }

    /// <summary>归档断言失败时的诊断上下文：MediaItem 关键字段 + 期望路径状态 + _scratchTargetRoot 实际目录树</summary>
    /// <remarks>
    /// 设计目的：D9 E2E 偶发失败（outcome=Completed 但 expectedTarget 文件不存在）时，让 CI 日志直接看出
    /// ParseSource（Rule/Ai/Tmdb fallback 路径）与 TargetPath（实际归档去哪），对比 expected 路径即可定位 title 分流。
    /// 即使 happy path 也跑（read-only 操作，&lt; 100ms），保证失败时上下文不丢。
    /// </remarks>
    private async Task<string> BuildArchiveDiagnosticsAsync(long mediaItemId, string expectedTarget)
    {
        System.Text.StringBuilder sb = new();
        sb.AppendLine();
        sb.AppendLine("=== D9 归档诊断 ===");
        sb.AppendLine($"期望路径    : {expectedTarget}");
        sb.AppendLine($"  存在       : {File.Exists(expectedTarget)}");
        sb.AppendLine($"  父目录存在 : {Directory.Exists(Path.GetDirectoryName(expectedTarget))}");

        try
        {
            MediaItem media = await ReadMediaItemByIdAsync(mediaItemId);
            sb.AppendLine($"MediaItem #{mediaItemId}:");
            sb.AppendLine($"  SourcePath    : {media.SourcePath}");
            sb.AppendLine($"  Status        : {media.Status}");
            sb.AppendLine($"  ParseSource   : {media.ParseSource}");
            sb.AppendLine($"  Confidence    : {media.Confidence}");
            sb.AppendLine($"  ParsedInfo    : {media.ParsedInfo}");
            sb.AppendLine($"  TmdbId/Type   : {media.TmdbId}/{media.TmdbMediaType}");
            sb.AppendLine($"  CategoryId    : {media.CategoryId}");
            sb.AppendLine($"  TargetPath    : {media.TargetPath}");
            sb.AppendLine($"  ErrorMessage  : {media.ErrorMessage}");
            sb.AppendLine($"  ArchivedAt    : {media.ArchivedAt:O}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"读 MediaItem 失败: {ex.Message}");
        }

        if (Directory.Exists(_scratchTargetRoot))
        {
            sb.AppendLine($"_scratchTargetRoot 实际内容（{_scratchTargetRoot}）:");
            try
            {
                foreach (string f in Directory.EnumerateFiles(_scratchTargetRoot, "*", SearchOption.AllDirectories))
                {
                    sb.AppendLine($"  {Path.GetRelativePath(_scratchTargetRoot, f)}");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  枚举失败: {ex.Message}");
            }
        }
        else
        {
            sb.AppendLine($"_scratchTargetRoot 不存在: {_scratchTargetRoot}");
        }

        return sb.ToString();
    }

    private async Task<IReadOnlyList<WebhookDelivery>> ReadWebhookDeliveriesAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        IDbContextFactory<PmmDbContext> f = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PmmDbContext>>();
        await using PmmDbContext db = await f.CreateDbContextAsync();
        return await db.WebhookDeliveries.AsNoTracking().ToListAsync();
    }
}
