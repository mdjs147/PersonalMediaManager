using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.Review;
using PersonalMediaManager.Application.Services.Archive;
using PersonalMediaManager.Application.Services.Library;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Aggregates.MediaWorks;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Persistence.Services.Review;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

public sealed partial class ReviewServiceTests
{
    private ReviewService AssistService(IRuleEngineService? rules = null, IWorkEnrichmentService? enrichment = null)
        => new(_dbFactory, _tmdb, _archive, _fileProbe, _folderCache, _webhook,
            NullLogger<ReviewService>.Instance, rules: rules, enrichment: enrichment);

    private long SeedAssistItem(string fileName = "12.mp4", int id = 101, string type = "tv", MediaItemStatus status = MediaItemStatus.AwaitingReview)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        MediaItem item = MediaItem.CreateFixture($"/review/{Guid.NewGuid():N}/{fileName}", fileName, 1024, status,
            parseSource: ParseSource.Rule, parsedInfo: new ParsedInfo("合成连续剧", 2020, type, 2, 12, null, null).ToJson(),
            tmdbId: id, tmdbMediaType: type);
        db.MediaItems.Add(item); db.SaveChanges(); return item.Id;
    }
    private void MockCompleteCatalogue()
    {
        TmdbDetailsResult details = new(101, "tv", "合成连续剧", "Synthetic Show", 2020, 2, null, null, null, null, null,
            "{}", [new(1, 10), new(2, 8)], CachedAt: DateTimeOffset.UtcNow);
        _tmdb.GetDetailsFreshAsync(101, "tv", Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(details);
        for (int sn = 1; sn <= 2; sn++)
        {
            int count = sn == 1 ? 10 : 8;
            TmdbSeasonDetail season = new(sn, $"第{sn}季", null, null, null,
                Enumerable.Range(1, count).Select(ep => new TmdbEpisodeRef(ep, "集名", "介绍", null,
                    DateTimeOffset.UtcNow.AddYears(-1), 30, null)).ToArray());
            _tmdb.GetSeasonCatalogueAsync(101, sn, Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(new TmdbSeasonCatalogueResult(101, "tv", season, DateTimeOffset.UtcNow));
        }
    }
    private static IRuleEngineService NumericRules(int number = 12, int? season = null)
    {
        IRuleEngineService rules = Substitute.For<IRuleEngineService>();
        rules.ParseAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>()).Returns(new RuleParseResult(
            "合成连续剧", 2020, "tv", season, number, null, 1, false, null,
            NumberingEvidence: [new(RuleNumberingKind.LocalEpisode, RuleEvidenceState.Accepted, "episode", "FileName", null, 0, 2, number.ToString(), number)]));
        return rules;
    }
    [Fact]
    public async Task EpisodeHints_AreReadOnly_AndRejectStaleRows()
    {
        long id = SeedAssistItem(); MediaItem before = ReadItem(id);
        ReviewService service = AssistService(NumericRules());
        ReviewEpisodeHintsResult result = await service.EpisodeHintsAsync(new([new(id, before.RowVersion), new(id, before.RowVersion + 1)]));
        result.Items[0].Episode.Should().Be(12); result.Items[0].Source.Should().Be("RuleExtraction");
        result.Items[1].Error.Should().Contain("已被其他用户修改");
        ReadItem(id).ParsedInfo.Should().Be(before.ParsedInfo);
        ReadItem(id).Status.Should().Be(MediaItemStatus.AwaitingReview);
        await _tmdb.DidNotReceiveWithAnyArgs().GetDetailsAsync(default, default!);
        await _archive.DidNotReceiveWithAnyArgs().ArchiveAsync(default!);
    }
    [Theory]
    [InlineData("tv")] [InlineData("Tv")] [InlineData("TV")]
    public async Task MappingPreview_IsReadOnly_ThenConfirmedTokenPreservesOriginalNumber(string mediaType)
    {
        long id = SeedAssistItem(); MediaItem before = ReadItem(id); long category = SeedCategory();
        MockCompleteCatalogue(); ReviewService service = AssistService(NumericRules());
        ReviewEpisodeMappingResult result = await service.PreviewEpisodeMappingAsync(new(101, "tv", 2, [new(id, before.RowVersion, 12)]));
        ReviewEpisodeMappingEntry entry = result.Items.Single(); entry.Error.Should().BeNull(); entry.Episode.Should().Be(2);
        entry.MappingToken.Should().NotBeNullOrEmpty(); ReadItem(id).ParsedInfo.Should().Be(before.ParsedInfo);
        await _archive.DidNotReceiveWithAnyArgs().ArchiveAsync(default!);
        _archive.ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>()).Returns(new ArchiveResult("/archive/S02E02.mp4", ArchiveOutcome.Completed));
        await service.ConfirmAsync(id, new(101, mediaType, category, "合成连续剧", 2020, 2, 2, before.RowVersion,
            DecisionSource: "AbsoluteMapping", SourceEpisode: 12, MappingToken: entry.MappingToken));
        ParsedInfo info = ParsedInfo.FromJson(ReadItem(id).ParsedInfo)!;
        info.Episode.Should().Be(2); info.OriginalEpisode.Should().Be(12);
        ReadSteps(id).Should().Contain(s => s.Detail != null && s.Detail.Contains("AbsoluteMapping") && s.Detail.Contains("catalogueCachedAt"));
    }
    [Fact]
    public async Task Mapping_CatalogueChangedBeforeConfirmation_RequiresNewPreview()
    {
        long id = SeedAssistItem(); long category = SeedCategory(); MockCompleteCatalogue();
        ReviewService service = AssistService(NumericRules()); long rv = ReadItem(id).RowVersion;
        ReviewEpisodeMappingEntry entry = (await service.PreviewEpisodeMappingAsync(new(101, "tv", 2, [new(id, rv, 12)]))).Items.Single();
        _tmdb.GetSeasonCatalogueAsync(101, 1, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(new TmdbSeasonCatalogueResult(101, "tv", null, RefreshError: "断网"));
        Func<Task> act = () => service.ConfirmAsync(id, new(101, "tv", category, "合成连续剧", 2020, 2, 2, rv,
            DecisionSource: "AbsoluteMapping", SourceEpisode: 12, MappingToken: entry.MappingToken));
        await act.Should().ThrowAsync<BusinessException>();
        ReadItem(id).Status.Should().Be(MediaItemStatus.AwaitingReview);
        await _archive.DidNotReceiveWithAnyArgs().ArchiveAsync(default!);
    }
    [Fact]
    public async Task Mapping_ExplicitLocalNamespace_OrTypedIdentityMismatch_DoNotRequestTmdb()
    {
        long explicitId = SeedAssistItem("Synthetic.S02E14.mkv"); long movieId = SeedAssistItem(type: "movie");
        ReviewService service = AssistService(NumericRules(14, 2));
        ReviewEpisodeMappingResult result = await service.PreviewEpisodeMappingAsync(new(101, "tv", 2,
            [new(explicitId, ReadItem(explicitId).RowVersion, 14), new(movieId, ReadItem(movieId).RowVersion, 12)]));
        result.Items.Should().OnlyContain(e => e.Error != null && e.MappingToken == null);
        await _tmdb.DidNotReceiveWithAnyArgs().GetDetailsFreshAsync(default, default!);
    }
    [Fact]
    public async Task Mapping_DuplicateIds_AreRejectedBeforeCatalogue()
    {
        long id = SeedAssistItem(); ReviewService service = AssistService(NumericRules()); long rv = ReadItem(id).RowVersion;
        Func<Task> act = () => service.PreviewEpisodeMappingAsync(new(101, "tv", 2, [new(id, rv, 12), new(id, rv, 13)]));
        await act.Should().ThrowAsync<BusinessException>().WithMessage("*重复*");
        await _tmdb.DidNotReceiveWithAnyArgs().GetDetailsFreshAsync(default, default!);
    }
    [Fact]
    public async Task LibraryCandidates_UseCompositeIdentity_AndNeverBind()
    {
        long id = SeedAssistItem(); SeedAssistItem(status: MediaItemStatus.Completed); SeedAssistItem(type: "movie", status: MediaItemStatus.Completed);
        using (PmmDbContext db = _dbFactory.CreateDbContext())
        {
            db.MediaWorks.Add(MediaWork.CreateMinimal(101, "tv", "合成连续剧", 2020));
            db.MediaWorks.Add(MediaWork.CreateMinimal(101, "movie", "同数字电影", 2021)); db.SaveChanges();
        }
        ReviewLibraryCandidatesResult result = await AssistService().LibraryCandidatesAsync(id, "合成连续剧");
        result.Items.Should().ContainSingle().Which.MediaType.Should().Be("tv"); result.Items[0].Source.Should().Be("LibraryContext");
        ReadItem(id).Status.Should().Be(MediaItemStatus.AwaitingReview);
        await _tmdb.DidNotReceiveWithAnyArgs().GetDetailsFreshAsync(default, default!);
    }
    [Fact]
    public async Task Confirm_RefreshesSelectedSeasonAndLibrary_AndRecordsFailureWithoutErasingSelection()
    {
        long id = SeedAssistItem(); long category = SeedCategory(); MockCompleteCatalogue();
        IWorkEnrichmentService enrichment = Substitute.For<IWorkEnrichmentService>();
        enrichment.EnrichAsync(101, "tv", true, Arg.Any<CancellationToken>()).Returns(Task.FromException<bool>(new IOException("离线")));
        ReviewService service = AssistService(enrichment: enrichment);
        _archive.ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>()).Returns(new ArchiveResult("/a/S02E02.mp4", ArchiveOutcome.Completed));
        ConfirmResult result = await service.ConfirmAsync(id, new(101, "tv", category, "合成连续剧", 2020, 2, 2, ReadItem(id).RowVersion));
        result.MetadataRefreshError.Should().NotBeNull();
        await _tmdb.Received().GetSeasonCatalogueAsync(101, 2, true, Arg.Any<CancellationToken>());
        ReadItem(id).Status.Should().Be(MediaItemStatus.Completed);
    }
    [Fact]
    public async Task Confirm_EmptyOfflineDetailsCannotValidateTypedIdentity()
    {
        long id = SeedAssistItem(); long category = SeedCategory();
        _tmdb.GetDetailsFreshAsync(101, "tv", true, Arg.Any<CancellationToken>()).Returns(new TmdbDetailsResult(101, "tv", null,
            null, null, null, null, null, null, null, null, "{}", RefreshError: "网络不可用"));
        Func<Task> act = () => AssistService().ConfirmAsync(id, new(101, "tv", category, "合成连续剧", 2020, 2, 2, ReadItem(id).RowVersion));
        await act.Should().ThrowAsync<BusinessException>().WithMessage("*没有有效缓存*");
        await _archive.DidNotReceiveWithAnyArgs().ArchiveAsync(default!);
    }
    [Fact]
    public async Task Mapping_UnboundSourceUsesExplicitSelectionOnly_AndDoesNotBind()
    {
        long id = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Rule, parsedInfo: new ParsedInfo("合成连续剧", 2020, "tv", null, 12, null, null).ToJson());
        MockCompleteCatalogue(); ReviewService service = AssistService(NumericRules());
        ReviewEpisodeMappingEntry entry = (await service.PreviewEpisodeMappingAsync(new(101, "tv", 2, [new(id, ReadItem(id).RowVersion, 12)]))).Items.Single();
        entry.Error.Should().BeNull(); entry.Episode.Should().Be(2); entry.Evidence.Should().Contain(e => e.Contains("未由源文件自动验证"));
        ReadItem(id).TmdbId.Should().BeNull(); await _archive.DidNotReceiveWithAnyArgs().ArchiveAsync(default!);
    }
    [Fact]
    public async Task Mapping_ExplicitAbsoluteEvidenceCanBeReviewedWithoutAdoptingAsLocalEpisode()
    {
        long id = SeedAssistItem("Synthetic.ABS12.mkv"); MockCompleteCatalogue();
        IRuleEngineService rules = Substitute.For<IRuleEngineService>();
        rules.ParseAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>()).Returns(new RuleParseResult(
            "合成连续剧", 2020, "tv", null, null, null, 0.5, false, null,
            NumberingEvidence: [new(RuleNumberingKind.Absolute, RuleEvidenceState.Candidate, "absolute", "FileName", null, 10, 5, "ABS12", 12)]));
        ReviewEpisodeMappingEntry entry = (await AssistService(rules).PreviewEpisodeMappingAsync(new(101, "tv", 2, [new(id, ReadItem(id).RowVersion, 12)]))).Items.Single();
        entry.Error.Should().BeNull(); entry.Episode.Should().Be(2);
    }
    [Fact]
    public async Task BindNewIdentityClearsOldEpisodeNamespace_AndRefreshesOnlyRequestedSeason()
    {
        long id = SeedAssistItem(id: 202); MockCompleteCatalogue();
        await AssistService().BindTmdbAsync(id, new(101, "tv", ReadItem(id).RowVersion, Season: 2));
        ParsedInfo info = ParsedInfo.FromJson(ReadItem(id).ParsedInfo)!;
        info.Season.Should().Be(2); info.Episode.Should().BeNull(); info.OriginalEpisode.Should().BeNull();
        ReadItem(id).Status.Should().Be(MediaItemStatus.AwaitingReview);
        await _tmdb.Received().GetSeasonCatalogueAsync(101, 2, true, Arg.Any<CancellationToken>());
        await _archive.DidNotReceiveWithAnyArgs().ArchiveAsync(default!);
    }
    [Fact]
    public async Task DetailRefreshExposesSeasonTimestampAndFailureWithoutChangingMedia()
    {
        long id = SeedAssistItem(); MockCompleteCatalogue(); DateTimeOffset old = DateTimeOffset.UtcNow.AddDays(-3);
        _tmdb.GetSeasonCatalogueAsync(101, 2, true, Arg.Any<CancellationToken>()).Returns(new TmdbSeasonCatalogueResult(101, "tv", null, old, true, "TMDB 暂时限流"));
        TmdbDetailItem result = await AssistService().TmdbDetailAsync(id, new(101, "tv", 2, true));
        result.Catalogue!.CachedAt.Should().Be(old); result.RefreshError.Should().Contain("限流");
        ReadItem(id).Status.Should().Be(MediaItemStatus.AwaitingReview);
    }
    [Theory]
    [InlineData("season0")] [InlineData("ova")] [InlineData("edition")]
    public async Task Mapping_SpecialFolderAndUnverifiedEdition_CannotUseRegularSeasonOffset(string kind)
    {
        long id = SeedAssistItem(); IRuleEngineService rules = NumericRules(season: kind == "season0" ? 0 : null);
        RuleParseResult rule = await rules.ParseAsync(FileParseContext.FileNameOnly("12.mp4"));
        if (kind == "ova") rule = rule with { NumberingEvidence = [new(RuleNumberingKind.ContentKind,
            RuleEvidenceState.Candidate, "contentKind", "RelativeSegment", 0, 0, 3, "OVA", TextValue: "OVA")] };
        if (kind == "edition") rule = rule with { NamingEvidence = new RuleNamingEvidence("合成连续剧", ["HD Remaster"]) };
        rules.ParseAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>()).Returns(rule);
        ReviewEpisodeMappingEntry entry = (await AssistService(rules).PreviewEpisodeMappingAsync(new(101, "tv", 2,
            [new(id, ReadItem(id).RowVersion, 12)]))).Items.Single();
        entry.Error.Should().NotBeNull(); entry.MappingToken.Should().BeNull();
        await _tmdb.DidNotReceiveWithAnyArgs().GetDetailsFreshAsync(default, default!);
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task MappingConfirmation_ForceRefreshFailureCannotBeHiddenByLaterCacheRead(bool detailsFailure)
    {
        long id = SeedAssistItem(); long category = SeedCategory(); MockCompleteCatalogue();
        ReviewService service = AssistService(NumericRules()); long rv = ReadItem(id).RowVersion;
        ReviewEpisodeMappingEntry entry = (await service.PreviewEpisodeMappingAsync(new(101, "tv", 2, [new(id, rv, 12)]))).Items.Single();
        if (detailsFailure)
        {
            TmdbDetailsResult old = await _tmdb.GetDetailsFreshAsync(101, "tv", false);
            _tmdb.GetDetailsFreshAsync(101, "tv", true, Arg.Any<CancellationToken>()).Returns(old with { FromCache = true, RefreshError = "离线" });
        }
        else
        {
            TmdbSeasonCatalogueResult old = await _tmdb.GetSeasonCatalogueAsync(101, 2, false);
            _tmdb.GetSeasonCatalogueAsync(101, 2, true, Arg.Any<CancellationToken>()).Returns(old with { FromCache = true, RefreshError = "限流" });
        }
        Func<Task> act = () => service.ConfirmAsync(id, new(101, "tv", category, "合成连续剧", 2020, 2, 2, rv,
            DecisionSource: "AbsoluteMapping", SourceEpisode: 12, MappingToken: entry.MappingToken));
        await act.Should().ThrowAsync<BusinessException>().WithMessage("*刷新失败*");
        ReadItem(id).Status.Should().Be(MediaItemStatus.AwaitingReview);
        await _archive.DidNotReceiveWithAnyArgs().ArchiveAsync(default!);
    }
    [Fact]
    public async Task BatchMappingConfirmation_AcceptsUiPascalCaseMediaType()
    {
        long id = SeedAssistItem(); long category = SeedCategory(); MockCompleteCatalogue();
        ReviewService service = AssistService(NumericRules()); long rv = ReadItem(id).RowVersion;
        ReviewEpisodeMappingEntry entry = (await service.PreviewEpisodeMappingAsync(new(101, "Tv", 2, [new(id, rv, 12)]))).Items.Single();
        entry.Error.Should().BeNull();
        _archive.ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>()).Returns(new ArchiveResult("/archive/S02E02.mp4", ArchiveOutcome.Completed));
        BatchConfirmResult result = await service.BatchConfirmAsync(new([new(id, 101, "Tv", category, "合成连续剧", 2020, 2, 2, rv,
            DecisionSource: "AbsoluteMapping", SourceEpisode: 12, MappingToken: entry.MappingToken)]));
        result.Failed.Should().BeEmpty(); result.Succeeded.Should().Contain(id);
        ParsedInfo.FromJson(ReadItem(id).ParsedInfo)!.Episode.Should().Be(2);
    }

    [Fact]
    public async Task BatchConfirm_ConflictPendingAndFailure_AreNeverReportedAsArchived()
    {
        long pending = SeedAssistItem(); long failing = SeedAssistItem(); long category = SeedCategory(); MockCompleteCatalogue();
        _archive.ArchiveAsync(Arg.Is<MediaItem>(m => m.Id == pending), Arg.Any<CancellationToken>())
            .Returns(new ArchiveResult("/archive/S02E02.mp4", ArchiveOutcome.ConflictPending));
        _archive.ArchiveAsync(Arg.Is<MediaItem>(m => m.Id == failing), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ArchiveResult>(new IOException("测试归档失败")));
        BatchConfirmResult result = await AssistService().BatchConfirmAsync(new([
            new(pending, 101, "Tv", category, "合成连续剧", 2020, 2, 2, ReadItem(pending).RowVersion),
            new(failing, 101, "Tv", category, "合成连续剧", 2020, 2, 3, ReadItem(failing).RowVersion)]));
        result.Succeeded.Should().BeEmpty(); result.Failed.Should().HaveCount(2);
        result.Failed.Should().Contain(f => f.Id == pending && f.Message.Contains("仍待确认"));
        ReadItem(pending).Status.Should().Be(MediaItemStatus.AwaitingReview);
        ReadItem(failing).Status.Should().Be(MediaItemStatus.Failed);
    }

}
