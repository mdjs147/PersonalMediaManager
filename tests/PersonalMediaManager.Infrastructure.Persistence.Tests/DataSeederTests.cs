using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalMediaManager.Domain.Aggregates.ParseRules;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Persistence;
using PersonalMediaManager.Infrastructure.Persistence.Interceptors;
using PersonalMediaManager.Infrastructure.Persistence.Services.Setup;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>DataSeeder — 初始种子幂等补齐：分类 / 分类匹配规则 / 解析规则</summary>
public sealed class DataSeederTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly DataSeeder _sut;

    public DataSeederTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbFactory = new TestDbContextFactory(_connection);
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ctx.Database.EnsureCreated();

        _sut = new DataSeeder(_dbFactory, NullLogger<DataSeeder>.Instance);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task SeedAsync_FreshDatabase_Seeds3Categories3RulesAnd23ParseRules()
    {
        await _sut.SeedAsync();

        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        (await ctx.CategoryDefinitions.CountAsync()).Should().Be(3);
        (await ctx.CategoryMatchRules.CountAsync()).Should().Be(3);
        (await ctx.ParseRules.CountAsync()).Should().Be(23, "V1 8 条 + V2 7 条 + V3 8 条 = 23");
    }

    [Fact]
    public async Task SeedAsync_ParseRules_HaveExpectedNamesPriorityAndDefaultType()
    {
        await _sut.SeedAsync();

        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        List<Domain.Aggregates.ParseRules.ParseRule> rules = await ctx.ParseRules
            .AsNoTracking().OrderBy(r => r.Priority).ThenBy(r => r.Id).ToListAsync();

        // 顺序：先按 Priority 升序，同 priority 内按 Id 升序（按 DataSeeder 中 AddRange 顺序）。
        // V3 8 条夹在 V1+V2 的优先级缝隙里：26（部）/27（双方括号）/29（期）/49（Part-Cour）/52（Vol）/82（#/No）/83（第N集兜底）/85（YYMMDD）
        rules.Select(r => r.Name).Should().BeEquivalentTo(new[]
        {
            // V2 优先级 22
            "综艺第N季 + 日期作集",
            // V1 优先级 25
            "国产剧第N季第N集",
            // V3 优先级 26
            "中文「第N部 第N集」",
            // V3 优先级 27
            "方括号双段季集 [Sxx][Eyy]",
            // V2 优先级 28
            "综艺日期作集",
            // V3 优先级 29
            "综艺「第N期」",
            // V1 优先级 30
            "Plex Jellyfin 标准命名",
            // V2 优先级 32
            "AKA 多语言标题",
            // V1 优先级 35
            "括号年份带季集",
            // V1 优先级 45
            "方括号包裹剧集",
            // V2 优先级 48
            "Anime 英文季号 (Nth Season)",
            // V3 优先级 49
            "番剧分卷 Part / Cour + 集号",
            // V1 优先级 50
            "动漫字幕组单集",
            // V3 优先级 52
            "动漫 Vol / Volume 卷集号",
            // V1 优先级 55
            "OVA SP 特别篇",
            // V1 优先级 60
            "季集 NxNN 格式",
            // V2 优先级 65
            "全N集 整季合集",
            // V1 优先级 70
            "括号年份电影",
            // V2 优先级 75
            "Episode N 完整英文",
            // V2 优先级 80
            "中文章节集号「第N章 / 第N回」",
            // V3 优先级 82
            "绝对集号「#N / No.N」",
            // V3 优先级 83
            "无季号「第N集」中文兜底",
            // V3 优先级 85
            "综艺 YYMMDD 短日期作集",
        }, options => options.WithStrictOrdering());

        rules.Select(r => r.Priority).Should().Equal(
            22, 25, 26, 27, 28, 29, 30, 32, 35, 45, 48, 49, 50, 52, 55, 60, 65, 70, 75, 80, 82, 83, 85);
        // 仅电影规则 DefaultType 留空让 InferMediaType 兜底；其它二十二条均为 tv
        rules.Where(r => r.Name == "括号年份电影").Should().OnlyContain(r => r.DefaultType == null);
        rules.Where(r => r.Name != "括号年份电影").Should().OnlyContain(r => r.DefaultType == "tv");
    }

    [Fact]
    public async Task SeedAsync_SeedsExpectedCategoryNamesAndTypes()
    {
        await _sut.SeedAsync();

        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        Dictionary<string, CategoryDefinition> categories = await ctx.CategoryDefinitions.AsNoTracking()
            .ToDictionaryAsync(c => c.Name, c => c);

        categories.Keys.Should().BeEquivalentTo("电影", "电视剧", "动漫");
        categories["电影"].MediaType.Should().Be(MediaType.Movie);
        categories["电视剧"].MediaType.Should().Be(MediaType.Tv);
        categories["动漫"].MediaType.Should().Be(MediaType.Tv);
        categories.Values.Should().OnlyContain(c => c.TargetRoot == "<UNSET>",
            "默认分类目标根目录为占位符，待用户设置");
    }

    [Fact]
    public async Task SeedAsync_MatchRules_AllReferenceSeededCategories()
    {
        await _sut.SeedAsync();

        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        List<long> categoryIds = await ctx.CategoryDefinitions.Select(c => c.Id).ToListAsync();
        List<CategoryMatchRule> rules = await ctx.CategoryMatchRules.AsNoTracking().ToListAsync();

        rules.Should().OnlyContain(r => categoryIds.Contains(r.CategoryId));
        rules.Select(r => r.Priority).Should().BeEquivalentTo(new[] { 20, 50, 90 });
    }

    [Fact]
    public async Task SeedAsync_RunTwice_IsIdempotent()
    {
        await _sut.SeedAsync();
        await _sut.SeedAsync();

        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        (await ctx.CategoryDefinitions.CountAsync()).Should().Be(3);
        (await ctx.CategoryMatchRules.CountAsync()).Should().Be(3);
        (await ctx.ParseRules.CountAsync()).Should().Be(23);
    }

    [Fact]
    public async Task SeedAsync_CategoriesAlreadyExist_SkipsCategorySeedOnly()
    {
        using (PmmDbContext arrange = _dbFactory.CreateDbContext())
        {
            arrange.CategoryDefinitions.Add(new CategoryDefinition
            {
                Name = "用户自建分类",
                MediaType = MediaType.Movie,
                TargetRoot = "/data/movies",
            });
            await arrange.SaveChangesAsync();
        }

        await _sut.SeedAsync();

        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        // 分类表非空 → 整体跳过分类与匹配规则；解析规则表仍为空 → 照常补齐
        (await ctx.CategoryDefinitions.CountAsync()).Should().Be(1);
        (await ctx.CategoryMatchRules.CountAsync()).Should().Be(0);
        (await ctx.ParseRules.CountAsync()).Should().Be(23);
    }

    // ---------- 存量种子规则一次性修正（FixLegacyParseRulesAsync） ----------

    // 旧/新种子值在测试侧按字面值重复声明（不引用 DataSeeder 私有常量）：
    // 若生产侧常量被误改，这里立即红——旧种子值是「历史快照」，一个字符都不能动。
    private const string LegacyFullPackPattern = @"^(?<title>.+?)[\s\._\-]+全(?<episode>\d{1,4})集";
    private const string NewFullPackPattern = @"^(?<title>.+?)[\s\._\-]+全\d{1,4}集";
    private const string LegacyFullPackDescription = "整季合集「全N集」命名（如扫毒.全30集），识别为 tv；episode 抓总集数仅作展示";
    private const string NewFullPackDescription = "整季合集「全N集」命名（如扫毒.全30集），识别为 tv 并清洗标题；总集数不作集号，交 AI / 人工审核定集";
    private const string OvaSeedPattern = @"^(?:\[[^\]]{1,40}\]\s*)*(?<title>[^\[\]]+?)[\s\._\-]+(?:OVA|SP|NCED|NCOP|番外|特典|映画|剧场版)[\s\._\-]?(?<episode>\d{1,3})(?![\d])";
    private const string LegacyOvaDescription = "OVA / SP / 番外 / 特典 等动漫特别篇标记，集号 1-3 位";
    private const string NewOvaDescription = "OVA / SP / 番外 / 特典 等动漫特别篇标记，集号 1-3 位；季号留空交 AI 按特别篇约定归 Season 0";

    [Fact]
    public async Task SeedAsync_LegacyFullPackSeedRow_PatternAndDescriptionAutoFixed()
    {
        // 模拟存量库：旧版种子「全N集」行原封未动（Pattern 与 Description 均为旧种子原值）
        long id = SeedParseRule(new ParseRule
        {
            Name = "全N集 整季合集",
            Scope = ParseScope.FileName,
            Pattern = LegacyFullPackPattern,
            DefaultType = "tv",
            ForceType = true,
            Priority = 65,
            Description = LegacyFullPackDescription,
        });

        await _sut.SeedAsync();

        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ParseRule rule = await ctx.ParseRules.SingleAsync(r => r.Id == id);
        rule.Pattern.Should().Be(NewFullPackPattern, "旧种子 Pattern 把总集数捕获为 episode，应被一次性修正为不抓集号的新值");
        rule.Description.Should().Be(NewFullPackDescription, "Description 同为旧种子原文 → 一并刷新");
        // 解析规则表非空 → 种子写入整体跳过，不会混入 23 条默认规则
        (await ctx.ParseRules.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task SeedAsync_LegacyFullPackPattern_UserDescription_PatternFixedDescriptionKept()
    {
        // Pattern 仍是旧种子值（功能缺陷必须修），但 Description 已被用户改过 → 只修 Pattern，保留用户描述
        long id = SeedParseRule(new ParseRule
        {
            Name = "全N集 整季合集",
            Scope = ParseScope.FileName,
            Pattern = LegacyFullPackPattern,
            DefaultType = "tv",
            ForceType = true,
            Priority = 65,
            Description = "用户自己写的备注",
        });

        await _sut.SeedAsync();

        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ParseRule rule = await ctx.ParseRules.SingleAsync(r => r.Id == id);
        rule.Pattern.Should().Be(NewFullPackPattern);
        rule.Description.Should().Be("用户自己写的备注", "用户自定义描述不得被覆盖");
    }

    [Fact]
    public async Task SeedAsync_UserModifiedFullPackPattern_NotTouched()
    {
        // 用户改过 Pattern（与旧种子值哪怕只差一个字符）→ 整行绝不触碰
        string userPattern = @"^(?<title>.+?)全(?<episode>\d{1,4})集"; // 用户删掉了分隔符前导
        long id = SeedParseRule(new ParseRule
        {
            Name = "全N集 整季合集",
            Scope = ParseScope.FileName,
            Pattern = userPattern,
            DefaultType = "tv",
            ForceType = true,
            Priority = 65,
            Description = LegacyFullPackDescription,
        });

        await _sut.SeedAsync();

        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ParseRule rule = await ctx.ParseRules.SingleAsync(r => r.Id == id);
        rule.Pattern.Should().Be(userPattern, "用户改过的 Pattern 不得被修正逻辑覆盖");
        rule.Description.Should().Be(LegacyFullPackDescription, "Pattern 非旧种子值 → 整行跳过，描述也不动");
    }

    [Fact]
    public async Task SeedAsync_LegacyOvaDescription_AutoFixed()
    {
        // OVA 行：Pattern 未变（仍为种子值）、Description 为旧种子原文 → 仅刷新 Description
        long id = SeedParseRule(new ParseRule
        {
            Name = "OVA SP 特别篇",
            Scope = ParseScope.FileName,
            Pattern = OvaSeedPattern,
            DefaultType = "tv",
            Priority = 55,
            Description = LegacyOvaDescription,
        });

        await _sut.SeedAsync();

        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ParseRule rule = await ctx.ParseRules.SingleAsync(r => r.Id == id);
        rule.Pattern.Should().Be(OvaSeedPattern, "OVA 的 Pattern 本就未变");
        rule.Description.Should().Be(NewOvaDescription, "旧种子描述应补充特别篇 Season 0 约定说明");
    }

    [Fact]
    public async Task SeedAsync_OvaUserModifiedDescription_NotTouched()
    {
        long id = SeedParseRule(new ParseRule
        {
            Name = "OVA SP 特别篇",
            Scope = ParseScope.FileName,
            Pattern = OvaSeedPattern,
            DefaultType = "tv",
            Priority = 55,
            Description = "用户改过的 OVA 描述",
        });

        await _sut.SeedAsync();

        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ParseRule rule = await ctx.ParseRules.SingleAsync(r => r.Id == id);
        rule.Description.Should().Be("用户改过的 OVA 描述", "Description 非旧种子原文 → 不动");
    }

    [Fact]
    public async Task SeedAsync_FreshDatabase_SeedsNewValues_LegacyFixIsNoOp()
    {
        // 新库：种子写入的就是新值，修正逻辑空转，不产生旧值残留
        await _sut.SeedAsync();

        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ParseRule fullPack = await ctx.ParseRules.SingleAsync(r => r.Name == "全N集 整季合集");
        fullPack.Pattern.Should().Be(NewFullPackPattern);
        fullPack.Description.Should().Be(NewFullPackDescription);
        ParseRule ova = await ctx.ParseRules.SingleAsync(r => r.Name == "OVA SP 特别篇");
        ova.Description.Should().Be(NewOvaDescription);
        (await ctx.ParseRules.CountAsync(r => r.Pattern == LegacyFullPackPattern)).Should().Be(0);
    }

    [Fact]
    public async Task SeedAsync_LegacyFix_RunTwice_Idempotent()
    {
        // 第二次启动时旧值已不存在 → 修正空转：值稳定且不产生多余 UPDATE（RowVersion 不再递增）
        long id = SeedParseRule(new ParseRule
        {
            Name = "全N集 整季合集",
            Scope = ParseScope.FileName,
            Pattern = LegacyFullPackPattern,
            DefaultType = "tv",
            ForceType = true,
            Priority = 65,
            Description = LegacyFullPackDescription,
        });

        await _sut.SeedAsync();
        long rowVersionAfterFix;
        using (PmmDbContext mid = _dbFactory.CreateDbContext())
        {
            rowVersionAfterFix = (await mid.ParseRules.AsNoTracking().SingleAsync(r => r.Id == id)).RowVersion;
        }

        await _sut.SeedAsync();

        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ParseRule rule = await ctx.ParseRules.AsNoTracking().SingleAsync(r => r.Id == id);
        rule.Pattern.Should().Be(NewFullPackPattern);
        rule.RowVersion.Should().Be(rowVersionAfterFix, "第二次启动不应再产生 UPDATE");
    }

    private long SeedParseRule(ParseRule rule)
    {
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ctx.ParseRules.Add(rule);
        ctx.SaveChanges();
        return rule.Id;
    }

    private sealed class TestDbContextFactory : IDbContextFactory<PmmDbContext>
    {
        private readonly SqliteConnection _connection;
        public TestDbContextFactory(SqliteConnection c) { _connection = c; }
        public PmmDbContext CreateDbContext()
        {
            DbContextOptionsBuilder<PmmDbContext> opts = new();
            opts.UseSqlite(_connection);
            opts.AddInterceptors(
                new TimestampInterceptor(() => DateTimeOffset.UtcNow),
                new RowVersionInterceptor());
            return new PmmDbContext(opts.Options);
        }
    }
}
