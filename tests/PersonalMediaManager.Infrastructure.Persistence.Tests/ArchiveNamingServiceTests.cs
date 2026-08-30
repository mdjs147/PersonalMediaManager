using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Common.Archiving;
using PersonalMediaManager.Application.Dtos.Settings;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Infrastructure.Persistence.Services.Archive;
using PersonalMediaManager.Infrastructure.Persistence.Services.Settings;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>归档命名模板：Reader 降级容错 + Service 读写校验 + 预览==实际落点</summary>
/// <remarks>
/// Reader：缺失 / 空 / 非法逐项回退 Default 不抛（绝不因模板配错阻断归档）。
/// Service：合法模板渲染样例与 ArchiveFolderResolver.BuildRelativeTargetPath 同入参一致；非法模板写入抛 BusinessException、预览给错误清单。
/// </remarks>
public sealed class ArchiveNamingServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly ArchiveNamingService _sut;

    public ArchiveNamingServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbFactory = new TestDbContextFactory(_connection);
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ctx.Database.EnsureCreated(); // HasData 落 4 个 Archive_* 模板种子（默认值）
        _sut = new ArchiveNamingService(_dbFactory);
    }

    public void Dispose() => _connection.Dispose();

    // ───────────────────── Reader 降级容错 ─────────────────────

    [Fact(DisplayName = "Reader：种子默认值读出 == NamingTemplateOptions.Default")]
    public async Task Reader_SeededDefaults_EqualDefault()
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        NamingTemplateOptions opts = await ArchiveNamingTemplateReader.ReadAsync(db, NullLogger.Instance, default);

        opts.MovieNameTemplate.Should().Be(NamingTemplateOptions.Default.MovieNameTemplate);
        opts.TvShowFolderTemplate.Should().Be(NamingTemplateOptions.Default.TvShowFolderTemplate);
        opts.TvEpisodeNameTemplate.Should().Be(NamingTemplateOptions.Default.TvEpisodeNameTemplate);
        opts.SeasonFolderIncludeTitle.Should().BeTrue();
    }

    [Fact(DisplayName = "Reader：key 缺失（删行）→ 回退默认，不抛")]
    public async Task Reader_MissingKeys_FallBackToDefault()
    {
        using (PmmDbContext db = _dbFactory.CreateDbContext())
        {
            db.SystemSettings.RemoveRange(db.SystemSettings.Where(s => s.Key.StartsWith("Archive_")));
            await db.SaveChangesAsync();
        }

        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        NamingTemplateOptions opts = await ArchiveNamingTemplateReader.ReadAsync(ctx, NullLogger.Instance, default);
        opts.Should().Be(NamingTemplateOptions.Default);
    }

    [Fact(DisplayName = "Reader：非法模板（含 {tmdb / 缺 {episode}）→ 该项回退默认，不抛")]
    public async Task Reader_IllegalTemplates_FallBackPerItem()
    {
        await SetTemplate(ArchiveNamingTemplateReader.MovieNameTemplateKey, "{title} ({year}) {tmdb-1}"); // 含 {tmdb 字面量
        await SetTemplate(ArchiveNamingTemplateReader.TvEpisodeNameTemplateKey, "{title} ({year})");      // 缺 {episode}

        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        NamingTemplateOptions opts = await ArchiveNamingTemplateReader.ReadAsync(ctx, NullLogger.Instance, default);

        opts.MovieNameTemplate.Should().Be(NamingTemplateOptions.Default.MovieNameTemplate, "非法→回退默认");
        opts.TvEpisodeNameTemplate.Should().Be(NamingTemplateOptions.Default.TvEpisodeNameTemplate, "缺 {episode}→回退默认");
    }

    [Fact(DisplayName = "Reader：空字符串模板 → 回退默认")]
    public async Task Reader_EmptyTemplate_FallBack()
    {
        await SetTemplate(ArchiveNamingTemplateReader.MovieNameTemplateKey, "   ");
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        NamingTemplateOptions opts = await ArchiveNamingTemplateReader.ReadAsync(ctx, NullLogger.Instance, default);
        opts.MovieNameTemplate.Should().Be(NamingTemplateOptions.Default.MovieNameTemplate);
    }

    // ───────────────────── Service 读写 ─────────────────────

    [Fact(DisplayName = "Service：合法模板写入后读出一致（季标题开关 false 落 'false'）")]
    public async Task Update_Then_Get_RoundTrip()
    {
        await _sut.UpdateAsync(new UpdateArchiveNamingRequest(
            "{title} ({year})",
            "{originaltitle} ({year})",
            "{title} - {episode}",
            SeasonFolderIncludeTitle: false));

        ArchiveNamingResponse got = await _sut.GetAsync();
        got.MovieNameTemplate.Should().Be("{title} ({year})");
        got.TvShowFolderTemplate.Should().Be("{originaltitle} ({year})");
        got.TvEpisodeNameTemplate.Should().Be("{title} - {episode}");
        got.SeasonFolderIncludeTitle.Should().BeFalse();
    }

    [Fact(DisplayName = "Service：单集模板缺 {episode} → 写入抛 BusinessException")]
    public async Task Update_EpisodeTemplateMissingEpisodeToken_Throws()
    {
        Func<Task> act = () => _sut.UpdateAsync(new UpdateArchiveNamingRequest(
            "{title} ({year})", "{title} ({year})", "{title} ({year})", true));
        await act.Should().ThrowAsync<BusinessException>().Where(e => e.Message.Contains("{episode}"));
    }

    [Fact(DisplayName = "Service：模板含路径分隔符 → 写入抛 BusinessException")]
    public async Task Update_TemplateWithPathSeparator_Throws()
    {
        Func<Task> act = () => _sut.UpdateAsync(new UpdateArchiveNamingRequest(
            "{title}/{year}", "{title} ({year})", "{title} ({year}) - {episode}", true));
        await act.Should().ThrowAsync<BusinessException>();
    }

    // ───────────────────── 预览 == 实际落点 ─────────────────────

    [Fact(DisplayName = "预览：默认模板样例与 BuildRelativeTargetPath 同入参逐字节一致（电影 + 剧集单集 + 双集）")]
    public void Preview_DefaultTemplates_MatchRealResolver()
    {
        ArchiveNamingPreviewResponse r = _sut.Preview(new ArchiveNamingPreviewRequest(
            NamingTemplateOptions.DefaultMovieNameTemplate,
            NamingTemplateOptions.DefaultTvShowFolderTemplate,
            NamingTemplateOptions.DefaultTvEpisodeNameTemplate,
            SeasonFolderIncludeTitle: true));

        r.MovieErrors.Should().BeEmpty();
        r.TvShowFolderErrors.Should().BeEmpty();
        r.TvEpisodeErrors.Should().BeEmpty();

        NamingTemplateOptions def = NamingTemplateOptions.Default;
        // 与服务内部样例完全相同的入参（电影：盗梦空间 2010 tmdb27205 导演剪辑版）
        string expectedMovie = ArchiveFolderResolver.BuildRelativeTargetPath(
            "盗梦空间", 2010, 27205, isTv: false, season: 0, episode: 0, episodeEnd: null, extension: "mkv",
            targetRootForReuse: null, logger: null, seasonYear: null, seasonTitle: null,
            template: def, originalTitle: "Inception", edition: "导演剪辑版");
        r.Samples.Single(s => s.Label == "电影（多版本）").RelativePath.Should().Be(expectedMovie);

        string expectedEp = ArchiveFolderResolver.BuildRelativeTargetPath(
            "鬼灭之刃", 2019, 85937, isTv: true, season: 3, episode: 1, episodeEnd: null, extension: "mkv",
            targetRootForReuse: null, logger: null, seasonYear: 2023, seasonTitle: "锻刀村篇",
            template: def, originalTitle: "Kimetsu no Yaiba");
        r.Samples.Single(s => s.Label == "剧集（单集）").RelativePath.Should().Be(expectedEp);

        // 双集样例必带 S01E08-E09 + Season 01 + {tmdb-NNN}（刮削必需结构锁定）
        ArchiveNamingPreviewSample dual = r.Samples.Single(s => s.Label == "剧集（双集合并）");
        dual.RelativePath.Should().Contain("S01E08-E09").And.Contain("Season 01").And.Contain("{tmdb-12345}");
    }

    [Fact(DisplayName = "预览：自定义 Emby 风单集模板（去 ' - '）渲染正确、{tmdb-NNN}/Season NN/SxxEyy 仍锁定")]
    public void Preview_EmbyStyleEpisode_LockedStructureIntact()
    {
        ArchiveNamingPreviewResponse r = _sut.Preview(new ArchiveNamingPreviewRequest(
            "{title} ({year})", "{title} ({year})", "{title} ({year}) {episode}", true));

        r.TvEpisodeErrors.Should().BeEmpty();
        ArchiveNamingPreviewSample ep = r.Samples.Single(s => s.Label == "剧集（单集）");
        // 季内单集文件名年份用该季首播年 2023；锻刀村篇季标题；S03E01 集号锁定
        ep.RelativePath.Should().Contain("鬼灭之刃 (2023) S03E01")
            .And.Contain("Season 03 锻刀村篇")
            .And.Contain("{tmdb-85937}");
    }

    [Fact(DisplayName = "预览：非法模板返回错误清单 + 相关样例给错误占位（不抛）")]
    public void Preview_IllegalTemplate_ReturnsErrorsNoThrow()
    {
        ArchiveNamingPreviewResponse r = _sut.Preview(new ArchiveNamingPreviewRequest(
            "{title} ({year}) {tmdb-1}", "{title} ({year})", "{title} ({year})", true));

        r.MovieErrors.Should().NotBeEmpty();
        r.TvEpisodeErrors.Should().Contain(e => e.Contains("{episode}"));
        r.Samples.Single(s => s.Label == "电影（多版本）").Error.Should().NotBeNullOrEmpty();
    }

    [Fact(DisplayName = "预览：季标题开关关闭 → 季目录恒纯 Season NN（无篇章后缀）")]
    public void Preview_SeasonTitleOff_PlainSeasonFolder()
    {
        ArchiveNamingPreviewResponse r = _sut.Preview(new ArchiveNamingPreviewRequest(
            NamingTemplateOptions.DefaultMovieNameTemplate,
            NamingTemplateOptions.DefaultTvShowFolderTemplate,
            NamingTemplateOptions.DefaultTvEpisodeNameTemplate,
            SeasonFolderIncludeTitle: false));

        ArchiveNamingPreviewSample ep = r.Samples.Single(s => s.Label == "剧集（单集）");
        ep.RelativePath.Should().Contain("Season 03").And.NotContain("锻刀村篇");
    }

    private async Task SetTemplate(string key, string value)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        SystemSetting row = db.SystemSettings.Single(s => s.Key == key);
        row.Value = value;
        await db.SaveChangesAsync();
    }

    private sealed class TestDbContextFactory : IDbContextFactory<PmmDbContext>
    {
        private readonly SqliteConnection _connection;
        public TestDbContextFactory(SqliteConnection c) { _connection = c; }
        public PmmDbContext CreateDbContext()
        {
            DbContextOptionsBuilder<PmmDbContext> opts = new();
            opts.UseSqlite(_connection);
            return new PmmDbContext(opts.Options);
        }
    }
}
