using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Infrastructure.Persistence.Services.Parse;
using Xunit.Abstractions;
using Setup = PersonalMediaManager.Infrastructure.Persistence.Services.Setup;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>《食戟之灵》5 季同时下载场景的自动扫描解析模拟（记录当前行为）</summary>
/// <remarks>
/// 复刻自动扫描的逐文件解析：每个分集 mkv 以「监控根 + 季文件夹 + 分集文件名」完整路径链
/// 喂入真实 RuleEngineService（含 DataSeeder 的 23 条生产种子规则 + 内置兜底正则），
/// 观察规则引擎对 DBD-Raws「全方括号 + 花式中文副标题季（貳/餐/神/豪 之皿）」命名的识别结果。
///
/// 实测结论（本测试固化为回归基线）：规则引擎对这 5 季——
///   · 标题：种子规则「方括号包裹剧集」(Id=4) 正确抽出「食戟之灵 [副标题]」；
///   · 集号：[NN] 方括号集号正确抽出；类型判定 tv；
///   · 季号：**全部为 null**。「貳」非内置中文数字字符集（一二三四五六七八九十两），
///     「餐/神/豪」更是纯文字游戏非数字，且「X之皿」不命中「第N季 / 罗马数字 / XXX篇」任一季号规则，
///     故规则引擎无法判定季号 → 置信度 0.75（tv 仅缺季 0.70 + bonus 0.05，≥0.6）→ 线上走 TMDB 直查，
///     多季剧（食戟之灵 5 季）由「单季自动补季」守护判定季数 &gt;1 → 转人工审核选季（不再为季号动用 AI）。
/// 即：单靠规则引擎，5 季都拿不到季号；季号最终对错取决于人工审核 / pmm.txt 强制匹配。
/// 若未来扩展季号识别（如识别異體数字「貳」），本测试的 Season 断言需相应翻转。
/// </remarks>
public sealed class ShokugekiAutoScanSimulationTests : IDisposable
{
    private const string WatchRoot = @"F:\Downloads";
    // TMDB《食戟之灵》剧集 id（已核实：5 季集数 24/13/24/12/13 与 5 个文件夹完美 1:1，无需剧集组）
    private const int TmdbId = 62273;

    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly RuleEngineService _sut;
    private readonly ITestOutputHelper _output;

    public ShokugekiAutoScanSimulationTests(ITestOutputHelper output)
    {
        _output = output;
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbFactory = new TestDbContextFactory(_connection);
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ctx.Database.EnsureCreated();
        _sut = new RuleEngineService(_dbFactory, NullLogger<RuleEngineService>.Instance);
    }

    public void Dispose() => _connection.Dispose();

    /// <summary>5 季各取首集 + 末集，喂真实解析器，打印结果表并固化「标题/集号 OK、季号缺失」当前行为</summary>
    [Fact]
    public async Task FiveSeasons_AutoScan_ParseSimulation()
    {
        // 先播种生产环境的 23 条种子规则（用户规则先于内置规则尝试），完全复刻线上解析输入
        await SeedProductionRulesAsync();

        Season[] seasons = Seasons();

        _output.WriteLine("=== 《食戟之灵》5 季自动扫描解析模拟（规则引擎单独产出，未含 AI 兜底 / TMDB）===");
        _output.WriteLine($"监控根: {WatchRoot}");
        _output.WriteLine("");

        foreach (Season s in seasons)
        {
            _output.WriteLine($"── {s.Label}  真实季号=S{s.RealSeason}");
            _output.WriteLine($"   文件夹: {s.Folder}");
            foreach (Ep ep in s.Files)
            {
                FileParseContext ctx = FileParseContext.FromFullPath(
                    Path.Combine(WatchRoot, s.Folder, ep.File), WatchRoot);
                RuleParseResult r = await _sut.ParseAsync(ctx);

                _output.WriteLine($"   文件: {ep.File}");
                _output.WriteLine($"     → Title='{r.Title}'");
                _output.WriteLine(
                    $"       Season={Fmt(r.Season)}  Episode={Fmt(r.Episode)}  EpisodeEnd={Fmt(r.EpisodeEnd)}  Year={Fmt(r.Year)}");
                _output.WriteLine(
                    $"       MediaType={r.MediaType}  Confidence={r.Confidence:0.00}  SpecialChars={r.HasSpecialChars}  SeasonTitle={r.SeasonTitle ?? "<null>"}  MatchedRuleId={Fmt(r.MatchedRuleId)}");

                // 当前行为基线：标题抽取干净（含「食戟之灵」）、集号正确、判定为 tv、置信度 0.75（仅缺季）
                r.Title.Should().Contain("食戟之灵", $"标题应抽出作品名：{ep.File}");
                r.Episode.Should().Be(ep.Episode, $"集号应来自 [NN]：{ep.File}");
                r.MediaType.Should().Be("tv", $"有集号应判 tv：{ep.File}");
                r.Confidence.Should().BeApproximately(0.75, 0.001, $"tv 仅缺季 0.70 + 种子 bonus 0.05：{ep.File}");

                // 缺口基线：规则引擎无法从「貳/餐/神/豪 之皿」副标题判定季号 → Season 为 null。
                // 季号最终由下游 AI 兜底 / 人工审核 / pmm.txt 强制匹配决定（见类注释）。
                r.Season.Should().BeNull(
                    $"规则引擎对花式中文副标题季识别不出季号（已知缺口，下游兜底）：{ep.File}");
            }
            _output.WriteLine("");
        }
    }

    /// <summary>带 pmm.txt 强制匹配：5 季各自的 TMDB 季 URL → 正确锚定 (id=62273, tv, S1..S5)，叠加规则集号还原完整 SxxEyy</summary>
    /// <remarks>
    /// 与上一个测试（裸规则引擎季号全 null）形成对照：每季文件夹放 pmm.txt 贴 themoviedb.org/tv/62273/season/{n} 后，
    /// ForcedMatchMarkerParser 解析出 (TmdbId=62273, MediaType=tv, Season=n)；线上 ProcessFileService 合成
    /// season = forced.Season ?? rule.Season、episode = rule.Episode（强制匹配只覆盖身份+季，集号仍走规则）。
    /// 本测试复刻该合成，证明 5 季都能拿到正确的 (season, episode) 且同一 tmdbId。URL 与写入磁盘的 pmm.txt 逐字一致。
    /// </remarks>
    [Fact]
    public async Task FiveSeasons_WithPmmTxt_ForcedMatch_ResolvesCorrectSeason()
    {
        await SeedProductionRulesAsync();

        _output.WriteLine($"=== 带 pmm.txt 强制匹配的季号还原（《食戟之灵》tmdb={TmdbId}）===");
        foreach (Season s in Seasons())
        {
            // 该季文件夹内 pmm.txt 的内容（与写入下载目录的完全一致）
            string pmmUrl = $"https://www.themoviedb.org/tv/{TmdbId}/season/{s.RealSeason}";
            ForcedMatchMarker? marker = ForcedMatchMarkerParser.Parse(pmmUrl);

            marker.Should().NotBeNull($"pmm.txt URL 应解析出有效标识：{pmmUrl}");
            marker!.TmdbId.Should().Be(TmdbId, $"应锚定《食戟之灵》：{s.Label}");
            marker.MediaType.Should().Be("tv", $"季 URL 自带 /tv/ 类型：{s.Label}");
            marker.Season.Should().Be(s.RealSeason, $"季号应来自 /season/N：{s.Label}");

            foreach (Ep ep in s.Files)
            {
                FileParseContext ctx = FileParseContext.FromFullPath(
                    Path.Combine(WatchRoot, s.Folder, ep.File), WatchRoot);
                RuleParseResult rule = await _sut.ParseAsync(ctx);

                // 线上合成：强制匹配覆盖季号，集号仍取规则解析值
                int? finalSeason = marker.Season ?? rule.Season;
                int? finalEpisode = rule.Episode;

                _output.WriteLine(
                    $"   {s.Label} / {ep.File} → S{Fmt(finalSeason)}E{Fmt(finalEpisode)} (tmdb={marker.TmdbId})");

                finalSeason.Should().Be(s.RealSeason, $"强制匹配后季号应正确：{ep.File}");
                finalEpisode.Should().Be(ep.Episode, $"集号应来自文件名 [NN]：{ep.File}");
            }
        }
    }

    // ---------- helpers ----------

    /// <summary>5 季：文件夹名（与截图逐字一致） + 代表性分集 (文件名, 期望集号) + 真实季号</summary>
    private static Season[] Seasons() => new Season[]
    {
        new("第1季 食戟之灵", 1,
            @"[DBD-Raws][食戟之灵][01-24TV全集][1080P][BDRip][HEVC-10bit][简繁外挂][FLACx2][MKV]",
            new Ep[]
            {
                new(@"[DBD-Raws][食戟之灵][01][1080P][BDRip][HEVC-10bit][FLACx2].mkv", 1),
                new(@"[DBD-Raws][食戟之灵][24][1080P][BDRip][HEVC-10bit][FLACx2].mkv", 24),
            }),
        new("第2季 貳之皿", 2,
            @"[DBD-Raws][食戟之灵 貳之皿][01-13TV全集][1080P][BDRip][HEVC-10bit][简繁外挂][FLACx2][MKV]",
            new Ep[]
            {
                new(@"[DBD-Raws][食戟之灵 貳之皿][01][1080P][BDRip][HEVC-10bit][FLACx2].mkv", 1),
                new(@"[DBD-Raws][食戟之灵 貳之皿][13][1080P][BDRip][HEVC-10bit][FLACx2].mkv", 13),
            }),
        new("第3季 餐之皿", 3,
            @"[DBD-Raws][食戟之灵 餐之皿][01-24TV全集][1080P][BDRip][HEVC-10bit][简繁外挂][FLAC][MKV]",
            new Ep[]
            {
                new(@"[DBD-Raws][食戟之灵 餐之皿][01][1080P][BDRip][HEVC-10bit][FLAC].mkv", 1),
                new(@"[DBD-Raws][食戟之灵 餐之皿][24][1080P][BDRip][HEVC-10bit][FLAC].mkv", 24),
            }),
        new("第4季 神之皿", 4,
            @"[DBD-Raws][食戟之灵 神之皿][01-12TV全集][1080P][BDRip][HEVC-10bit][简繁外挂][FLAC][MKV]",
            new Ep[]
            {
                new(@"[DBD-Raws][食戟之灵 神之皿][01][1080P][BDRip][HEVC-10bit][FLAC].mkv", 1),
                new(@"[DBD-Raws][食戟之灵 神之皿][12][1080P][BDRip][HEVC-10bit][FLAC].mkv", 12),
            }),
        new("第5季 豪之皿", 5,
            @"[DBD-Raws][食戟之灵 豪之皿][01-13TV全集][1080P][BDRip][HEVC-10bit][简繁外挂][FLAC][MKV]",
            new Ep[]
            {
                new(@"[DBD-Raws][食戟之灵 豪之皿][01][1080P][BDRip][HEVC-10bit][FLAC].mkv", 1),
                new(@"[DBD-Raws][食戟之灵 豪之皿][13][1080P][BDRip][HEVC-10bit][FLAC].mkv", 13),
            }),
    };

    private async Task SeedProductionRulesAsync()
    {
        Setup.DataSeeder seeder = new(_dbFactory, NullLogger<Setup.DataSeeder>.Instance);
        await seeder.SeedAsync();
    }

    private static string Fmt(int? v) => v?.ToString() ?? "<null>";
    private static string Fmt(long? v) => v?.ToString() ?? "<null>";

    private sealed record Season(string Label, int RealSeason, string Folder, Ep[] Files);

    private sealed record Ep(string File, int Episode);

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
