using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Classify;
using PersonalMediaManager.Application.Services.Tmdb;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Persistence.Services.Classify;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>D7.3 ClassifyService + CategoryRuleEvaluator — JSON 条件树 + 规则评估全分支</summary>
public sealed class ClassifyServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly ITmdbSearchService _tmdb;
    private readonly ClassifyService _sut;

    public ClassifyServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbFactory = new TestDbContextFactory(_connection);
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ctx.Database.EnsureCreated();

        _tmdb = Substitute.For<ITmdbSearchService>();
        _sut = new ClassifyService(_dbFactory, _tmdb, NullLogger<ClassifyService>.Instance);
    }

    public void Dispose() => _connection.Dispose();

    // =====================================================================
    // CategoryRuleEvaluator —— 纯函数测试
    // =====================================================================

    [Fact]
    public void Evaluator_Eq_Type_Matches_CaseInsensitive()
    {
        const string json = """{"field":"type","op":"eq","value":"tv"}""";
        CategoryRuleEvaluator.Evaluate(json, NewContext("TV")).Should().BeTrue();
        CategoryRuleEvaluator.Evaluate(json, NewContext("movie")).Should().BeFalse();
    }

    [Fact]
    public void Evaluator_All_Combines_Conditions_With_And()
    {
        const string json = """
        {"all":[
            {"field":"type","op":"eq","value":"tv"},
            {"field":"originCountry","op":"in","value":["CN","HK","TW"]}
        ]}
        """;
        CategoryRuleEvaluator.Evaluate(json, NewContext("tv", origins: ["CN"])).Should().BeTrue();
        CategoryRuleEvaluator.Evaluate(json, NewContext("tv", origins: ["US"])).Should().BeFalse();
        CategoryRuleEvaluator.Evaluate(json, NewContext("movie", origins: ["CN"])).Should().BeFalse();
    }

    [Fact]
    public void Evaluator_Any_Combines_Conditions_With_Or()
    {
        const string json = """
        {"any":[
            {"field":"originalLanguage","op":"eq","value":"ja"},
            {"field":"genres","op":"contains","value":"Animation"}
        ]}
        """;
        CategoryRuleEvaluator.Evaluate(json, NewContext("tv", language: "ja")).Should().BeTrue();
        CategoryRuleEvaluator.Evaluate(json, NewContext("tv", genres: ["Animation"])).Should().BeTrue();
        CategoryRuleEvaluator.Evaluate(json, NewContext("tv", language: "en", genres: ["Drama"])).Should().BeFalse();
    }

    [Fact]
    public void Evaluator_OriginCountry_In_Multiple_Countries_Matches_Any()
    {
        const string json = """{"field":"originCountry","op":"in","value":["US","GB"]}""";
        CategoryRuleEvaluator.Evaluate(json, NewContext("tv", origins: ["GB", "AU"])).Should().BeTrue();
        CategoryRuleEvaluator.Evaluate(json, NewContext("tv", origins: ["FR"])).Should().BeFalse();
    }

    [Fact]
    public void Evaluator_Genres_Contains()
    {
        const string json = """{"field":"genres","op":"contains","value":"Documentary"}""";
        CategoryRuleEvaluator.Evaluate(json, NewContext("movie", genres: ["Drama", "Documentary"])).Should().BeTrue();
        CategoryRuleEvaluator.Evaluate(json, NewContext("movie", genres: ["Drama"])).Should().BeFalse();
    }

    [Fact]
    public void Evaluator_NotIn_Negates()
    {
        const string json = """{"field":"originCountry","op":"notIn","value":["CN","HK","TW"]}""";
        CategoryRuleEvaluator.Evaluate(json, NewContext("tv", origins: ["US"])).Should().BeTrue();
        CategoryRuleEvaluator.Evaluate(json, NewContext("tv", origins: ["CN"])).Should().BeFalse();
    }

    [Fact]
    public void Evaluator_NotEq_Negates()
    {
        const string json = """{"field":"type","op":"notEq","value":"movie"}""";
        CategoryRuleEvaluator.Evaluate(json, NewContext("tv")).Should().BeTrue();
        CategoryRuleEvaluator.Evaluate(json, NewContext("movie")).Should().BeFalse();
    }

    [Fact]
    public void Evaluator_MalformedJson_ReturnsFalse_NoThrow()
    {
        CategoryRuleEvaluator.Evaluate("not json", NewContext("tv")).Should().BeFalse();
        CategoryRuleEvaluator.Evaluate("", NewContext("tv")).Should().BeFalse();
        CategoryRuleEvaluator.Evaluate("{}", NewContext("tv")).Should().BeFalse();
    }

    [Fact]
    public void Evaluator_UnknownField_Or_Op_ReturnsFalse()
    {
        CategoryRuleEvaluator.Evaluate(
            """{"field":"unknownField","op":"eq","value":"x"}""", NewContext("tv")).Should().BeFalse();
        CategoryRuleEvaluator.Evaluate(
            """{"field":"type","op":"weirdOp","value":"tv"}""", NewContext("tv")).Should().BeFalse();
    }

    [Fact]
    public void Evaluator_Empty_AllArray_ReturnsFalse()
    {
        CategoryRuleEvaluator.Evaluate("""{"all":[]}""", NewContext("tv")).Should().BeFalse();
        CategoryRuleEvaluator.Evaluate("""{"any":[]}""", NewContext("tv")).Should().BeFalse();
    }

    [Fact]
    public void Evaluator_OriginalLanguage_In_And_NotIn_Single()
    {
        // 标量字段（originalLanguage）的 in / notIn —— 收敛后前端对标量字段也提供 in/notIn，需与求值器对齐
        const string inJson = """{"field":"originalLanguage","op":"in","value":["zh","ja"]}""";
        CategoryRuleEvaluator.Evaluate(inJson, NewContext("tv", language: "ja")).Should().BeTrue();
        CategoryRuleEvaluator.Evaluate(inJson, NewContext("tv", language: "en")).Should().BeFalse();

        const string notInJson = """{"field":"originalLanguage","op":"notIn","value":["zh","ja"]}""";
        CategoryRuleEvaluator.Evaluate(notInJson, NewContext("tv", language: "en")).Should().BeTrue();
        CategoryRuleEvaluator.Evaluate(notInJson, NewContext("tv", language: "zh")).Should().BeFalse();
    }

    [Fact]
    public void Evaluator_RemovedUiOps_AreUnsupported_ReturnFalse()
    {
        // 收敛后前端不再提供以下操作符（求值器从未实现）；固化「不命中」契约，
        // 防止有人把它们加回 CategoryRuleBuilder.vue 造成「能保存但永不命中」的静默失效复发。
        // 含旧前端误用的 neq（正确名为 notEq）。
        foreach (string op in new[] { "neq", "startsWith", "endsWith", "gt", "gte", "lt", "lte" })
        {
            string json = $$"""{"field":"type","op":"{{op}}","value":"tv"}""";
            CategoryRuleEvaluator.Evaluate(json, NewContext("tv")).Should()
                .BeFalse($"求值器未实现 op '{op}'，前端不得提供");
        }
    }

    // =====================================================================
    // ClassifyService —— 数据库 + DI 集成
    // =====================================================================

    [Fact]
    public async Task NoMatchingRule_Returns_SendToReview()
    {
        SeedMeta(tmdbId: 1, mediaType: "tv", origins: ["FR"], language: "fr", genres: "[]");
        MediaItem item = NewMediaItem(tmdbId: 1, type: "tv");

        SeedCategoryWithRule(name: "中国剧", rules: """
            {"all":[{"field":"originCountry","op":"in","value":["CN"]}]}
            """);

        ClassifyResult r = await _sut.ClassifyAsync(item);
        r.Decision.Should().Be(ClassifyDecision.SendToReview);
        r.CategoryId.Should().BeNull();
    }

    [Fact]
    public async Task FirstMatching_Rule_Wins_By_Priority()
    {
        SeedMeta(tmdbId: 2, mediaType: "tv", origins: ["CN"], language: "zh", genres: "[]");
        MediaItem item = NewMediaItem(tmdbId: 2, type: "tv");

        long generalCat = SeedCategoryWithRule(name: "电视剧通用", priority: 100, rules: """
            {"field":"type","op":"eq","value":"tv"}
            """);
        long chineseCat = SeedCategoryWithRule(name: "国产剧", priority: 10, rules: """
            {"all":[
                {"field":"type","op":"eq","value":"tv"},
                {"field":"originCountry","op":"in","value":["CN","HK","TW"]}
            ]}
            """);

        ClassifyResult r = await _sut.ClassifyAsync(item);
        r.Decision.Should().Be(ClassifyDecision.Matched);
        r.CategoryId.Should().Be(chineseCat); // Priority=10 优先于 100
    }

    [Fact]
    public async Task NotIn_OriginCountry_Matches_Overseas_EndToEnd()
    {
        // 端到端验证收敛后的 notIn：非中港台出品 → 海外剧
        SeedMeta(tmdbId: 7, mediaType: "tv", origins: ["US"], language: "en", genres: "[]");
        MediaItem item = NewMediaItem(tmdbId: 7, type: "tv");
        long overseasCat = SeedCategoryWithRule(name: "海外剧", rules: """
            {"all":[
                {"field":"type","op":"eq","value":"tv"},
                {"field":"originCountry","op":"notIn","value":["CN","HK","TW"]}
            ]}
            """);

        ClassifyResult r = await _sut.ClassifyAsync(item);
        r.Decision.Should().Be(ClassifyDecision.Matched);
        r.CategoryId.Should().Be(overseasCat);
    }

    [Fact]
    public async Task Disabled_Rule_Is_Skipped()
    {
        SeedMeta(tmdbId: 3, mediaType: "tv", origins: ["CN"], language: "zh", genres: "[]");
        MediaItem item = NewMediaItem(tmdbId: 3, type: "tv");

        // 关掉国产剧规则；电视剧通用规则启用
        SeedCategoryWithRule(name: "国产剧", priority: 10, enabled: false, rules: """
            {"all":[{"field":"type","op":"eq","value":"tv"},{"field":"originCountry","op":"in","value":["CN"]}]}
            """);
        long generalCat = SeedCategoryWithRule(name: "电视剧通用", priority: 100, rules: """
            {"field":"type","op":"eq","value":"tv"}
            """);

        ClassifyResult r = await _sut.ClassifyAsync(item);
        r.CategoryId.Should().Be(generalCat);
    }

    [Fact]
    public async Task Missing_Tmdb_Cache_Triggers_GetDetails_Then_Reads()
    {
        // 不预热 cache；当 TmdbSearchService.GetDetailsAsync 被调时，模拟写一个 cache 行
        MediaItem item = NewMediaItem(tmdbId: 99, type: "tv");
        long matched = SeedCategoryWithRule(name: "动漫", rules: """
            {"all":[{"field":"originalLanguage","op":"eq","value":"ja"}]}
            """);

        _tmdb.GetDetailsAsync(99, "tv", Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                SeedMeta(99, "tv", origins: ["JP"], language: "ja", genres: "[]");
                return new TmdbDetailsResult(99, "tv", "Title", null, 2020, 1, null, ["JP"], "ja", null, null, "{}");
            });

        ClassifyResult r = await _sut.ClassifyAsync(item);

        await _tmdb.Received(1).GetDetailsAsync(99, "tv", Arg.Any<CancellationToken>());
        r.CategoryId.Should().Be(matched);
    }

    [Fact]
    public async Task Missing_TmdbId_Throws_BusinessException()
    {
        MediaItem item = NewMediaItem(tmdbId: null, type: null);
        Func<Task> act = async () => await _sut.ClassifyAsync(item);
        await act.Should().ThrowAsync<BusinessException>().WithMessage("*MediaItem 缺少 TmdbId*");
    }

    [Fact]
    public async Task Genres_Contains_Works_Against_Tmdb_GenresJson()
    {
        // 模拟 TMDB Genres JSON 真实结构 [{"id":16,"name":"Animation"},{"id":18,"name":"Drama"}]
        SeedMeta(tmdbId: 5, mediaType: "tv", origins: ["JP"], language: "ja",
            genres: """[{"id":16,"name":"Animation"},{"id":18,"name":"Drama"}]""");
        MediaItem item = NewMediaItem(tmdbId: 5, type: "tv");
        long animeCat = SeedCategoryWithRule(name: "动漫", rules: """
            {"all":[
                {"field":"originCountry","op":"in","value":["JP"]},
                {"field":"genres","op":"contains","value":"Animation"}
            ]}
            """);

        ClassifyResult r = await _sut.ClassifyAsync(item);
        r.CategoryId.Should().Be(animeCat);
    }

    [Fact]
    public void ParseGenres_TmdbShape_Returns_Names()
    {
        const string json = """[{"id":18,"name":"Drama"},{"id":80,"name":"Crime"}]""";
        ClassifyService.ParseGenres(json).Should().Equal("Drama", "Crime");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("[{\"missing\":\"name\"}]")]
    public void ParseGenres_Invalid_Or_Empty_Returns_Empty(string? json)
    {
        ClassifyService.ParseGenres(json).Should().BeEmpty();
    }

    // =====================================================================
    // helpers
    // =====================================================================

    private static ClassifyContext NewContext(string type, string? language = null,
        string[]? origins = null, string[]? genres = null)
        => new(type, language, origins ?? Array.Empty<string>(), genres ?? Array.Empty<string>());

    private static MediaItem NewMediaItem(int? tmdbId, string? type)
        => MediaItem.CreateFixture("/tmp/x.mkv", "x.mkv", 0, tmdbId: tmdbId, tmdbMediaType: type);

    private void SeedMeta(int tmdbId, string mediaType, string[] origins, string? language, string genres)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        // 防重复 seed（GetDetails 回调可能在已有 row 时被调）
        TmdbMetadataCache? existing = db.TmdbMetadataCaches
            .FirstOrDefault(c => c.TmdbId == tmdbId && c.MediaType == mediaType);
        if (existing is not null) return;

        TmdbMetadataCache m = new()
        {
            TmdbId = tmdbId,
            MediaType = mediaType,
            Title = $"T-{tmdbId}",
            OriginCountry = origins.ToList(),
            OriginalLanguage = language,
            Genres = genres,
            CachedAt = DateTimeOffset.UtcNow,
            RawJson = "{}",
        };
        db.TmdbMetadataCaches.Add(m);
        db.SaveChanges();
    }

    private long SeedCategoryWithRule(string name, string rules, int priority = 100, bool enabled = true)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        CategoryDefinition cat = new()
        {
            Name = name,
            MediaType = MediaType.Both,
            TargetRoot = $"/M/{name}",
            Priority = priority,
        };
        db.CategoryDefinitions.Add(cat);
        db.SaveChanges();

        CategoryMatchRule rule = new()
        {
            CategoryId = cat.Id,
            Name = $"{name}-rule",
            Conditions = rules,
            Priority = priority,
            Enabled = enabled,
        };
        db.CategoryMatchRules.Add(rule);
        db.SaveChanges();
        return cat.Id;
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
