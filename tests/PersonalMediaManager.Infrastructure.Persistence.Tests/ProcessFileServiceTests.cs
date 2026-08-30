using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Archive;
using PersonalMediaManager.Application.Services.Classify;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Application.Services.Tmdb;
using PersonalMediaManager.Application.Services.Webhook;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Aggregates.WatchDirectories;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Persistence.Services.Parse;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>ProcessFileService（D7.1）— 状态机编排全分支覆盖</summary>
/// <remarks>
/// 覆盖矩阵（与 MediaItem.AllowedTransitions §6.2 对齐）：
///   1. 写入未完成 → Skipped（不创建 MediaItem）
///   2. 已有终态 MediaItem → 幂等 Skipped
///   3. 规则高置信 → TmdbMatching → 候选合规 → Classifying → Archiving → Completed
///   4. 规则高置信 → TmdbMatching → 0 候选 → AiParsing → TmdbRematching → Classifying → Completed
///   5. 规则高置信 → TmdbMatching → &gt;N 候选 → AiParsing 分支
///   6. 规则低置信 → 直走 AiParsing
///   7. 规则有特殊字符 → 直走 AiParsing（即使置信度高）
///   8. AI 失败 → TmdbRematching → AwaitingReview
///   9. AI 成功 → 二次 TMDB 候选过多 → AwaitingReview
///  10. 分类无命中 → AwaitingReview
///  11. 归档同名冲突 → Skipped
///  12. 异常路径 → Failed（MarkFailed）
///  13. 失败状态 MediaItem 重入 → 幂等 Skipped
/// </remarks>
public sealed class ProcessFileServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly IWriteCompletionDetector _writeDetector;
    private readonly IRuleEngineService _ruleEngine;
    private readonly IForcedMatchMarkerStore _forcedMatch;
    private readonly ITmdbSearchService _tmdb;
    private readonly IAiCallOrchestrator _aiOrchestrator;
    private readonly IClassifyService _classify;
    private readonly IArchiveService _archive;
    private readonly IFileHasher _fileHasher;
    private readonly IFileProbe _fileProbe;
    private readonly IMediaAudioProbe _audioProbe;
    private readonly IFolderSeriesCache _folderCache;
    private readonly IWebhookEmitter _webhook;
    private readonly string _tempFile;

    public ProcessFileServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbFactory = new TestDbContextFactory(_connection);
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ctx.Database.EnsureCreated();

        _writeDetector = Substitute.For<IWriteCompletionDetector>();
        _writeDetector.WaitUntilCompleteAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(true); // 默认写入完成；不需要时单测覆写

        _ruleEngine = Substitute.For<IRuleEngineService>();
        _forcedMatch = Substitute.For<IForcedMatchMarkerStore>();
        // 默认无强制匹配标识：返回 null，既有用例走正常解析流程不受影响；强制匹配专测单独覆写
        _forcedMatch.TryReadAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>())
            .Returns((ForcedMatchMarker?)null);
        _tmdb = Substitute.For<ITmdbSearchService>();
        _aiOrchestrator = Substitute.For<IAiCallOrchestrator>();
        _classify = Substitute.For<IClassifyService>();
        _archive = Substitute.For<IArchiveService>();
        _fileHasher = Substitute.For<IFileHasher>();
        // 默认返回 null：不触发内容去重，保持既有用例流程不变；去重专测单独覆写
        _fileHasher.TryComputeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        _fileProbe = Substitute.For<IFileProbe>();
        // 默认：任何路径都视为存在（去重命中时认为归档副本仍在）；防丢专测覆写为 false
        _fileProbe.FileExists(Arg.Any<string>()).Returns(true);
        _audioProbe = Substitute.For<IMediaAudioProbe>();
        // 默认探测不可用（音频检查开关种子默认关，正常不会触发探测；此默认仅为防御性降级，绝不构造重混计划）
        _audioProbe.ProbeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<CancellationToken>())
            .Returns(AudioProbeResult.Unavailable("test default"));
        _folderCache = new InMemoryFolderSeriesCache();
        _webhook = Substitute.For<IWebhookEmitter>();

        // 临时文件让 new FileInfo(...).Length 返回 0（FileInfo 不存在也不抛）
        _tempFile = Path.Combine(Path.GetTempPath(), $"pmm-pfs-{Guid.NewGuid():N}.mkv");
    }

    public void Dispose()
    {
        _connection.Dispose();
        try { if (File.Exists(_tempFile)) File.Delete(_tempFile); } catch { }
    }

    // ---------- 1. 写入未完成 → Skipped 不入库 ----------
    [Fact]
    public async Task WriteIncomplete_Returns_Skipped_NoMediaItem_Created()
    {
        _writeDetector.WaitUntilCompleteAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(false);

        ProcessFileOutcome r = await Run();
        r.Outcome.Should().Be(ProcessOutcome.Skipped);
        r.MediaItemId.Should().Be(0);

        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.MediaItems.Count().Should().Be(0);
    }

    // ---------- 2. 已有终态 → 幂等 Skipped ----------
    [Theory]
    [InlineData(MediaItemStatus.Completed)]
    [InlineData(MediaItemStatus.Skipped)]
    [InlineData(MediaItemStatus.Ignored)]
    [InlineData(MediaItemStatus.Cancelled)]
    [InlineData(MediaItemStatus.Failed)]
    public async Task Existing_Terminal_MediaItem_Returns_Skipped_NoPipelineCall(MediaItemStatus terminal)
    {
        long id = SeedMediaItemAtStatus(terminal);

        ProcessFileOutcome r = await Run();
        r.Outcome.Should().Be(ProcessOutcome.Skipped);
        r.MediaItemId.Should().Be(id);

        await _ruleEngine.DidNotReceive().ParseAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>());
        await _tmdb.DidNotReceive().SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>());
    }

    // ---------- 3. 规则高置信 + TMDB 1 候选 → Completed ----------
    [Fact]
    public async Task HighRule_OneTmdbCandidate_Goes_Through_To_Completed()
    {
        ConfigureRule(confidence: 0.9, hasSpecialChars: false);
        ConfigureTmdb(candidates: 1);
        ConfigureClassify(ClassifyDecision.Matched, categoryId: 7);
        ConfigureArchive(ArchiveOutcome.Completed, targetPath: "/M/Inception (2010)/Inception (2010).mkv");

        ProcessFileOutcome r = await Run();
        r.Outcome.Should().Be(ProcessOutcome.Completed);
        MediaItem m = ReadOne();
        m.Status.Should().Be(MediaItemStatus.Completed);
        m.TmdbId.Should().Be(100); // ConfigureTmdb 第一个候选 id=100
        m.ParseSource.Should().Be(ParseSource.Rule);
        m.CategoryId.Should().Be(7);
        m.TargetPath.Should().Be("/M/Inception (2010)/Inception (2010).mkv");
        m.ArchivedAt.Should().NotBeNull();
        await _aiOrchestrator.DidNotReceive().ExecuteAsync(Arg.Any<AiParseRequest>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    // ---------- 3b. 归档前拦截开关：高置信命中分类也转 AwaitingReview（不归档）----------
    [Fact]
    public async Task HoldBeforeArchive_Enabled_HighConfidence_Goes_To_AwaitingReview_Not_Archived()
    {
        SeedSetting("Archive_HoldBeforeArchive", "true");
        ConfigureRule(confidence: 0.9, hasSpecialChars: false);
        ConfigureTmdb(candidates: 1);
        ConfigureClassify(ClassifyDecision.Matched, categoryId: 7);
        // 不配置 archive：拦截发生在归档前，IArchiveService 绝不应被调用

        ProcessFileOutcome r = await Run();

        r.Outcome.Should().Be(ProcessOutcome.AwaitingReview);
        MediaItem m = ReadOne();
        m.Status.Should().Be(MediaItemStatus.AwaitingReview);
        m.ReviewReason.Should().Be(ReviewReason.HoldBeforeArchive);
        m.TmdbId.Should().Be(100);          // 匹配已就绪（候选已选定）
        m.CategoryId.Should().Be(7);        // 分类已定
        m.TargetPath.Should().BeNull();     // 未归档
        m.ArchivedAt.Should().BeNull();
        m.TmdbCandidatesJson.Should().NotBeNull();   // 候选全集落库供审核页复核换选
        await _archive.DidNotReceive().ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>());
    }

    // ---------- 3c. 归档前拦截显式关闭：仍正常归档到 Completed（对照）----------
    [Fact]
    public async Task HoldBeforeArchive_ExplicitFalse_Still_Archives_To_Completed()
    {
        SeedSetting("Archive_HoldBeforeArchive", "false");
        ConfigureRule(confidence: 0.9, hasSpecialChars: false);
        ConfigureTmdb(candidates: 1);
        ConfigureClassify(ClassifyDecision.Matched, categoryId: 7);
        ConfigureArchive(ArchiveOutcome.Completed, targetPath: "/M/x.mkv");

        ProcessFileOutcome r = await Run();

        r.Outcome.Should().Be(ProcessOutcome.Completed);
        ReadOne().Status.Should().Be(MediaItemStatus.Completed);
        await _archive.Received(1).ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>());
    }

    /// <summary>往共享 in-memory DB 写一条 System_Setting（拦截开关等 KV）；service 经同一 connection 读得到</summary>
    private void SeedSetting(string key, string value)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.SystemSettings.Add(new SystemSetting { Key = key, Value = value, Category = "Archive", Description = "test" });
        db.SaveChanges();
    }

    // ---------- 4. 规则高置信 + TMDB 0 候选但备选拿到过候选 → AI 兜底 → Completed ----------
    [Fact]
    public async Task HighRule_ZeroTmdbCandidates_Falls_To_Ai_Then_Completes()
    {
        // 主标题零候选、备选标题搜到多候选（>N 不可直接采纳，主查非多候选也不投票）：
        // 「全程零候选」不成立 → 不触发「TMDB 未收录跳 AI」拦截，照常走 AI 兜底 → 二次 TMDB 走完
        ConfigureRule(confidence: 0.9, hasSpecialChars: false, alternativeTitles: ["Sample Alt"]);
        _tmdb.SearchAsync(Arg.Is<TmdbSearchRequest>(r => r.Query == "Sample"), Arg.Any<CancellationToken>())
             .Returns(new TmdbSearchResult([], null));                       // 主标题：零候选
        _tmdb.SearchAsync(Arg.Is<TmdbSearchRequest>(r => r.Query == "Sample Alt"), Arg.Any<CancellationToken>())
             .Returns(new TmdbSearchResult(Enumerable.Range(1, 5).Select(i => NewCandidate(300 + i, "movie")).ToList(), null)); // 备选：5 个 >N
        _tmdb.SearchAsync(Arg.Is<TmdbSearchRequest>(r => r.Query == "AI-Title"), Arg.Any<CancellationToken>())
             .Returns(new TmdbSearchResult([NewCandidate(202, "movie", "AI-Title", 2011)], null));   // AI 后二次
        ConfigureAi(success: true);
        ConfigureClassify(ClassifyDecision.Matched, 5);
        ConfigureArchive(ArchiveOutcome.Completed, "/X.mkv");

        ProcessFileOutcome r = await Run();
        r.Outcome.Should().Be(ProcessOutcome.Completed);
        MediaItem m = ReadOne();
        m.ParseSource.Should().Be(ParseSource.Ai);
        m.TmdbId.Should().Be(202);
        m.AiInvolved.Should().BeTrue();
        await _aiOrchestrator.Received(1).ExecuteAsync(Arg.Any<AiParseRequest>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    // ---------- 4b. 规则高置信 + 主标题与全部备选均零候选 → 判定 TMDB 未收录：跳过 AI 转 AwaitingReview ----------
    [Fact]
    public async Task HighRule_AllQueriesZero_Skips_Ai_Goes_To_AwaitingReview_TmdbZeroResult()
    {
        ConfigureRule(confidence: 0.9, hasSpecialChars: false, alternativeTitles: ["Sample Alt"]);
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
             .Returns(new TmdbSearchResult([], null));   // 主标题 + 备选全部零候选

        ProcessFileOutcome r = await Run();

        r.Outcome.Should().Be(ProcessOutcome.AwaitingReview);
        MediaItem m = ReadOne();
        m.Status.Should().Be(MediaItemStatus.AwaitingReview);
        m.ReviewReason.Should().Be(ReviewReason.TmdbZeroResult);
        m.AiInvolved.Should().BeFalse();   // 未烧 AI —— TmdbZeroResultRetryJob 每日自动重投的筛选口径
        await _aiOrchestrator.DidNotReceive().ExecuteAsync(Arg.Any<AiParseRequest>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    // ---------- 4c. 无备选标题时同样适用：高置信 + 主标题零候选 → 跳 AI 待自动重试 ----------
    [Fact]
    public async Task HighRule_ZeroCandidates_NoAlternatives_Skips_Ai_Goes_To_AwaitingReview()
    {
        ConfigureRule(confidence: 0.9, hasSpecialChars: false);
        ConfigureTmdb(candidates: 0);

        ProcessFileOutcome r = await Run();

        r.Outcome.Should().Be(ProcessOutcome.AwaitingReview);
        MediaItem m = ReadOne();
        m.ReviewReason.Should().Be(ReviewReason.TmdbZeroResult);
        m.AiInvolved.Should().BeFalse();
        await _aiOrchestrator.DidNotReceive().ExecuteAsync(Arg.Any<AiParseRequest>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    // ---------- 4d. 混排标题全零候选不适用跳 AI（AI 的标题清洗 + 检索别名有真实增量）----------
    [Fact]
    public async Task SpecialChars_AllZero_Still_Calls_Ai()
    {
        ConfigureRule(confidence: 0.9, hasSpecialChars: true, alternativeTitles: ["Mixed Alt"]);
        _tmdb.SearchAsync(Arg.Is<TmdbSearchRequest>(r => r.Query == "Mixed Alt"), Arg.Any<CancellationToken>())
             .Returns(new TmdbSearchResult([], null));    // 备选零候选（混排不查主标题，直达备选重搜）
        _tmdb.SearchAsync(Arg.Is<TmdbSearchRequest>(r => r.Query == "AI-Title"), Arg.Any<CancellationToken>())
             .Returns(new TmdbSearchResult([NewCandidate(404, "movie", "AI-Title", 2011)], null));
        ConfigureAi(success: true);
        ConfigureClassify(ClassifyDecision.Matched, 5);
        ConfigureArchive(ArchiveOutcome.Completed, "/M/m.mkv");

        ProcessFileOutcome r = await Run();

        r.Outcome.Should().Be(ProcessOutcome.Completed);
        ReadOne().TmdbId.Should().Be(404);
        await _aiOrchestrator.Received(1).ExecuteAsync(Arg.Any<AiParseRequest>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    // ---------- 5. 规则高置信 + TMDB >N 候选 → AI 兜底 ----------
    [Fact]
    public async Task HighRule_TooManyTmdbCandidates_Falls_To_Ai()
    {
        ConfigureRule(confidence: 0.9, hasSpecialChars: false);
        // 候选用 movie 避免触发「剧集字段不全」守护——本测试只关心「>N → AI 兜底」分支
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
             .Returns(new TmdbSearchResult(Enumerable.Range(1, 10).Select(i => NewCandidate(i, "movie")).ToList(), null),
                      new TmdbSearchResult([NewCandidate(303, "movie")], null));
        ConfigureAi(success: true);
        ConfigureClassify(ClassifyDecision.Matched, 5);
        ConfigureArchive(ArchiveOutcome.Completed, "/Y.mkv");

        ProcessFileOutcome r = await Run();
        r.Outcome.Should().Be(ProcessOutcome.Completed);
        await _aiOrchestrator.Received(1).ExecuteAsync(Arg.Any<AiParseRequest>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    // ---------- 6. 规则低置信 → 直走 AI（不查首次 TMDB） ----------
    [Fact]
    public async Task LowRule_Confidence_Skips_FirstTmdb_DirectAi()
    {
        ConfigureRule(confidence: 0.3, hasSpecialChars: false);
        ConfigureTmdb(candidates: 1); // 这是 AI 之后的二次 TMDB
        ConfigureAi(success: true);
        ConfigureClassify(ClassifyDecision.Matched, 1);
        ConfigureArchive(ArchiveOutcome.Completed, "/Z.mkv");

        await Run();
        // 验证只调过一次 TMDB（二次）
        await _tmdb.Received(1).SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>());
        MediaItem m = ReadOne();
        m.Status.Should().Be(MediaItemStatus.Completed);
        m.ParseSource.Should().Be(ParseSource.Ai);
    }

    // ---------- 7. 特殊字符 → 直走 AI ----------
    [Fact]
    public async Task SpecialChars_ForceAi_Even_With_HighConfidence()
    {
        ConfigureRule(confidence: 0.95, hasSpecialChars: true);
        ConfigureTmdb(candidates: 1);
        ConfigureAi(success: true);
        ConfigureClassify(ClassifyDecision.Matched, 1);
        ConfigureArchive(ArchiveOutcome.Completed, "/W.mkv");

        await Run();
        await _tmdb.Received(1).SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>());
        await _aiOrchestrator.Received(1).ExecuteAsync(Arg.Any<AiParseRequest>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    // ---------- 8. AI 失败 → AwaitingReview ----------
    [Fact]
    public async Task Ai_Failure_Goes_To_AwaitingReview_Via_TmdbRematching()
    {
        ConfigureRule(confidence: 0.3, hasSpecialChars: false);
        _aiOrchestrator.ExecuteAsync(Arg.Any<AiParseRequest>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new AiCallOutcome(false, null, null, 2, "all providers failed"));

        ProcessFileOutcome r = await Run();
        r.Outcome.Should().Be(ProcessOutcome.AwaitingReview);
        MediaItem m = ReadOne();
        m.Status.Should().Be(MediaItemStatus.AwaitingReview);
        m.ErrorMessage.Should().Be("all providers failed");
        // 二次 TMDB 不应被调（AI 都没成功，无需再查）
        await _tmdb.DidNotReceive().SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>());
    }

    // ---------- 9. AI 成功 + 二次 TMDB 候选过多 → AwaitingReview ----------
    [Fact]
    public async Task Ai_Success_But_SecondTmdb_TooMany_Goes_To_AwaitingReview()
    {
        ConfigureRule(confidence: 0.3, hasSpecialChars: false);
        ConfigureAi(success: true);
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult(Enumerable.Range(1, 10).Select(i => NewCandidate(i, "movie")).ToList(), null));

        ProcessFileOutcome r = await Run();
        r.Outcome.Should().Be(ProcessOutcome.AwaitingReview);
        MediaItem m = ReadOne();
        m.Status.Should().Be(MediaItemStatus.AwaitingReview);
        m.ReviewReason.Should().Be(ReviewReason.TmdbMultiCandidate);
        // 多候选全集应持久化进 TmdbCandidatesJson，供审核页单选（本次核心修复点）
        m.TmdbCandidatesJson.Should().NotBeNullOrEmpty();
        m.TmdbCandidatesJson.Should().Contain("\"tmdbId\":1");
    }

    // ---------- 9b. 中文 title 二次搜索零结果但 AI 别名命中 → Completed（第 0 层国漫/日漫元数据兜底）----------
    [Fact]
    public async Task Ai_Success_SecondTmdbMiss_But_AliasHit_Goes_To_Completed()
    {
        ConfigureRule(confidence: 0.3, hasSpecialChars: false);
        // 模拟日漫/国漫：AI 给中文片名 + 原名别名；TMDB 主条目是原名，中文名搜不到
        ConfigureAi(success: true, title: "中文片名", mediaType: "movie", aliases: ["Original Name"]);
        // 默认任何查询零结果；唯独别名「Original Name」搜得唯一候选
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult([], null));
        _tmdb.SearchAsync(Arg.Is<TmdbSearchRequest>(r => r.Query == "Original Name"), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult([NewCandidate(42, "movie")], null));
        ConfigureClassify(ClassifyDecision.Matched, 1);
        ConfigureArchive(ArchiveOutcome.Completed, "/test/root/out.mkv");

        ProcessFileOutcome r = await Run();

        r.Outcome.Should().Be(ProcessOutcome.Completed);
        MediaItem m = ReadOne();
        m.Status.Should().Be(MediaItemStatus.Completed);
        m.TmdbId.Should().Be(42);
        // 中文片名（默认零结果）+ 别名 Original Name（命中）各搜一次
        await _tmdb.Received().SearchAsync(Arg.Is<TmdbSearchRequest>(x => x.Query == "中文片名"), Arg.Any<CancellationToken>());
        await _tmdb.Received().SearchAsync(Arg.Is<TmdbSearchRequest>(x => x.Query == "Original Name"), Arg.Any<CancellationToken>());
    }

    // ---------- 9c. 中文名与别名都搜不中 → 仍 AwaitingReview（兜底失败安全回落，reason 仍取首搜）----------
    [Fact]
    public async Task Ai_Success_AliasAlsoMiss_Still_Goes_To_AwaitingReview()
    {
        ConfigureRule(confidence: 0.3, hasSpecialChars: false);
        ConfigureAi(success: true, title: "中文片名", mediaType: "movie", aliases: ["Original Name", "Another"]);
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult([], null));

        ProcessFileOutcome r = await Run();

        r.Outcome.Should().Be(ProcessOutcome.AwaitingReview);
        MediaItem m = ReadOne();
        m.Status.Should().Be(MediaItemStatus.AwaitingReview);
        m.ReviewReason.Should().Be(ReviewReason.TmdbZeroResult);
    }

    // ---------- 10. 分类无命中 → AwaitingReview ----------
    [Fact]
    public async Task Classify_SendToReview_Goes_To_AwaitingReview()
    {
        ConfigureRule(confidence: 0.9, hasSpecialChars: false);
        ConfigureTmdb(candidates: 1);
        ConfigureClassify(ClassifyDecision.SendToReview, null);

        ProcessFileOutcome r = await Run();
        r.Outcome.Should().Be(ProcessOutcome.AwaitingReview);
        ReadOne().Status.Should().Be(MediaItemStatus.AwaitingReview);
        await _archive.DidNotReceive().ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>());
    }

    // ---------- 10b. 剧集解析缺 season → AwaitingReview（ParseIncomplete 守护） ----------
    [Fact]
    public async Task Tv_MissingSeason_AfterTmdb_Goes_To_AwaitingReview_NotFailed()
    {
        // 复现真实日志样本：JJK 这类「整条路径无 season 标记」的剧集
        // 规则置信度高 → 跳过 AI 走 TMDB；TMDB 命中 tv 候选；但 ParsedInfo.Season=null
        // 期望守护把流程在 Archive 前截断，转 AwaitingReview，而不是让 ArchiveService 抛 BusinessException → Failed
        ConfigureRule(confidence: 0.9, hasSpecialChars: false,
            year: null, title: "Jujutsu Kaisen", season: null, episode: 59);
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult([NewCandidate(95479, "tv")], null));

        ProcessFileOutcome r = await Run();

        r.Outcome.Should().Be(ProcessOutcome.AwaitingReview);
        MediaItem m = ReadOne();
        m.Status.Should().Be(MediaItemStatus.AwaitingReview);
        m.ReviewReason.Should().Be(ReviewReason.ParseIncomplete);
        // 守护在 Archive 前截断；Classify / Archive 都不应被调
        await _classify.DidNotReceive().ClassifyAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>());
        await _archive.DidNotReceive().ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Tv_MissingEpisode_AfterTmdb_Also_Goes_To_AwaitingReview()
    {
        // 反向边界：只有 season 没 episode（少见但合法路径），守护同样应触发
        ConfigureRule(confidence: 0.9, hasSpecialChars: false,
            year: null, title: "Some Show", season: 2, episode: null);
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult([NewCandidate(12345, "tv")], null));

        ProcessFileOutcome r = await Run();

        r.Outcome.Should().Be(ProcessOutcome.AwaitingReview);
        ReadOne().ReviewReason.Should().Be(ReviewReason.ParseIncomplete);
    }

    [Fact]
    public async Task Tv_MissingSeason_But_SingleSeason_Tmdb_AutoFills_S01_And_Archives()
    {
        // 剧集缺季号但集号在；TMDB 仅 1 季 → 自动定为 S01 继续归档，不进 AwaitingReview
        _ruleEngine.ParseAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>())
            .Returns(new RuleParseResult("Jujutsu Kaisen", null, "tv", null, 59, null, 0.9, false, 1));
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult([NewCandidate(95479, "tv")], null));
        _tmdb.GetDetailsAsync(95479, "tv", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(95479, "tv", "Jujutsu Kaisen", "呪術廻戦", 2020, 1, null, ["JP"], "ja", null, null, "{}"));
        ConfigureClassify(ClassifyDecision.Matched, 1);
        ConfigureArchive(ArchiveOutcome.Completed, "/Tv/JJK/Season 01/S01E59.mkv");

        ProcessFileOutcome r = await Run();

        r.Outcome.Should().Be(ProcessOutcome.Completed);
        MediaItem m = ReadOne();
        m.Status.Should().Be(MediaItemStatus.Completed);
        m.ParsedInfo.Should().Contain("\"season\":1");
        await _archive.Received(1).ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Tv_MissingSeason_With_MultiSeason_Tmdb_Still_Goes_To_AwaitingReview()
    {
        // 多季剧集缺季号 → 不自动补，仍进人工队列让用户选季
        _ruleEngine.ParseAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>())
            .Returns(new RuleParseResult("Multi Show", null, "tv", null, 5, null, 0.9, false, 1));
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult([NewCandidate(55555, "tv")], null));
        _tmdb.GetDetailsAsync(55555, "tv", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(55555, "tv", "Show", "Show", 2018, 4, null, ["US"], "en", null, null, "{}"));

        ProcessFileOutcome r = await Run();

        r.Outcome.Should().Be(ProcessOutcome.AwaitingReview);
        MediaItem m = ReadOne();
        m.ReviewReason.Should().Be(ReviewReason.ParseIncomplete);
        await _archive.DidNotReceive().ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Movie_Without_Season_Episode_NOT_Triggered_By_Guard()
    {
        // 反例校验：电影没有 season/episode 是正常的，守护不应误伤
        ConfigureRule(confidence: 0.9, hasSpecialChars: false,
            year: 2010, title: "Inception", season: null, episode: null);
        ConfigureTmdb(candidates: 1); // 默认 type=movie
        ConfigureClassify(ClassifyDecision.Matched, 1);
        ConfigureArchive(ArchiveOutcome.Completed, "/M/Inception (2010)/Inception (2010).mkv");

        ProcessFileOutcome r = await Run();

        r.Outcome.Should().Be(ProcessOutcome.Completed);
        ReadOne().Status.Should().Be(MediaItemStatus.Completed);
    }

    // ---------- 11. 归档同名冲突 → Skipped（不把他人文件写入 TargetPath） ----------
    [Fact]
    public async Task Archive_ConflictSkipped_Goes_To_Skipped()
    {
        ConfigureRule(confidence: 0.9, hasSpecialChars: false);
        ConfigureTmdb(candidates: 1);
        ConfigureClassify(ClassifyDecision.Matched, 1);
        ConfigureArchive(ArchiveOutcome.ConflictSkipped, "/CONFLICT.mkv");

        ProcessFileOutcome r = await Run();
        r.Outcome.Should().Be(ProcessOutcome.Skipped);
        MediaItem m = ReadOne();
        m.Status.Should().Be(MediaItemStatus.Skipped);
        // 冲突目标是「他人产物」（本记录未做任何文件操作）：绝不持久化进 TargetPath，
        // 否则启动恢复 / 撤销归档会把他人文件误当本记录归档产物处理
        m.TargetPath.Should().BeNull("冲突目标是已存在的他人文件，不应记为本记录产物");

        // 冲突目标仅保留在时间线步骤 detail 供排查
        using PmmDbContext db = _dbFactory.CreateDbContext();
        ProcessStep archivingStep = db.ProcessSteps.AsNoTracking().Single(s => s.Stage == MediaItemStatus.Archiving);
        archivingStep.Detail.Should().Contain("/CONFLICT.mkv");
    }

    // ---------- 11.5 归档落地但元数据失败（ArchiveResult.Warnings）→ Completed 而非 Failed ----------
    [Fact(DisplayName = "归档视频已落地但 nfo/Webhook 失败（带 Warnings）→ Completed 不判 Failed，时间线标记待补元数据")]
    public async Task Archive_With_MetadataWarnings_Completes_Not_Failed_And_Marks_Pending()
    {
        ConfigureRule(confidence: 0.9, hasSpecialChars: false, year: 2010, title: "Inception", season: null, episode: null);
        ConfigureTmdb(candidates: 1);
        ConfigureClassify(ClassifyDecision.Matched, 7);
        ConfigureArchive(ArchiveOutcome.Completed, "/M/Inception (2010)/Inception (2010).mkv",
            warnings: new[] { "元数据(nfo)写入失败：拒绝访问" });

        ProcessFileOutcome r = await Run();

        r.Outcome.Should().Be(ProcessOutcome.Completed);
        MediaItem m = ReadOne();
        m.Status.Should().Be(MediaItemStatus.Completed, "视频已落地即完成，nfo 失败不应判 Failed");
        m.ErrorMessage.Should().BeNull("非失败终态，不写 ErrorMessage");

        using PmmDbContext db = _dbFactory.CreateDbContext();
        ProcessStep archivingStep = db.ProcessSteps.AsNoTracking().Single(s => s.Stage == MediaItemStatus.Archiving);
        archivingStep.Detail.Should().Contain("metadataPending").And.Contain("nfo");
    }

    // ---------- 12. 任意异常 → Failed ----------
    [Fact]
    public async Task Exception_In_Pipeline_MarksFailed()
    {
        ConfigureRule(confidence: 0.9, hasSpecialChars: false);
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new TmdbClientException("503 Service Unavailable", 503));

        ProcessFileOutcome r = await Run();
        r.Outcome.Should().Be(ProcessOutcome.Failed);
        MediaItem m = ReadOne();
        m.Status.Should().Be(MediaItemStatus.Failed);
        m.ErrorMessage.Should().Contain("503");
    }

    // ---------- 13. Cancellation 透传 ----------
    [Fact]
    public async Task Cancellation_Propagates_When_Token_Cancelled_During_Write()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();
        _writeDetector.WaitUntilCompleteAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                ((CancellationToken)call[3]).ThrowIfCancellationRequested();
                return Task.FromResult(true);
            });

        Func<Task> act = async () => await Run(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---------- 14. MediaItem 行确实落库 ----------
    [Fact]
    public async Task Successful_Run_Creates_Exactly_One_MediaItem_With_All_Fields()
    {
        ConfigureRule(confidence: 0.85, hasSpecialChars: false, year: 2010, title: "Inception", season: null, episode: null);
        ConfigureTmdb(candidates: 1);
        ConfigureClassify(ClassifyDecision.Matched, 99);
        ConfigureArchive(ArchiveOutcome.Completed, "/M/Inception (2010)/Inception (2010).mkv");

        await Run();
        using PmmDbContext db = _dbFactory.CreateDbContext();
        MediaItem m = db.MediaItems.Single();
        m.SourcePath.Should().Be(_tempFile);
        m.FileName.Should().Be(Path.GetFileName(_tempFile));
        m.Status.Should().Be(MediaItemStatus.Completed);
        m.CategoryId.Should().Be(99);
        m.ParsedInfo.Should().Contain("Inception").And.Contain("2010");
    }

    // ---------- 15. Process_Step 时间线写入（M2 真实化） ----------
    [Fact]
    public async Task Successful_Run_Writes_Timeline_Steps_Per_Stage()
    {
        ConfigureRule(confidence: 0.85, hasSpecialChars: false, year: 2010, title: "Inception", season: null, episode: null);
        ConfigureTmdb(candidates: 1);
        ConfigureClassify(ClassifyDecision.Matched, 99);
        ConfigureArchive(ArchiveOutcome.Completed, "/M/Inception (2010)/Inception (2010).mkv");

        await Run();
        using PmmDbContext db = _dbFactory.CreateDbContext();
        List<ProcessStep> steps = db.ProcessSteps.AsNoTracking().OrderBy(s => s.Id).ToList();

        // 期望路径：Detected → Queued → Parsing → TmdbMatching → Classifying → Archiving → Completed
        // 每个非终态记 Exit step；Completed 记终态 step。共 7 条。
        steps.Should().HaveCount(7);
        steps.Select(s => s.Stage).Should().ContainInOrder(
            MediaItemStatus.Detected, MediaItemStatus.Queued, MediaItemStatus.Parsing,
            MediaItemStatus.TmdbMatching, MediaItemStatus.Classifying, MediaItemStatus.Archiving,
            MediaItemStatus.Completed);
        steps.All(s => s.DurMs >= 0).Should().BeTrue();
        // Parsing 步骤 detail 含 cleaned 字段（来自规则引擎结果）
        steps.Single(s => s.Stage == MediaItemStatus.Parsing).Detail.Should().Contain("cleaned");
        // Archiving 步骤 detail 含 target 路径
        steps.Single(s => s.Stage == MediaItemStatus.Archiving).Detail.Should().Contain("Inception");
    }

    [Fact]
    public async Task Failed_Run_Writes_Failed_Terminal_Step_With_Reason()
    {
        ConfigureRule(confidence: 0.85, hasSpecialChars: false, year: 2010, title: "Inception", season: null, episode: null);
        ConfigureTmdb(candidates: 1);
        ConfigureClassify(ClassifyDecision.Matched, 99);
        _archive.ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>())
            .Throws(new IOException("磁盘满"));

        await Run();
        using PmmDbContext db = _dbFactory.CreateDbContext();
        ProcessStep failedStep = db.ProcessSteps.AsNoTracking().Single(s => s.Stage == MediaItemStatus.Failed);
        failedStep.Detail.Should().Contain("磁盘满");
    }

    [Fact]
    public async Task Terminal_MediaItem_NoNewSteps_Written_On_Idempotent_Skip()
    {
        long id = SeedMediaItemAtStatus(MediaItemStatus.Completed);

        await Run();
        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.ProcessSteps.AsNoTracking().Should().BeEmpty(
            "已是终态的 MediaItem 直接返回 Skipped，不应再追加任何 Step");
    }

    // ---------- 16. 子目录内第二个文件复用缓存、跳过 AI（专属剧集文件夹才允许复用） ----------
    [Fact]
    public async Task SameSubfolder_SecondFile_Reuses_Cache_And_Skips_Ai()
    {
        // 复用仅在「监控根的子目录」（专属剧集文件夹）内生效：
        // 第一集走 AI 锁定 tv series 并归档（回写文件夹缓存）；第二集本应再走 AI，
        // 但命中缓存（同子目录 + TV + 剧名相似）→ 跳过 AI 与二次 TMDB，季 / 集仍来自规则。
        string watchRoot = Path.Combine(Path.GetTempPath(), $"pmm-watch-{Guid.NewGuid():N}");
        long watchId = SeedWatchFolder(watchRoot);
        string seriesDir = Path.Combine(watchRoot, "Show A");           // 子目录 → 复用合格
        string fileA = Path.Combine(seriesDir, "ShowA.S01E01.mkv");
        string fileB = Path.Combine(seriesDir, "ShowA.S01E02.mkv");

        // 两次规则解析都低置信 → 走 AI 路径；季同为 1，集分别 1 / 2
        _ruleEngine.ParseAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>())
            .Returns(
                new RuleParseResult("Show A", 2020, "tv", 1, 1, null, 0.3, false, 1),
                new RuleParseResult("Show A", 2020, "tv", 1, 2, null, 0.3, false, 1));
        // AI 返回 tv（季集留空 → 下游回退到规则的季集）
        _aiOrchestrator.ExecuteAsync(Arg.Any<AiParseRequest>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new AiCallOutcome(true, new AiParseResult("Show A", 2020, "tv", null, null, null, 0.85), 1L, 1, null));
        // 文件 A 的二次 TMDB 返回唯一 tv 候选（标题/年份与解析匹配，过四维择优门槛）；文件 B 复用缓存不应再查
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult([NewCandidate(500, "tv", "Show A", 2020)], null));
        ConfigureClassify(ClassifyDecision.Matched, 3);
        ConfigureArchive(ArchiveOutcome.Completed, "/Tv/ShowA/S01.mkv");

        ProcessFileOutcome rA = await NewSut().ProcessAsync(
            new PendingFileItem(fileA, WatchFolderId: watchId, PendingFileSource.Watcher), CancellationToken.None);
        ProcessFileOutcome rB = await NewSut().ProcessAsync(
            new PendingFileItem(fileB, WatchFolderId: watchId, PendingFileSource.Watcher), CancellationToken.None);

        rA.Outcome.Should().Be(ProcessOutcome.Completed);
        rB.Outcome.Should().Be(ProcessOutcome.Completed);

        // AI 只被文件 A 调用一次；文件 B 命中缓存跳过
        await _aiOrchestrator.Received(1).ExecuteAsync(Arg.Any<AiParseRequest>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        // TMDB 搜索只发生在文件 A（AI 后二次查询）；文件 B 跳过二次 TMDB
        await _tmdb.Received(1).SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>());

        using PmmDbContext db = _dbFactory.CreateDbContext();
        MediaItem mB = db.MediaItems.AsNoTracking().Single(m => m.SourcePath == fileB);
        mB.Status.Should().Be(MediaItemStatus.Completed);
        mB.TmdbId.Should().Be(500);
        mB.ParseSource.Should().Be(ParseSource.Hybrid);   // 复用路径标 Hybrid（规则季集 + 缓存 series）
        mB.ParsedInfo.Should().Contain("\"episode\":2");  // 集号来自本文件规则解析（第二集）
        mB.ParsedInfo.Should().Contain("Show A");          // 剧名来自缓存
    }

    // ---------- 16b. 监控根目录平铺堆放 → 绝不复用（复现「掠食城市」事故） ----------
    [Fact]
    public async Task FilesDirectlyInWatchRoot_DoNotShareFolderCache()
    {
        // 监控根目录本身就是下载堆放地，第一部电影解析成功后绝不能锁定整个监控根 →
        // 后续无关文件必须各自独立解析，不得复用上一部的 TMDB（这是 4 部无关片全被套成 428078 的根因）。
        string watchRoot = Path.Combine(Path.GetTempPath(), $"pmm-root-{Guid.NewGuid():N}");
        long watchId = SeedWatchFolder(watchRoot);
        string fileA = Path.Combine(watchRoot, "掠食城市.2018.1080p.mp4");      // 直接在监控根下
        string fileB = Path.Combine(watchRoot, "银河护卫队2.2017.1080p.mkv");    // 同上、无关电影

        // A：规则高置信 → 直查 TMDB 命中 movie 428078（旧逻辑会把监控根锁成 428078）
        // B：规则低置信 → 走 AI；若错误复用会被套成 428078，正确行为是用自己的 AI/TMDB 结果
        _ruleEngine.ParseAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>())
            .Returns(
                new RuleParseResult("掠食城市", 2018, "movie", null, null, null, 0.85, false, 1),
                new RuleParseResult("银河护卫队2", 2017, "movie", null, null, null, 0.30, false, 1));
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new TmdbSearchResult([NewCandidate(428078, "movie", "掠食城市", 2018)], null),    // A 首查
                new TmdbSearchResult([NewCandidate(283587, "movie", "银河护卫队2", 2017)], null));    // B 的 AI 后二次查
        _aiOrchestrator.ExecuteAsync(Arg.Any<AiParseRequest>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new AiCallOutcome(true, new AiParseResult("银河护卫队2", 2017, "movie", null, null, null, 0.8), 1L, 1, null));
        ConfigureClassify(ClassifyDecision.Matched, 1);
        ConfigureArchive(ArchiveOutcome.Completed, "/M/x.mkv");

        await NewSut().ProcessAsync(new PendingFileItem(fileA, watchId, PendingFileSource.Watcher), CancellationToken.None);
        await NewSut().ProcessAsync(new PendingFileItem(fileB, watchId, PendingFileSource.Watcher), CancellationToken.None);

        // B 必须自己调用 AI（没被监控根缓存短路）
        await _aiOrchestrator.Received(1).ExecuteAsync(Arg.Any<AiParseRequest>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        using PmmDbContext db = _dbFactory.CreateDbContext();
        MediaItem mB = db.MediaItems.AsNoTracking().Single(m => m.SourcePath == fileB);
        mB.TmdbId.Should().Be(283587);              // B 用自己的结果
        mB.TmdbId.Should().NotBe(428078);           // 绝不被套成 A 的 TMDB
        mB.ParseSource.Should().Be(ParseSource.Ai); // 非 Hybrid（未复用）
    }

    // ---------- 16c. 子目录里先放电影 → 不锁定目录（电影无分集语义） ----------
    [Fact]
    public async Task MovieInSubfolder_DoesNotLockFolderForReuse()
    {
        // 即便在子目录，电影也不应锁定目录：先放的一部电影不得被同目录后续无关文件复用。
        string watchRoot = Path.Combine(Path.GetTempPath(), $"pmm-w-{Guid.NewGuid():N}");
        long watchId = SeedWatchFolder(watchRoot);
        string sub = Path.Combine(watchRoot, "杂项");
        string fileA = Path.Combine(sub, "掠食城市.2018.mp4");
        string fileB = Path.Combine(sub, "银河护卫队2.2017.mkv");

        _ruleEngine.ParseAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>())
            .Returns(
                new RuleParseResult("掠食城市", 2018, "movie", null, null, null, 0.85, false, 1),
                new RuleParseResult("银河护卫队2", 2017, "movie", null, null, null, 0.30, false, 1));
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new TmdbSearchResult([NewCandidate(428078, "movie", "掠食城市", 2018)], null),
                new TmdbSearchResult([NewCandidate(283587, "movie", "银河护卫队2", 2017)], null));
        _aiOrchestrator.ExecuteAsync(Arg.Any<AiParseRequest>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new AiCallOutcome(true, new AiParseResult("银河护卫队2", 2017, "movie", null, null, null, 0.8), 1L, 1, null));
        ConfigureClassify(ClassifyDecision.Matched, 1);
        ConfigureArchive(ArchiveOutcome.Completed, "/M/x.mkv");

        await NewSut().ProcessAsync(new PendingFileItem(fileA, watchId, PendingFileSource.Watcher), CancellationToken.None);
        await NewSut().ProcessAsync(new PendingFileItem(fileB, watchId, PendingFileSource.Watcher), CancellationToken.None);

        await _aiOrchestrator.Received(1).ExecuteAsync(Arg.Any<AiParseRequest>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        using PmmDbContext db = _dbFactory.CreateDbContext();
        MediaItem mB = db.MediaItems.AsNoTracking().Single(m => m.SourcePath == fileB);
        mB.TmdbId.Should().Be(283587);
        mB.ParseSource.Should().Be(ParseSource.Ai);
    }

    // ---------- 16d. 子目录混放两部不同剧 → 标题不相似不复用 ----------
    [Fact]
    public async Task DifferentSeriesInSameSubfolder_NotReused_WhenTitleDissimilar()
    {
        // 子目录里混放两部不同的剧：第一部 TV 锁定目录后，第二部规则标题与缓存剧名差异大，
        // 相似度守门拒绝复用 → 第二部走自己的 AI/TMDB，不被套成第一部。
        string watchRoot = Path.Combine(Path.GetTempPath(), $"pmm-w-{Guid.NewGuid():N}");
        long watchId = SeedWatchFolder(watchRoot);
        string sub = Path.Combine(watchRoot, "混合剧集");
        string fileA = Path.Combine(sub, "Breaking.Bad.S01E01.mkv");
        string fileB = Path.Combine(sub, "Better.Call.Saul.S01E01.mkv");

        _ruleEngine.ParseAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>())
            .Returns(
                new RuleParseResult("Breaking Bad", 2008, "tv", 1, 1, null, 0.30, false, 1),
                new RuleParseResult("Better Call Saul", 2015, "tv", 1, 1, null, 0.30, false, 1));
        _aiOrchestrator.ExecuteAsync(Arg.Any<AiParseRequest>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(
                new AiCallOutcome(true, new AiParseResult("Breaking Bad", 2008, "tv", null, null, null, 0.85), 1L, 1, null),
                new AiCallOutcome(true, new AiParseResult("Better Call Saul", 2015, "tv", null, null, null, 0.85), 1L, 1, null));
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new TmdbSearchResult([NewCandidate(1396, "tv", "Breaking Bad", 2008)], null),
                new TmdbSearchResult([NewCandidate(60059, "tv", "Better Call Saul", 2015)], null));
        ConfigureClassify(ClassifyDecision.Matched, 1);
        ConfigureArchive(ArchiveOutcome.Completed, "/Tv/x.mkv");

        await NewSut().ProcessAsync(new PendingFileItem(fileA, watchId, PendingFileSource.Watcher), CancellationToken.None);
        await NewSut().ProcessAsync(new PendingFileItem(fileB, watchId, PendingFileSource.Watcher), CancellationToken.None);

        // 两部剧都各自调用 AI（B 未复用 A）
        await _aiOrchestrator.Received(2).ExecuteAsync(Arg.Any<AiParseRequest>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        using PmmDbContext db = _dbFactory.CreateDbContext();
        MediaItem mB = db.MediaItems.AsNoTracking().Single(m => m.SourcePath == fileB);
        mB.TmdbId.Should().Be(60059);                 // Better Call Saul，不是 Breaking Bad 的 1396
        mB.ParseSource.Should().Be(ParseSource.Ai);   // 未复用 → Ai 而非 Hybrid
    }

    // ---------- 16e. 持久化剧集映射：进程重启后（内存缓存清空）规则直查路径仍复用 DB 兄弟集、跳过 TMDB 搜索 ----------
    [Fact]
    public async Task SameSubfolder_SecondFile_RuleHighConfidence_Reuses_PersistedSibling_SkipsTmdbSearch()
    {
        // 第一集高置信走规则直查 → TMDB 命中唯一 tv 候选 → 归档落库（带 tmdbId）。
        // 用全新 folderCache 实例处理第二集（模拟进程重启、内存缓存清空）：第二集同样高置信规则路径，
        // 应从「同子目录已归档兄弟集」还原 series 身份、跳过 TMDB 搜索；季 / 集仍取本文件规则。
        string watchRoot = Path.Combine(Path.GetTempPath(), $"pmm-persist-{Guid.NewGuid():N}");
        long watchId = SeedWatchFolder(watchRoot);
        string seriesDir = Path.Combine(watchRoot, "Persist Show");
        string fileA = Path.Combine(seriesDir, "PersistShow.S01E01.mkv");
        string fileB = Path.Combine(seriesDir, "PersistShow.S01E02.mkv");

        // 两集都高置信规则路径（season=1，episode 分别 1 / 2）
        _ruleEngine.ParseAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>())
            .Returns(
                new RuleParseResult("Persist Show", 2021, "tv", 1, 1, null, 0.9, false, 1),
                new RuleParseResult("Persist Show", 2021, "tv", 1, 2, null, 0.9, false, 1));
        // 仅文件 A 的首次搜索返回唯一 tv 候选（标题/年份匹配过得分门槛）；文件 B 复用 DB 兄弟集，不应再搜索
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult([NewCandidate(700, "tv", "Persist Show", 2021)], null));
        ConfigureClassify(ClassifyDecision.Matched, 3);
        ConfigureArchive(ArchiveOutcome.Completed, "/Tv/PersistShow/S01.mkv");

        // 文件 A 用缓存实例 1；文件 B 用全新空缓存实例 2（模拟重启，L1 清空，强制走 DB 还原）
        await NewSutWithCache(new InMemoryFolderSeriesCache())
            .ProcessAsync(new PendingFileItem(fileA, watchId, PendingFileSource.Watcher), CancellationToken.None);
        ProcessFileOutcome rB = await NewSutWithCache(new InMemoryFolderSeriesCache())
            .ProcessAsync(new PendingFileItem(fileB, watchId, PendingFileSource.Watcher), CancellationToken.None);

        rB.Outcome.Should().Be(ProcessOutcome.Completed);
        // TMDB 搜索只发生在文件 A；文件 B 从 DB 兄弟集复用 → 不再搜索（本特性核心断言）
        await _tmdb.Received(1).SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>());
        // 两集都是高置信规则路径，AI 全程不参与
        await _aiOrchestrator.DidNotReceive().ExecuteAsync(Arg.Any<AiParseRequest>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());

        using PmmDbContext db = _dbFactory.CreateDbContext();
        MediaItem mB = db.MediaItems.AsNoTracking().Single(m => m.SourcePath == fileB);
        mB.Status.Should().Be(MediaItemStatus.Completed);
        mB.TmdbId.Should().Be(700);                       // 复用 A 的 tmdbId
        mB.ParseSource.Should().Be(ParseSource.Hybrid);   // 复用路径标 Hybrid（规则季集 + 持久映射 series）
        mB.ParsedInfo.Should().Contain("\"episode\":2");  // 集号来自本文件规则解析
        mB.ParsedInfo.Should().Contain("Persist Show");   // 剧名来自复用映射

        // 时间线 TmdbMatching 步骤标注数据来源：文件 A 远端拉取(remote)，文件 B 命中本地剧集映射(reuse)
        List<ProcessStep> tmdbSteps = db.ProcessSteps.AsNoTracking()
            .Where(s => s.Stage == MediaItemStatus.TmdbMatching).OrderBy(s => s.Id).ToList();
        tmdbSteps.Should().HaveCount(2);
        tmdbSteps[0].Detail.Should().Contain("\"source\":\"remote\"");  // 文件 A：真打了 TMDB
        tmdbSteps[1].Detail.Should().Contain("\"source\":\"reuse\"");   // 文件 B：复用持久映射，未发搜索
    }

    // ---------- 16f. 持久化剧集映射：进程重启后 AI 兜底路径同样复用 DB 兄弟集、跳过 AI 与二次 TMDB ----------
    [Fact]
    public async Task SameSubfolder_SecondFile_AiPath_Reuses_PersistedSibling_AcrossRestart_SkipsAi()
    {
        // 第一集低置信走 AI 路径锁定 series 并归档；重启（新缓存实例）后第二集本应再走 AI，
        // 但从 DB 兄弟集还原 series 身份 → 跳过 AI 与二次 TMDB（覆盖中日韩剧集每集走 AI 的重启复用场景）。
        string watchRoot = Path.Combine(Path.GetTempPath(), $"pmm-persist-ai-{Guid.NewGuid():N}");
        long watchId = SeedWatchFolder(watchRoot);
        string seriesDir = Path.Combine(watchRoot, "Anime Show");
        string fileA = Path.Combine(seriesDir, "AnimeShow.S01E01.mkv");
        string fileB = Path.Combine(seriesDir, "AnimeShow.S01E02.mkv");

        // 两集都低置信 → 走 AI 路径；季同为 1，集分别 1 / 2
        _ruleEngine.ParseAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>())
            .Returns(
                new RuleParseResult("Anime Show", 2022, "tv", 1, 1, null, 0.3, false, 1),
                new RuleParseResult("Anime Show", 2022, "tv", 1, 2, null, 0.3, false, 1));
        _aiOrchestrator.ExecuteAsync(Arg.Any<AiParseRequest>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new AiCallOutcome(true, new AiParseResult("Anime Show", 2022, "tv", null, null, null, 0.85), 1L, 1, null));
        // 仅文件 A 的 AI 后二次搜索返回唯一 tv 候选（标题/年份匹配过得分门槛）；文件 B 复用 DB 兄弟集，不应再搜索
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult([NewCandidate(800, "tv", "Anime Show", 2022)], null));
        ConfigureClassify(ClassifyDecision.Matched, 3);
        ConfigureArchive(ArchiveOutcome.Completed, "/Tv/AnimeShow/S01.mkv");

        await NewSutWithCache(new InMemoryFolderSeriesCache())
            .ProcessAsync(new PendingFileItem(fileA, watchId, PendingFileSource.Watcher), CancellationToken.None);
        ProcessFileOutcome rB = await NewSutWithCache(new InMemoryFolderSeriesCache())
            .ProcessAsync(new PendingFileItem(fileB, watchId, PendingFileSource.Watcher), CancellationToken.None);

        rB.Outcome.Should().Be(ProcessOutcome.Completed);
        // AI 只被文件 A 调用；文件 B 跨重启复用 DB 兄弟集 → 跳过 AI
        await _aiOrchestrator.Received(1).ExecuteAsync(Arg.Any<AiParseRequest>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        // TMDB 搜索只发生在文件 A（AI 后二次查询）；文件 B 跳过二次 TMDB
        await _tmdb.Received(1).SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>());

        using PmmDbContext db = _dbFactory.CreateDbContext();
        MediaItem mB = db.MediaItems.AsNoTracking().Single(m => m.SourcePath == fileB);
        mB.TmdbId.Should().Be(800);
        mB.ParseSource.Should().Be(ParseSource.Hybrid);
        mB.ParsedInfo.Should().Contain("\"episode\":2");
    }

    // ---------- 16g. TMDB 步骤来源标注：搜索命中本地缓存(DB) → source=cache ----------
    [Fact]
    public async Task TmdbStep_Records_Source_Cache_When_SearchResult_FromCache()
    {
        // ITmdbSearchService 返回 FromCache=true（模拟命中 Tmdb_SearchCache）→ 时间线步骤来源应标 cache
        ConfigureRule(confidence: 0.9, hasSpecialChars: false, year: 2010, title: "Inception", season: null, episode: null);
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult([NewCandidate(100, "movie")], null, FromCache: true));
        ConfigureClassify(ClassifyDecision.Matched, 7);
        ConfigureArchive(ArchiveOutcome.Completed, "/M/Inception.mkv");

        await Run();

        using PmmDbContext db = _dbFactory.CreateDbContext();
        ProcessStep tmdbStep = db.ProcessSteps.AsNoTracking().Single(s => s.Stage == MediaItemStatus.TmdbMatching);
        tmdbStep.Detail.Should().Contain("\"source\":\"cache\"");
    }

    // ---------- 内容去重（FileHash）----------

    [Fact]
    public async Task Dedup_Hits_Completed_Duplicate_Skips_Without_Pipeline()
    {
        // 库内已有一条 Completed 记录、FileHash=deadbeef；本次文件算出相同 hash → 直接 Skipped，不进解析/归档管线
        SeedCompletedWithHash("/already/archived.mkv", "deadbeef");
        _fileHasher.TryComputeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("deadbeef");

        ProcessFileOutcome r = await Run();

        r.Outcome.Should().Be(ProcessOutcome.Skipped);
        await _ruleEngine.DidNotReceive().ParseAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>());
        await _archive.DidNotReceive().ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>());
        // 新建的 _tempFile 记录：Skipped + 写入了 hash
        MediaItem dup = ReadBySource(_tempFile);
        dup.Status.Should().Be(MediaItemStatus.Skipped);
        dup.FileHash.Should().Be("deadbeef");
    }

    [Fact]
    public async Task Dedup_NoMatch_Stores_Hash_And_Proceeds_To_Completed()
    {
        // 算出 hash 但库内无同 hash 的 Completed → 照常走完管线，并把 hash 落库
        _fileHasher.TryComputeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("freshhash");
        ConfigureRule(confidence: 0.9, hasSpecialChars: false);
        ConfigureTmdb(candidates: 1);
        ConfigureClassify(ClassifyDecision.Matched, categoryId: 7);
        ConfigureArchive(ArchiveOutcome.Completed, targetPath: "/M/x.mkv");

        ProcessFileOutcome r = await Run();

        r.Outcome.Should().Be(ProcessOutcome.Completed);
        MediaItem m = ReadOne();
        m.Status.Should().Be(MediaItemStatus.Completed);
        m.FileHash.Should().Be("freshhash");
        await _archive.Received(1).ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Dedup_Ignores_NonCompleted_SameHash()
    {
        // 同 hash 但已有记录状态为 Failed（非 Completed）→ 不算重复，照常处理（去重只比对已成功归档）
        using (PmmDbContext seedDb = _dbFactory.CreateDbContext())
        {
            seedDb.MediaItems.Add(MediaItem.CreateFixture("/f/failed.mkv", "failed.mkv", 1024,
                status: MediaItemStatus.Failed, fileHash: "samehash"));
            seedDb.SaveChanges();
        }
        _fileHasher.TryComputeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("samehash");
        ConfigureRule(confidence: 0.9, hasSpecialChars: false);
        ConfigureTmdb(candidates: 1);
        ConfigureClassify(ClassifyDecision.Matched, categoryId: 7);
        ConfigureArchive(ArchiveOutcome.Completed, targetPath: "/M/x.mkv");

        ProcessFileOutcome r = await Run();

        r.Outcome.Should().Be(ProcessOutcome.Completed);
        await _archive.Received(1).ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Dedup_DuplicateTarget_Missing_ReProcesses_NotSkipped()
    {
        // 库内有同 hash 的 Completed 记录，但其归档副本已被外部删除（FileExists=false）→
        // 绝不当重复跳过（否则旧副本没了、新文件也不归档 = 内容彻底丢失），应照常走完管线重新归档
        const string missingTarget = "/archived/gone.mkv";
        SeedCompletedWithHash("/old/source.mkv", "duphash", targetPath: missingTarget);
        _fileProbe.FileExists(missingTarget).Returns(false);   // 归档副本已不在
        _fileHasher.TryComputeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("duphash");
        ConfigureRule(confidence: 0.9, hasSpecialChars: false);
        ConfigureTmdb(candidates: 1);
        ConfigureClassify(ClassifyDecision.Matched, categoryId: 7);
        ConfigureArchive(ArchiveOutcome.Completed, targetPath: "/M/re-archived.mkv");

        ProcessFileOutcome r = await Run();

        r.Outcome.Should().Be(ProcessOutcome.Completed);   // 重新归档，而非 Skipped
        await _archive.Received(1).ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>());
        MediaItem m = ReadBySource(_tempFile);
        m.Status.Should().Be(MediaItemStatus.Completed);
        m.FileHash.Should().Be("duphash");
    }

    // ---------- Webhook 事件触发（media.failed / media.skipped / review.created）----------
    // 仅验证对应终态调用了 IWebhookEmitter.EmitAsync(正确事件名)；
    // 「总开关 / 订阅匹配 / 真实投递」由 WebhookEmitterTests 覆盖（此处 _webhook 是替身）。

    [Fact]
    public async Task Failed_Emits_MediaFailed_Webhook()
    {
        ConfigureRule(confidence: 0.9, hasSpecialChars: false);
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new TmdbClientException("503 Service Unavailable", 503));

        await Run();

        await _webhook.Received(1).EmitAsync(WebhookEvents.MediaFailed, Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConflictSkipped_Emits_MediaSkipped_Webhook()
    {
        ConfigureRule(confidence: 0.9, hasSpecialChars: false);
        ConfigureTmdb(candidates: 1);
        ConfigureClassify(ClassifyDecision.Matched, 1);
        ConfigureArchive(ArchiveOutcome.ConflictSkipped, "/CONFLICT.mkv");

        await Run();

        await _webhook.Received(1).EmitAsync(WebhookEvents.MediaSkipped, Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DedupHit_Emits_MediaSkipped_Webhook()
    {
        SeedCompletedWithHash("/already/archived.mkv", "deadbeef");
        _fileHasher.TryComputeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("deadbeef");

        await Run();

        await _webhook.Received(1).EmitAsync(WebhookEvents.MediaSkipped, Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AwaitingReview_Emits_ReviewCreated_Webhook()
    {
        // 剧集缺季号 + TMDB 多季 → ParseIncomplete → AwaitingReview
        _ruleEngine.ParseAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>())
            .Returns(new RuleParseResult("Multi Show", null, "tv", null, 5, null, 0.9, false, 1));
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult([NewCandidate(55555, "tv")], null));
        _tmdb.GetDetailsAsync(55555, "tv", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(55555, "tv", "Show", "Show", 2018, 4, null, ["US"], "en", null, null, "{}"));

        ProcessFileOutcome r = await Run();

        r.Outcome.Should().Be(ProcessOutcome.AwaitingReview);
        await _webhook.Received(1).EmitAsync(WebhookEvents.ReviewCreated, Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    // ---------- 17. TMDB 候选四维择优（修复盲取 Candidates[0] + Tmdb_Setting 权重死旋钮）----------

    [Fact]
    public async Task MultiCandidates_AdoptsBestScored_NotServerOrder()
    {
        ConfigureRule(confidence: 0.9, hasSpecialChars: false, year: 1999, title: "The Matrix");
        // 服务端默认序把错误候选放首位：旧实现盲取 id=7；四维择优应改选「标题 + 年份」全中的 id=8
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult([
                NewCandidate(7, "movie", "Totally Different", 1985),
                NewCandidate(8, "movie", "The Matrix", 1999),
            ], null));
        ConfigureClassify(ClassifyDecision.Matched, 1);
        ConfigureArchive(ArchiveOutcome.Completed, "/M/Matrix.mkv");

        ProcessFileOutcome r = await Run();

        r.Outcome.Should().Be(ProcessOutcome.Completed);
        ReadOne().TmdbId.Should().Be(8, "应采纳综合得分最高的候选，而非服务端首位");
    }

    [Fact]
    public async Task MultiCandidates_AllLowScore_Go_To_AwaitingReview()
    {
        // 标题全不相似 + 年份全偏差大 → 最高综合得分 < 多候选门槛 0.5 → 无法可信取舍，转人工
        ConfigureRule(confidence: 0.9, hasSpecialChars: false, year: 2020, title: "你好世界");
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult([
                NewCandidate(31, "movie", "Alpha Beta", 1980),
                NewCandidate(32, "movie", "Gamma Delta", 1979),
            ], null));

        ProcessFileOutcome r = await Run();

        r.Outcome.Should().Be(ProcessOutcome.AwaitingReview);
        MediaItem m = ReadOne();
        m.Status.Should().Be(MediaItemStatus.AwaitingReview);
        m.ReviewReason.Should().Be(ReviewReason.TmdbMultiCandidate);
        m.TmdbCandidatesJson.Should().NotBeNullOrEmpty("候选全集落库供审核页单选");
        await _archive.DidNotReceive().ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SingleCandidate_LowScore_Goes_To_AwaitingReview()
    {
        // 残缺 / 无关标题模糊命中唯一一条错误结果：单候选下限 0.35 拦截 → 转人工而非直接采纳
        ConfigureRule(confidence: 0.9, hasSpecialChars: false, year: 2020, title: "你好世界");
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult([NewCandidate(41, "movie", "Unrelated Movie", 2010)], null));

        ProcessFileOutcome r = await Run();

        r.Outcome.Should().Be(ProcessOutcome.AwaitingReview);
        MediaItem m = ReadOne();
        m.ReviewReason.Should().Be(ReviewReason.TmdbMultiCandidate);
        await _archive.DidNotReceive().ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScoreWeights_From_TmdbSetting_Affect_Pick()
    {
        // 把权重改成「只看年份」：标题全中但年份远的 51 落选、标题不沾边但年份全中的 52 当选 → 证明权重真从库读
        UpdateTmdbSetting(s =>
        {
            s.ScoreWeightTitle = 0;
            s.ScoreWeightYear = 1.0;
            s.ScoreWeightPopularity = 0;
            s.ScoreWeightLanguage = 0;
        });
        ConfigureRule(confidence: 0.9, hasSpecialChars: false, year: 2020, title: "Exact Title");
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult([
                NewCandidate(51, "movie", "Exact Title", 1990),
                NewCandidate(52, "movie", "Zzz Unrelated", 2020),
            ], null));
        ConfigureClassify(ClassifyDecision.Matched, 1);
        ConfigureArchive(ArchiveOutcome.Completed, "/M/x.mkv");

        ProcessFileOutcome r = await Run();

        r.Outcome.Should().Be(ProcessOutcome.Completed);
        ReadOne().TmdbId.Should().Be(52, "权重改为只看年份后应选年份全中的候选");
    }

    // ---------- 18. CandidateThreshold 从 Tmdb_Setting 运行时读取（修复死旋钮 N=3 硬编码）----------

    [Fact]
    public async Task CandidateThreshold_From_TmdbSetting_Consumed()
    {
        // 阈值 N 调到 1：首查 2 个候选 > N → 触发 AI 兜底（硬编码 N=3 时 2 个候选会被直接采用、AI 不会被调）。
        // 两个候选设计成同名同年（四维打分并列，榜首不显著）——免 AI 裁决弃权，验证的是「阈值被消费」本身
        UpdateTmdbSetting(s => s.CandidateThreshold = 1);
        ConfigureRule(confidence: 0.9, hasSpecialChars: false, year: 2020, title: "Dual Hit");
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new TmdbSearchResult([
                    NewCandidate(61, "movie", "Dual Hit", 2020),
                    NewCandidate(62, "movie", "Dual Hit", 2020),
                ], null),
                new TmdbSearchResult([NewCandidate(63, "movie", "AI-Title", 2011)], null));
        ConfigureAi(success: true);
        ConfigureClassify(ClassifyDecision.Matched, 1);
        ConfigureArchive(ArchiveOutcome.Completed, "/M/dual.mkv");

        ProcessFileOutcome r = await Run();

        r.Outcome.Should().Be(ProcessOutcome.Completed);
        await _aiOrchestrator.Received(1).ExecuteAsync(Arg.Any<AiParseRequest>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        ReadOne().TmdbId.Should().Be(63);
    }

    // ---------- 18b. 候选过多但四维打分榜首显著 → 免 AI 直接采纳 ----------
    [Fact]
    public async Task TooManyCandidates_DominantScore_Adopts_Without_Ai()
    {
        // 4 个候选 > N=3：榜首「Dual Hit/2020」与规则标题、年份完全匹配（高分），
        // 其余同年但标题完全不相关（低分）→ 榜首显著领先 → 免 AI 采纳，ParseSource 保持 Rule
        ConfigureRule(confidence: 0.9, hasSpecialChars: false, year: 2020, title: "Dual Hit");
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult([
                NewCandidate(61, "movie", "Dual Hit", 2020),
                NewCandidate(62, "movie", "Zebra Quantum", 2020),
                NewCandidate(64, "movie", "Xylophone Nine", 2020),
                NewCandidate(65, "movie", "Wombat Curry", 2020),
            ], null));
        ConfigureClassify(ClassifyDecision.Matched, 1);
        ConfigureArchive(ArchiveOutcome.Completed, "/M/dual.mkv");

        ProcessFileOutcome r = await Run();

        r.Outcome.Should().Be(ProcessOutcome.Completed);
        MediaItem m = ReadOne();
        m.TmdbId.Should().Be(61);
        m.ParseSource.Should().Be(ParseSource.Rule);
        m.AiInvolved.Should().BeFalse();
        await _aiOrchestrator.DidNotReceive().ExecuteAsync(Arg.Any<AiParseRequest>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    // ---------- 18c. 候选过多且打分含糊 → 备选标题交叉投票唯一赢家 → 免 AI 采纳 ----------
    [Fact]
    public async Task TooManyCandidates_CrossVote_UniqueWinner_Adopts_Without_Ai()
    {
        // 首查（规则标题 Alpha）4 个候选全不相似（打分并列低分，无显著榜首）；
        // 备选「Beta」重搜同样 >N 不可直接采纳，但结果与首查候选交集命中 72（唯一得票），
        // 且 72 标题与备选词完全一致（得分过多候选门槛 0.5）→ 多次 TMDB 查询对比消歧，免 AI 采纳
        ConfigureRule(confidence: 0.9, hasSpecialChars: false, year: 2020, title: "Alpha", alternativeTitles: ["Beta"]);
        _tmdb.SearchAsync(Arg.Is<TmdbSearchRequest>(r => r.Query == "Alpha"), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult([
                NewCandidate(71, "movie", "Gamma Delta", 2020),
                NewCandidate(72, "movie", "Beta", 2020),
                NewCandidate(73, "movie", "Gamma Delta", 2020),
                NewCandidate(74, "movie", "Gamma Delta", 2020),
            ], null));
        _tmdb.SearchAsync(Arg.Is<TmdbSearchRequest>(r => r.Query == "Beta"), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult([
                NewCandidate(72, "movie", "Beta", 2020),      // 与首查交集 → 得票
                NewCandidate(81, "movie", "Beta Redux", 2019),
                NewCandidate(82, "movie", "Beta Origins", 2018),
                NewCandidate(83, "movie", "Beta Forever", 2017),
            ], null));
        ConfigureClassify(ClassifyDecision.Matched, 1);
        ConfigureArchive(ArchiveOutcome.Completed, "/M/beta.mkv");

        ProcessFileOutcome r = await Run();

        r.Outcome.Should().Be(ProcessOutcome.Completed);
        MediaItem m = ReadOne();
        m.TmdbId.Should().Be(72);
        m.ParseSource.Should().Be(ParseSource.Rule);   // 全程未动用 AI
        m.AiInvolved.Should().BeFalse();
        await _aiOrchestrator.DidNotReceive().ExecuteAsync(Arg.Any<AiParseRequest>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    // ---------- 18d. 交叉投票并列最高票 → 歧义弃权，仍走 AI ----------
    [Fact]
    public async Task TooManyCandidates_CrossVote_TiedWinners_Falls_To_Ai()
    {
        // 两个备选各投中不同首查候选（各 1 票并列）→ 弃权交 AI 裁决
        ConfigureRule(confidence: 0.9, hasSpecialChars: false, year: 2020, title: "Alpha",
            alternativeTitles: ["Beta", "Ceta"]);
        _tmdb.SearchAsync(Arg.Is<TmdbSearchRequest>(r => r.Query == "Alpha"), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult([
                NewCandidate(71, "movie", "Gamma Delta", 2020),
                NewCandidate(72, "movie", "Beta", 2020),
                NewCandidate(73, "movie", "Ceta", 2020),
                NewCandidate(74, "movie", "Gamma Delta", 2020),
            ], null));
        _tmdb.SearchAsync(Arg.Is<TmdbSearchRequest>(r => r.Query == "Beta"), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult(
                [NewCandidate(72, "movie", "Beta", 2020), NewCandidate(81, "movie", "B1", 2019), NewCandidate(82, "movie", "B2", 2018), NewCandidate(83, "movie", "B3", 2017)], null));
        _tmdb.SearchAsync(Arg.Is<TmdbSearchRequest>(r => r.Query == "Ceta"), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult(
                [NewCandidate(73, "movie", "Ceta", 2020), NewCandidate(84, "movie", "C1", 2019), NewCandidate(85, "movie", "C2", 2018), NewCandidate(86, "movie", "C3", 2017)], null));
        _tmdb.SearchAsync(Arg.Is<TmdbSearchRequest>(r => r.Query == "AI-Title"), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult([NewCandidate(99, "movie", "AI-Title", 2011)], null));
        ConfigureAi(success: true);
        ConfigureClassify(ClassifyDecision.Matched, 1);
        ConfigureArchive(ArchiveOutcome.Completed, "/M/tied.mkv");

        ProcessFileOutcome r = await Run();

        r.Outcome.Should().Be(ProcessOutcome.Completed);
        ReadOne().TmdbId.Should().Be(99);
        await _aiOrchestrator.Received(1).ExecuteAsync(Arg.Any<AiParseRequest>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    // ---------- 19. 复用分支不再丢季集：规则缺季 / 集时仍调 AI 补齐 ----------

    [Fact]
    public async Task FolderReuse_RuleMissingSeason_StillCallsAi_ToFill_ThenCompletes()
    {
        // 复用命中但规则没解析出季号（标准 Season NN 布局常见）：旧实现合成 Season:null/Episode:null 的
        // aiResult → 多季剧第二个文件起全进人工审核（复用反而更差）。现在复用仅跳过 TMDB 搜索：
        // AI 照常补季 / 集，补齐后直通归档（保留复用的 TMDB 绑定）。
        string watchRoot = Path.Combine(Path.GetTempPath(), $"pmm-reuse-fill-{Guid.NewGuid():N}");
        long watchId = SeedWatchFolder(watchRoot);
        string seriesDir = Path.Combine(watchRoot, "Show B");
        string file = Path.Combine(seriesDir, "ShowB.E07.mkv");
        _folderCache.Set(seriesDir, new FolderSeriesEntry(900, "tv", "Show B", 2021, 0.9));

        _ruleEngine.ParseAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>())
            .Returns(new RuleParseResult("Show B", 2021, "tv", null, 7, null, 0.3, false, 1));
        _aiOrchestrator.ExecuteAsync(Arg.Any<AiParseRequest>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new AiCallOutcome(true, new AiParseResult("Show B", 2021, "tv", 2, 7, null, 0.9), 1L, 1, null));
        ConfigureClassify(ClassifyDecision.Matched, 3);
        ConfigureArchive(ArchiveOutcome.Completed, "/Tv/ShowB/S02E07.mkv");

        ProcessFileOutcome r = await NewSut().ProcessAsync(
            new PendingFileItem(file, watchId, PendingFileSource.Watcher), CancellationToken.None);

        r.Outcome.Should().Be(ProcessOutcome.Completed);
        // 复用不再一概跳过 AI（规则缺季 → 仍调 AI 补齐）
        await _aiOrchestrator.Received(1).ExecuteAsync(Arg.Any<AiParseRequest>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        // 但仍跳过 TMDB 搜索（复用省的是搜索，不是解析）
        await _tmdb.DidNotReceive().SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>());
        MediaItem m = ReadBySource(file);
        m.Status.Should().Be(MediaItemStatus.Completed);
        m.TmdbId.Should().Be(900);
        m.ParseSource.Should().Be(ParseSource.Hybrid);
        m.ParsedInfo.Should().Contain("\"season\":2").And.Contain("\"episode\":7");
    }

    // ---------- 20. 复用守门：双语混排规则标题按归一化子串命中 ----------

    [Fact]
    public async Task FolderReuse_MixedLanguageRuleTitle_HitsBySubstring_SkipsAi()
    {
        // 双语混排规则标题 vs AI 清洗后的缓存剧名：Levenshtein 相似度仅 ≈0.24（旧守门恒拒绝 → 同剧每集烧 AI）；
        // 新守门「相似度 OR 归一化子串」兜底命中 → 跳过 AI 与二次 TMDB 直接复用
        string watchRoot = Path.Combine(Path.GetTempPath(), $"pmm-reuse-mix-{Guid.NewGuid():N}");
        long watchId = SeedWatchFolder(watchRoot);
        string seriesDir = Path.Combine(watchRoot, "Ousama Ranking");
        string file = Path.Combine(seriesDir, "国王排名 Ousama Ranking - 03.mkv");
        _folderCache.Set(seriesDir, new FolderSeriesEntry(1042, "tv", "国王排名", 2021, 0.9));

        _ruleEngine.ParseAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>())
            .Returns(new RuleParseResult("国王排名 Ousama Ranking", 2021, "tv", 1, 3, null, 0.5, true, 1));
        ConfigureClassify(ClassifyDecision.Matched, 3);
        ConfigureArchive(ArchiveOutcome.Completed, "/Tv/国王排名/S01E03.mkv");

        ProcessFileOutcome r = await NewSut().ProcessAsync(
            new PendingFileItem(file, watchId, PendingFileSource.Watcher), CancellationToken.None);

        r.Outcome.Should().Be(ProcessOutcome.Completed);
        await _aiOrchestrator.DidNotReceive().ExecuteAsync(Arg.Any<AiParseRequest>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        await _tmdb.DidNotReceive().SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>());
        MediaItem m = ReadBySource(file);
        m.TmdbId.Should().Be(1042);
        m.ParseSource.Should().Be(ParseSource.Hybrid);
        m.ParsedInfo.Should().Contain("\"episode\":3");
    }

    // ---------- 20b. 兄弟目录同剧复用：追更下载「每集一个目录」跨目录还原 series 身份 ----------

    [Fact]
    public async Task SiblingFolder_PerEpisodeDirectories_ReusesSeries_SkipsSearchAndAi()
    {
        // 第 1 集在「剧名[第01集]xxx」目录已归档；第 2 集落在「剧名[第02集]xxx」新目录——
        // 精确目录键永不命中，旧实现每个目录首集都重烧搜索/AI。兄弟目录兜底：目录名剥集号段
        // 归一化后相同 → 视为同剧，直接复用已归档的 series 身份，跳过 TMDB 搜索与 AI
        string watchRoot = Path.Combine(Path.GetTempPath(), $"pmm-sib-{Guid.NewGuid():N}");
        long watchId = SeedWatchFolder(watchRoot);
        string dir1 = Path.Combine(watchRoot, "金特务：本色回归[第01集][简繁英字幕].Agent.Kim.S01.1080p");
        string dir2 = Path.Combine(watchRoot, "金特务：本色回归[第02集][简繁英字幕].Agent.Kim.S01.1080p");
        SeedArchivedTvItem(Path.Combine(dir1, "Agent.Kim.S01E01.mkv"), tmdbId: 700, title: "金特务：本色回归", year: 2026);
        string file2 = Path.Combine(dir2, "Agent.Kim.S01E02.mkv");

        _ruleEngine.ParseAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>())
            .Returns(new RuleParseResult("金特务：本色回归", 2026, "tv", 1, 2, null, 0.9, false, 1));
        ConfigureClassify(ClassifyDecision.Matched, 3);
        ConfigureArchive(ArchiveOutcome.Completed, "/Tv/金特务/S01E02.mkv");

        ProcessFileOutcome r = await NewSut().ProcessAsync(
            new PendingFileItem(file2, watchId, PendingFileSource.Watcher), CancellationToken.None);

        r.Outcome.Should().Be(ProcessOutcome.Completed);
        await _tmdb.DidNotReceive().SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>());
        await _aiOrchestrator.DidNotReceive().ExecuteAsync(Arg.Any<AiParseRequest>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        MediaItem m = ReadBySource(file2);
        m.TmdbId.Should().Be(700);
        m.ParseSource.Should().Be(ParseSource.Hybrid);
    }

    /// <summary>Seed 一条已归档（Completed）的 TV 记录，供兄弟目录 / L2 持久映射还原类用例使用</summary>
    private void SeedArchivedTvItem(string sourcePath, int tmdbId, string title, int? year)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        MediaItem m = MediaItem.CreateDetected(sourcePath, Path.GetFileName(sourcePath), 1024);
        m.Transition(MediaItemStatus.Queued);
        m.Transition(MediaItemStatus.Parsing);
        m.Transition(MediaItemStatus.TmdbMatching);
        m.ApplyTmdbMatch(tmdbId, "tv", ParseSource.Rule, 0.9,
            ParsedInfo.CreateFromOverride(title, year, "tv", 1, 1));
        m.Transition(MediaItemStatus.Classifying);
        m.Transition(MediaItemStatus.Archiving);
        m.SetArchiveResult("/archived/" + Path.GetFileName(sourcePath));
        m.Transition(MediaItemStatus.Completed);
        db.MediaItems.Add(m);
        db.SaveChanges();
    }

    // ---------- 21. 写入检测失败终态化（源消失 / 写入超时 → Failed，不再滞留僵尸行）----------

    [Fact]
    public async Task WriteDetectFail_SourceMissing_Terminalizes_Existing_Row_As_Failed()
    {
        // FileIntakeService 先建的 Detected 行 + 检测时文件已消失 → MarkFailed（旧实现 return Skipped 滞留僵尸行）
        long id = SeedMediaItemAtStatus(MediaItemStatus.Detected);
        _writeDetector.WaitUntilCompleteAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _fileProbe.FileExists(_tempFile).Returns(false);   // 复核：源文件已消失

        ProcessFileOutcome r = await Run();

        r.Outcome.Should().Be(ProcessOutcome.Failed);
        r.MediaItemId.Should().Be(id);
        MediaItem m = ReadOne();
        m.Status.Should().Be(MediaItemStatus.Failed);
        m.ErrorMessage.Should().Contain("源文件已消失");
        // 终态化前不进入解析管线
        await _ruleEngine.DidNotReceive().ParseAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>());
        await _webhook.Received(1).EmitAsync(WebhookEvents.MediaFailed, Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WriteDetectFail_Timeout_Terminalizes_Existing_Row_As_Failed_Rescannable()
    {
        // 文件还在但大小始终不稳定（大文件复制 > 超时窗口）→ Failed（文案提示完成后可重新扫描）
        long id = SeedMediaItemAtStatus(MediaItemStatus.Detected);
        _writeDetector.WaitUntilCompleteAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(false);
        // 默认 _fileProbe.FileExists=true：文件仍在 → 归类「写入超时」而非「源消失」

        ProcessFileOutcome r = await Run();

        r.Outcome.Should().Be(ProcessOutcome.Failed);
        r.MediaItemId.Should().Be(id);
        MediaItem m = ReadOne();
        m.Status.Should().Be(MediaItemStatus.Failed);
        m.ErrorMessage.Should().Contain("写入超时");
        // Failed 是可重投终态：Rescan / 强制全扫可拉回 Queued（文件写完后自动救回的闭环前提）
        m.Transition(MediaItemStatus.Queued);
        m.Status.Should().Be(MediaItemStatus.Queued);
    }

    // ---------- 22. 崩溃窗口防护：TargetPath 先行小事务持久化 ----------

    [Fact]
    public async Task ArchiveMove_Crash_Before_TerminalSave_TargetPath_Already_Persisted()
    {
        // 模拟「视频已 Move、终态(Completed)落库前进程崩溃」（崩溃后该上下文所有保存均失败）。
        // 断言：库里 TargetPath 已在、状态停在 Archiving —— 正是 StartupRecoveryWorker
        // 「Archiving + TargetPath 文件存在 → 推进 Completed」恢复路径的前置条件；
        // 若 TargetPath 与 Completed 同一事务提交，崩溃窗口内 TargetPath=null 会被重启误判 Failed。
        CrashBeforeCompletedSaveInterceptor crash = new();
        TestDbContextFactory crashFactory = new(_connection, crash);
        ConfigureRule(confidence: 0.9, hasSpecialChars: false);
        ConfigureTmdb(candidates: 1);
        ConfigureClassify(ClassifyDecision.Matched, 1);
        ConfigureArchive(ArchiveOutcome.Completed, "/M/Persisted-First.mkv");

        ProcessFileService sut = new(
            crashFactory, _writeDetector, _ruleEngine, _forcedMatch, _tmdb, _aiOrchestrator, _folderCache,
            _classify, _archive, _fileHasher, _fileProbe, _audioProbe, new NullTaskNotifier(), _webhook, new SystemClock(),
            NullLogger<ProcessFileService>.Instance);

        ProcessFileOutcome r = await sut.ProcessAsync(
            new PendingFileItem(_tempFile, WatchFolderId: 1, PendingFileSource.Watcher), CancellationToken.None);

        r.Outcome.Should().Be(ProcessOutcome.Failed, "终态落库失败由上层按失败感知（实际文件已落地，重启恢复收尾）");
        MediaItem m = ReadOne();
        m.TargetPath.Should().Be("/M/Persisted-First.mkv", "TargetPath 必须在终态落库前先行持久化（崩溃窗口防护）");
        m.Status.Should().Be(MediaItemStatus.Archiving, "崩溃点在终态落库前 → 状态停在 Archiving，由启动恢复推进 Completed");
    }

    // ---------- 23. 特别篇（OVA/SP/特别篇等）禁用单季自动补季 ----------

    [Theory]
    [InlineData("Some Show OVA")]   // ASCII 标记（词边界）
    [InlineData("Some Show SP01")]  // SP + 数字
    [InlineData("某剧 特别篇")]      // CJK 标记
    public async Task SpecialEpisodeMarker_DisablesSingleSeasonAutofill_GoesToReview(string title)
    {
        // 带特别篇标记 + 缺季有集：即便 TMDB 仅 1 季也不得自动补成 S01 正片（特别篇语义归 Season 00）→ 转人工定季
        _ruleEngine.ParseAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>())
            .Returns(new RuleParseResult(title, null, "tv", null, 1, null, 0.9, false, 1));
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult([NewCandidate(777, "tv", title, year: null)], null));
        _tmdb.GetDetailsAsync(777, "tv", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(777, "tv", title, title, 2020, 1, null, ["JP"], "ja", null, null, "{}"));

        ProcessFileOutcome r = await Run();

        r.Outcome.Should().Be(ProcessOutcome.AwaitingReview);
        MediaItem m = ReadOne();
        m.ReviewReason.Should().Be(ReviewReason.ParseIncomplete);
        m.ParsedInfo.Should().NotContain("\"season\":1", "特别篇不得被自动补成第 1 季正片");
        // 标记命中即禁用补季：连 TMDB 季数都不必查
        await _tmdb.DidNotReceive().GetDetailsAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _archive.DidNotReceive().ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NormalEpisode_NoMarker_SingleSeasonAutofill_StillWorks()
    {
        // 反例守护：无特别篇标记的常规缺季单季剧，自动补季照常工作（确认守护没把正常路径误伤）
        _ruleEngine.ParseAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>())
            .Returns(new RuleParseResult("Spy Family", null, "tv", null, 5, null, 0.9, false, 1));
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult([NewCandidate(888, "tv", "Spy Family", year: null)], null));
        _tmdb.GetDetailsAsync(888, "tv", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(888, "tv", "Spy Family", "Spy Family", 2022, 1, null, ["JP"], "ja", null, null, "{}"));
        ConfigureClassify(ClassifyDecision.Matched, 1);
        ConfigureArchive(ArchiveOutcome.Completed, "/Tv/SpyFamily/S01E05.mkv");

        ProcessFileOutcome r = await Run();

        r.Outcome.Should().Be(ProcessOutcome.Completed);
        ReadOne().ParsedInfo.Should().Contain("\"season\":1", "无标记单季剧应自动补 S01");
    }

    // ---------- 24. 强制匹配标识（pmm.txt / TMDB URL）----------

    [Fact(DisplayName = "强制匹配：仅锚 series + 覆盖季 → 跳过规则/AI/搜索，ParseSource=Manual")]
    public async Task ForcedMatch_SeriesAnchor_SkipsEverything_ParseSourceManual()
    {
        // 标识：tmdb=555 tv season=1；文件规则只解析出集号 5（季缺）
        _forcedMatch.TryReadAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>())
            .Returns(new ForcedMatchMarker(555, "tv", Season: 1, EpisodeGroupId: null, GroupId: null, TitleOverride: null));
        ConfigureRule(confidence: 0.2, hasSpecialChars: true, season: null, episode: 5, title: "杂质文件名");
        _tmdb.GetDetailsAsync(555, "tv", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(555, "tv", "机动战士高达SEED", "Gundam SEED", 2002, 2, null, ["JP"], "ja", null, null, "{}"));
        ConfigureClassify(ClassifyDecision.Matched, 3);
        ConfigureArchive(ArchiveOutcome.Completed, "/Tv/Gundam/S01E05.mkv");

        ProcessFileOutcome r = await Run();

        r.Outcome.Should().Be(ProcessOutcome.Completed);
        MediaItem m = ReadOne();
        m.ParseSource.Should().Be(ParseSource.Manual);
        m.TmdbId.Should().Be(555);
        m.TmdbMediaType.Should().Be("tv");
        m.ParsedInfo.Should().Contain("\"season\":1").And.Contain("\"episode\":5");
        // 非剧集组强制匹配：无翻译重排 → 源命名空间集号字段保持 null（归档侧回退正典，行为不变）
        m.ParsedInfo.Should().Contain("\"originalEpisode\":null");
        // 强制匹配：绝不发 TMDB 搜索、绝不走 AI（即便规则置信度低 + 特殊字符）
        await _tmdb.DidNotReceive().SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>());
        await _aiOrchestrator.DidNotReceive().ExecuteAsync(Arg.Any<AiParseRequest>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "强制匹配：剧集组把第 1 集翻译成正典 S01E02（重排证明非恒等）")]
    public async Task ForcedMatch_EpisodeGroup_TranslatesEpisode()
    {
        _forcedMatch.TryReadAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>())
            .Returns(new ForcedMatchMarker(20111, "tv", Season: null, EpisodeGroupId: "eg1", GroupId: "g1", TitleOverride: null));
        // 文件第 1 集
        ConfigureRule(confidence: 0.9, hasSpecialChars: false, season: null, episode: 1, title: "Gundam SEED HD Remaster");
        _tmdb.GetDetailsAsync(20111, "tv", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(20111, "tv", "机动战士高达SEED", "Gundam SEED", 2002, 1, null, ["JP"], "ja", null, null, "{}"));
        // 剧集组：编组内第 1 位(order 0)→ 正典 S01E02，第 2 位(order 1)→ S01E01
        _tmdb.GetEpisodeGroupAsync("eg1", Arg.Any<CancellationToken>())
            .Returns(new TmdbEpisodeGroup("eg1", "HD Remaster", 6, new[]
            {
                new TmdbEpisodeGroupSegment("g1", "HD Remaster", 1, new[]
                {
                    new TmdbEpisodeGroupEntry(0, 1, 2, "假面的下方", 1001),
                    new TmdbEpisodeGroupEntry(1, 1, 1, "崩溃的大地", 1002),
                }),
            }));
        ConfigureClassify(ClassifyDecision.Matched, 3);
        ConfigureArchive(ArchiveOutcome.Completed, "/Tv/Gundam/S01E02.mkv");

        ProcessFileOutcome r = await Run();

        r.Outcome.Should().Be(ProcessOutcome.Completed);
        MediaItem m = ReadOne();
        m.ParseSource.Should().Be(ParseSource.Manual);
        m.TmdbId.Should().Be(20111);
        // 关键：第 1 集被翻译成正典 S01E02（而非机械的 S01E01）
        m.ParsedInfo.Should().Contain("\"season\":1").And.Contain("\"episode\":2");
        // 翻译前的源文件名命名空间集号（编组内第 1 集）须随 ParsedInfo 透传，供归档阶段字幕按原始集号归属匹配
        m.ParsedInfo.Should().Contain("\"originalEpisode\":1");
        await _tmdb.DidNotReceive().SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "强制匹配：标识 TMDB id 无效（详情拉取抛错）→ AwaitingReview")]
    public async Task ForcedMatch_InvalidId_GoesToReview()
    {
        _forcedMatch.TryReadAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>())
            .Returns(new ForcedMatchMarker(999999, "tv", null, null, null, null));
        ConfigureRule(confidence: 0.9, hasSpecialChars: false, season: 1, episode: 1);
        _tmdb.GetDetailsAsync(999999, "tv", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<TmdbDetailsResult>(new TmdbClientException("TMDB 404：id 不存在", 404)));

        ProcessFileOutcome r = await Run();

        r.Outcome.Should().Be(ProcessOutcome.AwaitingReview);
        MediaItem m = ReadOne();
        m.ReviewReason.Should().Be(ReviewReason.TmdbZeroResult);
        await _tmdb.DidNotReceive().SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>());
        await _archive.DidNotReceive().ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "强制匹配：剧集组集号越界 → 翻译失败清空集号 → 完整性守护转 AwaitingReview")]
    public async Task ForcedMatch_EpisodeGroup_OutOfRange_GoesToReview()
    {
        _forcedMatch.TryReadAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>())
            .Returns(new ForcedMatchMarker(20111, "tv", Season: null, EpisodeGroupId: "eg1", GroupId: "g1", TitleOverride: null));
        ConfigureRule(confidence: 0.9, hasSpecialChars: false, season: null, episode: 99, title: "Gundam SEED HD Remaster");
        _tmdb.GetDetailsAsync(20111, "tv", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(20111, "tv", "机动战士高达SEED", "Gundam SEED", 2002, 1, null, ["JP"], "ja", null, null, "{}"));
        _tmdb.GetEpisodeGroupAsync("eg1", Arg.Any<CancellationToken>())
            .Returns(new TmdbEpisodeGroup("eg1", "HD Remaster", 6, new[]
            {
                new TmdbEpisodeGroupSegment("g1", "HD Remaster", 1, new[]
                {
                    new TmdbEpisodeGroupEntry(0, 1, 2, "E1", 1001),
                    new TmdbEpisodeGroupEntry(1, 1, 1, "E2", 1002),
                }),
            }));

        ProcessFileOutcome r = await Run();

        r.Outcome.Should().Be(ProcessOutcome.AwaitingReview);
        ReadOne().ReviewReason.Should().Be(ReviewReason.ParseIncomplete);
        await _archive.DidNotReceive().ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "强制匹配：剧集组双集顺序映射 → 末集随起始集翻译，保留连续区间 S01E05-E06")]
    public async Task ForcedMatch_EpisodeGroup_DoubleEpisode_SequentialKept()
    {
        _forcedMatch.TryReadAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>())
            .Returns(new ForcedMatchMarker(20111, "tv", Season: null, EpisodeGroupId: "eg1", GroupId: "g1", TitleOverride: null));
        // 文件「第 1-2 集」（编组内位置 1-2）；规则解析出双集合并 episode=1 / episodeEnd=2
        _ruleEngine.ParseAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>())
            .Returns(new RuleParseResult("Gundam SEED HD Remaster", null, "tv", null, 1, 2, 0.9, false, 1));
        _tmdb.GetDetailsAsync(20111, "tv", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(20111, "tv", "机动战士高达SEED", "Gundam SEED", 2002, 2, null, ["JP"], "ja", null, null, "{}"));
        // 编组该段顺序映射：位置 1(order 0)→ 正典 S01E05，位置 2(order 1)→ S01E06（正典跨度 == 编组内跨度=1）
        _tmdb.GetEpisodeGroupAsync("eg1", Arg.Any<CancellationToken>())
            .Returns(new TmdbEpisodeGroup("eg1", "HD Remaster", 6, new[]
            {
                new TmdbEpisodeGroupSegment("g1", "HD Remaster", 1, new[]
                {
                    new TmdbEpisodeGroupEntry(0, 1, 5, "E5", 1005),
                    new TmdbEpisodeGroupEntry(1, 1, 6, "E6", 1006),
                }),
            }));
        ConfigureClassify(ClassifyDecision.Matched, 3);
        ConfigureArchive(ArchiveOutcome.Completed, "/Tv/Gundam/S01E05-E06.mkv");

        ProcessFileOutcome r = await Run();

        r.Outcome.Should().Be(ProcessOutcome.Completed);
        ParsedInfo pi = ParsedInfo.FromJson(ReadOne().ParsedInfo)!;
        pi.Season.Should().Be(1);
        pi.Episode.Should().Be(5);
        // 关键：双集末集随起始集一并翻译为正典连续区间 E05-E06（修复前会沿用未翻译的原始末集 2 → S01E05-E02 非法）
        pi.EpisodeEnd.Should().Be(6);
        await _tmdb.DidNotReceive().SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "强制匹配：剧集组双集翻译后非连续（乱序）→ 退化单集，不产生非法区间")]
    public async Task ForcedMatch_EpisodeGroup_DoubleEpisode_NonContiguousDegradesToSingle()
    {
        _forcedMatch.TryReadAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>())
            .Returns(new ForcedMatchMarker(20111, "tv", Season: null, EpisodeGroupId: "eg1", GroupId: "g1", TitleOverride: null));
        // 文件「第 1-2 集」；规则解析双集合并 episode=1 / episodeEnd=2
        _ruleEngine.ParseAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>())
            .Returns(new RuleParseResult("Gundam SEED HD Remaster", null, "tv", null, 1, 2, 0.9, false, 1));
        _tmdb.GetDetailsAsync(20111, "tv", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(20111, "tv", "机动战士高达SEED", "Gundam SEED", 2002, 2, null, ["JP"], "ja", null, null, "{}"));
        // 编组重排乱序：位置 1(order 0)→ 正典 S01E02，位置 2(order 1)→ S01E01（翻译后末集 < 起始集，非连续）
        _tmdb.GetEpisodeGroupAsync("eg1", Arg.Any<CancellationToken>())
            .Returns(new TmdbEpisodeGroup("eg1", "HD Remaster", 6, new[]
            {
                new TmdbEpisodeGroupSegment("g1", "HD Remaster", 1, new[]
                {
                    new TmdbEpisodeGroupEntry(0, 1, 2, "E2", 1002),
                    new TmdbEpisodeGroupEntry(1, 1, 1, "E1", 1001),
                }),
            }));
        ConfigureClassify(ClassifyDecision.Matched, 3);
        ConfigureArchive(ArchiveOutcome.Completed, "/Tv/Gundam/S01E02.mkv");

        ProcessFileOutcome r = await Run();

        r.Outcome.Should().Be(ProcessOutcome.Completed);
        ParsedInfo pi = ParsedInfo.FromJson(ReadOne().ParsedInfo)!;
        pi.Season.Should().Be(1);
        pi.Episode.Should().Be(2);
        // 关键：末集翻译后为 E01（< 起始 E02），构不成合法连续区间 → 退化单集，EpisodeEnd 清空
        pi.EpisodeEnd.Should().BeNull();
    }

    [Fact(DisplayName = "强制匹配：文件夹名 {tmdb-NNN} 标记 → 仅锚 id，类型/季由规则识别，ParseSource=Manual")]
    public async Task ForcedMatch_FolderNameMarker_AnchorsIdOnly_TypeFromRule()
    {
        // 文件夹名标记只给 id（MediaType=null）：类型 / 季 / 集全由规则识别，标识不锁
        _forcedMatch.TryReadAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>())
            .Returns(new ForcedMatchMarker(45782, MediaType: null, Season: null, EpisodeGroupId: null, GroupId: null, TitleOverride: null));
        // 规则识别为 tv、第 1 季第 5 集
        _ruleEngine.ParseAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>())
            .Returns(new RuleParseResult("刀剑神域", 2012, "tv", 1, 5, null, 0.9, false, 1));
        _tmdb.GetDetailsAsync(45782, "tv", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(45782, "tv", "刀剑神域", "Sword Art Online", 2012, 4, null, ["JP"], "ja", null, null, "{}"));
        ConfigureClassify(ClassifyDecision.Matched, 3);
        ConfigureArchive(ArchiveOutcome.Completed, "/Tv/SAO/S01E05.mkv");

        ProcessFileOutcome r = await Run();

        r.Outcome.Should().Be(ProcessOutcome.Completed);
        MediaItem m = ReadOne();
        m.ParseSource.Should().Be(ParseSource.Manual);
        m.TmdbId.Should().Be(45782);
        m.TmdbMediaType.Should().Be("tv");   // 类型取自规则识别（标识只给了 id）
        m.ParsedInfo.Should().Contain("\"season\":1").And.Contain("\"episode\":5");
        // 类型由规则正确识别 → 只按 tv 拉一次，绝不触发另一类型回退
        await _tmdb.DidNotReceive().GetDetailsAsync(45782, "movie", Arg.Any<CancellationToken>());
        // 强制匹配：绝不发 TMDB 搜索 / 绝不走 AI
        await _tmdb.DidNotReceive().SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>());
        await _aiOrchestrator.DidNotReceive().ExecuteAsync(Arg.Any<AiParseRequest>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "强制匹配：文件夹名标记类型猜反（规则判 tv 实为 movie）→ 翻 movie 兜底成功")]
    public async Task ForcedMatch_FolderNameMarker_TypeFallback_TvToMovie()
    {
        _forcedMatch.TryReadAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>())
            .Returns(new ForcedMatchMarker(27205, MediaType: null, Season: null, EpisodeGroupId: null, GroupId: null, TitleOverride: null));
        // 规则误判为 tv，但 27205 实为电影
        _ruleEngine.ParseAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>())
            .Returns(new RuleParseResult("盗梦空间", 2010, "tv", null, null, null, 0.9, false, 1));
        // 首选类型 tv 拉取 404，回退 movie 命中（TMDB 的 tv/{id} 与 movie/{id} 是独立命名空间）
        _tmdb.GetDetailsAsync(27205, "tv", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<TmdbDetailsResult>(new TmdbClientException("TMDB 404：id 不存在", 404)));
        _tmdb.GetDetailsAsync(27205, "movie", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(27205, "movie", "盗梦空间", "Inception", 2010, null, null, ["US"], "en", null, null, "{}"));
        ConfigureClassify(ClassifyDecision.Matched, 3);
        ConfigureArchive(ArchiveOutcome.Completed, "/Movie/Inception (2010).mkv");

        ProcessFileOutcome r = await Run();

        r.Outcome.Should().Be(ProcessOutcome.Completed);
        MediaItem m = ReadOne();
        m.ParseSource.Should().Be(ParseSource.Manual);
        m.TmdbId.Should().Be(27205);
        m.TmdbMediaType.Should().Be("movie");   // 回退到正确类型 movie
        // 验证确实先试 tv 再翻 movie（各一次），且未退化为 TMDB 搜索
        await _tmdb.Received(1).GetDetailsAsync(27205, "tv", Arg.Any<CancellationToken>());
        await _tmdb.Received(1).GetDetailsAsync(27205, "movie", Arg.Any<CancellationToken>());
        await _tmdb.DidNotReceive().SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "强制匹配：文件夹名标记 id 两种类型都拉不到 → AwaitingReview")]
    public async Task ForcedMatch_FolderNameMarker_BothTypesFail_GoesToReview()
    {
        _forcedMatch.TryReadAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>())
            .Returns(new ForcedMatchMarker(999999, MediaType: null, Season: null, EpisodeGroupId: null, GroupId: null, TitleOverride: null));
        _ruleEngine.ParseAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>())
            .Returns(new RuleParseResult("不存在的片", 2010, "tv", 1, 1, null, 0.9, false, 1));
        _tmdb.GetDetailsAsync(999999, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<TmdbDetailsResult>(new TmdbClientException("TMDB 404：id 不存在", 404)));

        ProcessFileOutcome r = await Run();

        r.Outcome.Should().Be(ProcessOutcome.AwaitingReview);
        ReadOne().ReviewReason.Should().Be(ReviewReason.TmdbZeroResult);
        // 两种类型都尝试过
        await _tmdb.Received(1).GetDetailsAsync(999999, "tv", Arg.Any<CancellationToken>());
        await _tmdb.Received(1).GetDetailsAsync(999999, "movie", Arg.Any<CancellationToken>());
        await _archive.DidNotReceive().ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>());
    }

    private void SeedCompletedWithHash(string sourcePath, string hash, string? targetPath = null)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.MediaItems.Add(MediaItem.CreateFixture(sourcePath, Path.GetFileName(sourcePath), 2048,
            status: MediaItemStatus.Completed, targetPath: targetPath ?? $"/archived/{Path.GetFileName(sourcePath)}", fileHash: hash));
        db.SaveChanges();
    }

    private MediaItem ReadBySource(string sourcePath)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        return db.MediaItems.AsNoTracking().Single(m => m.SourcePath == sourcePath);
    }

    // ---------- helpers ----------

    private ProcessFileService NewSut() => NewSutWithCache(_folderCache);

    /// <summary>用指定 folderCache 实例构造 SUT：传入全新空缓存即可模拟「进程重启、内存缓存清空」场景</summary>
    private ProcessFileService NewSutWithCache(IFolderSeriesCache cache) => new(
        _dbFactory, _writeDetector, _ruleEngine, _forcedMatch, _tmdb, _aiOrchestrator, cache,
        _classify, _archive, _fileHasher, _fileProbe, _audioProbe, new NullTaskNotifier(), _webhook, new SystemClock(), NullLogger<ProcessFileService>.Instance);

    private Task<ProcessFileOutcome> Run(CancellationToken? ct = null)
        => NewSut().ProcessAsync(
            new PendingFileItem(_tempFile, WatchFolderId: 1, PendingFileSource.Watcher),
            ct ?? CancellationToken.None);

    private MediaItem ReadOne()
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        return db.MediaItems.AsNoTracking().Single();
    }

    private long SeedMediaItemAtStatus(MediaItemStatus status)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        MediaItem m = MediaItem.CreateDetected(_tempFile, Path.GetFileName(_tempFile), 0);
        if (status != MediaItemStatus.Detected)
        {
            // 一路推过去（合法转移）；Failed 走 MarkFailed
            switch (status)
            {
                case MediaItemStatus.Completed:
                    m.Transition(MediaItemStatus.Queued);
                    m.Transition(MediaItemStatus.Parsing);
                    m.Transition(MediaItemStatus.TmdbMatching);
                    m.Transition(MediaItemStatus.Classifying);
                    m.Transition(MediaItemStatus.Archiving);
                    m.Transition(MediaItemStatus.Completed);
                    break;
                case MediaItemStatus.Skipped:
                    m.Transition(MediaItemStatus.Queued);
                    m.Transition(MediaItemStatus.Parsing);
                    m.Transition(MediaItemStatus.TmdbMatching);
                    m.Transition(MediaItemStatus.Classifying);
                    m.Transition(MediaItemStatus.Archiving);
                    m.Transition(MediaItemStatus.Skipped);
                    break;
                case MediaItemStatus.Ignored:
                    m.Transition(MediaItemStatus.Queued);
                    m.Transition(MediaItemStatus.Parsing);
                    m.Transition(MediaItemStatus.AiParsing);
                    m.Transition(MediaItemStatus.TmdbRematching);
                    m.Transition(MediaItemStatus.AwaitingReview);
                    m.Transition(MediaItemStatus.Ignored);
                    break;
                case MediaItemStatus.Cancelled:
                    m.Transition(MediaItemStatus.Cancelled);
                    break;
                case MediaItemStatus.Failed:
                    m.MarkFailed("seed-failed");
                    break;
            }
        }
        db.MediaItems.Add(m);
        db.SaveChanges();
        return m.Id;
    }

    private long SeedWatchFolder(string path)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        WatchFolder w = new() { Path = path };
        db.WatchFolders.Add(w);
        db.SaveChanges();
        return w.Id;
    }

    private void ConfigureRule(double confidence, bool hasSpecialChars, int? year = 2010, string title = "Sample",
        int? season = null, int? episode = null, string mediaType = "movie", string[]? alternativeTitles = null)
    {
        _ruleEngine.ParseAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>())
            .Returns(new RuleParseResult(title, year, mediaType, season, episode, EpisodeEnd: null, confidence, hasSpecialChars, MatchedRuleId: 1,
                AlternativeTitles: alternativeTitles));
    }

    private void ConfigureTmdb(int candidates)
    {
        List<TmdbCandidate> list = Enumerable.Range(0, candidates)
            .Select(i => NewCandidate(100 + i, "movie")).ToList();
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult(list, null));
    }

    private void ConfigureAi(bool success, string title = "AI-Title", int year = 2011, string mediaType = "movie", string[]? aliases = null)
    {
        _aiOrchestrator.ExecuteAsync(Arg.Any<AiParseRequest>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(success
                ? new AiCallOutcome(true, new AiParseResult(title, year, mediaType, Season: null, Episode: null, EpisodeEnd: null, 0.85, aliases), 1L, 1, null)
                : new AiCallOutcome(false, null, null, 2, "all providers failed"));
    }

    private void ConfigureClassify(ClassifyDecision decision, long? categoryId)
    {
        if (categoryId.HasValue)
        {
            SeedCategory(categoryId.Value);
        }
        _classify.ClassifyAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>())
            .Returns(new ClassifyResult(categoryId, decision));
    }

    private void SeedCategory(long id)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        if (db.CategoryDefinitions.Any(c => c.Id == id)) return;
        CategoryDefinition c = new()
        {
            Name = $"Cat-{id}",
            MediaType = MediaType.Movie,
            TargetRoot = "/test/root",
        };
        // 让 Id 由数据库自增；如果 id 不匹配则插入并忽略指定 id 测试方便
        db.CategoryDefinitions.Add(c);
        db.SaveChanges();
        if (c.Id != id)
        {
            // 用 raw SQL 强制把 id 改成测试期望的值，让 ClassifyResult 与 FK 对得上
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE Category_Definition SET Id=$new WHERE Id=$old";
            cmd.Parameters.AddWithValue("$new", id);
            cmd.Parameters.AddWithValue("$old", c.Id);
            cmd.ExecuteNonQuery();
        }
    }

    private void ConfigureArchive(ArchiveOutcome outcome, string targetPath, IReadOnlyList<string>? warnings = null)
    {
        _archive.ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>())
            .Returns(new ArchiveResult(targetPath, outcome, warnings));
    }

    /// <summary>改写 Tmdb_Setting 单例行（EnsureCreated 已按 HasData 种子注入 Id=1）</summary>
    private void UpdateTmdbSetting(Action<TmdbSetting> mutate)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        TmdbSetting setting = db.TmdbSettings.Single(x => x.Id == 1);
        mutate(setting);
        db.SaveChanges();
    }

    /// <summary>构造候选；title/year 可定制——四维择优落地后，候选标题/年份须与解析结果匹配才能过得分门槛（贴近真实 TMDB 返回）</summary>
    private static TmdbCandidate NewCandidate(int id, string type, string? title = null, int? year = 2010, double popularity = 0.5)
        => new(id, type, title ?? $"Title-{id}", $"Original-{id}", year, popularity, "en", ["US"], null, null);

    /// <summary>IFolderSeriesCache 测试替身（语义同生产实现，避免给 Application internal 加 InternalsVisibleTo）</summary>
    private sealed class InMemoryFolderSeriesCache : IFolderSeriesCache
    {
        private readonly Dictionary<string, FolderSeriesEntry> _map = new(StringComparer.OrdinalIgnoreCase);
        public FolderSeriesEntry? TryGet(string folderPath) =>
            _map.TryGetValue(folderPath, out FolderSeriesEntry? e) ? e : null;
        public void Set(string folderPath, FolderSeriesEntry entry) => _map[folderPath] = entry;
    }

    private sealed class TestDbContextFactory : IDbContextFactory<PmmDbContext>
    {
        private readonly SqliteConnection _connection;
        private readonly IInterceptor? _interceptor;
        public TestDbContextFactory(SqliteConnection c, IInterceptor? interceptor = null)
        {
            _connection = c;
            _interceptor = interceptor;
        }
        public PmmDbContext CreateDbContext()
        {
            DbContextOptionsBuilder<PmmDbContext> opts = new();
            opts.UseSqlite(_connection);
            if (_interceptor is not null) opts.AddInterceptors(_interceptor);
            return new PmmDbContext(opts.Options);
        }
    }

    /// <summary>模拟崩溃：首次把 MediaItem 落为 Completed 的保存即抛异常，且此后所有保存全部失败（进程已死语义）</summary>
    /// <remarks>验证「TargetPath 先行小事务」崩溃窗口防护：第一段保存（Status=Archiving + TargetPath）放行，终态段掐断。</remarks>
    private sealed class CrashBeforeCompletedSaveInterceptor : SaveChangesInterceptor
    {
        private bool _crashed;

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            ThrowIfCompleting(eventData);
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            ThrowIfCompleting(eventData);
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        private void ThrowIfCompleting(DbContextEventData eventData)
        {
            if (_crashed)
                throw new InvalidOperationException("模拟崩溃：进程已死，后续保存全部失败");
            bool completing = eventData.Context!.ChangeTracker.Entries<MediaItem>()
                .Any(e => e.State == EntityState.Modified && e.Entity.Status == MediaItemStatus.Completed);
            if (completing)
            {
                _crashed = true;
                throw new InvalidOperationException("模拟崩溃：视频已 Move、终态(Completed)落库前进程中断");
            }
        }
    }
}
