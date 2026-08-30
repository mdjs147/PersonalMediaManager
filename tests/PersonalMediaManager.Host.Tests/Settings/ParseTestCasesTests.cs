using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Aggregates.WatchDirectories;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Host.Middleware;
using PersonalMediaManager.Infrastructure.Persistence;

namespace PersonalMediaManager.Host.Tests.Settings;

/// <summary>ParseTestCases e2e：CRUD 闭环 + 状态切换 + 乐观锁 + 从 Failed 灌入 + 回归运行 + AI Triage（假 advisor）</summary>
public sealed class ParseTestCasesTests : IDisposable
{
    private readonly PmmHostFactory _factory;
    private readonly HttpClient _client;

    public ParseTestCasesTests()
    {
        SetupGuardMiddleware.ResetCacheForTest();
        // 用假 IAiAdvisor 替换真实 AI 调用，避免集成测试打外部 AI；只有 Triage 测试会触发它
        _factory = new PmmHostFactory
        {
            ConfigureTestServices = s =>
            {
                s.RemoveAll<IAiAdvisor>();
                s.AddScoped<IAiAdvisor, FakeAiAdvisor>();
            },
        };
        _client = _factory.CreateClient();
    }

    /// <summary>固定返回的假顾问；验证 Triage / SuggestRule 落库链路而非真实 AI 行为</summary>
    private sealed class FakeAiAdvisor : IAiAdvisor
    {
        public Task<TriageAdvice> TriageAsync(TriageInput input, CancellationToken ct = default) =>
            Task.FromResult(new TriageAdvice(
                WorthAdding: true,
                Reason: "命名套路独特，值得纳入回归集",
                SimilarityToExisting: 0.15,
                SuggestedTitle: "盗梦空间",
                SuggestedYear: 2010,
                SuggestedMediaType: MediaType.Movie,
                SuggestedSeason: null,
                SuggestedEpisode: null));

        public Task<RuleSuggestion> SuggestRuleAsync(SuggestRuleInput input, CancellationToken ct = default) =>
            Task.FromResult(new RuleSuggestion(
                Pattern: @"^(?<title>.+?)\.(?<year>\d{4})\.",
                Scope: ParseScope.FileName,
                DefaultType: "movie",
                Explanation: "匹配 标题.年份. 前缀格式"));
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        SetupGuardMiddleware.ResetCacheForTest();
    }

    [Fact]
    public async Task Crud_RoundTrip()
    {
        await LoginAsAdminAsync();

        JsonElement created = await PostAsync("/api/settings/parse-testcases", new
        {
            samplePath = @"F:\迅雷下载\Inception.2010.1080p.mkv",
            watchRootPath = (string?)null,
            expectedTitle = "Inception",
            expectedYear = 2010,
            expectedMediaType = "Movie",
            expectedSeason = (int?)null,
            expectedEpisode = (int?)null,
            notes = "首条样本",
        });
        created.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        long id = created.GetProperty("data").GetProperty("id").GetInt64();
        long rowVersion = created.GetProperty("data").GetProperty("rowVersion").GetInt64();
        created.GetProperty("data").GetProperty("source").GetString().Should().Be("Manual");
        created.GetProperty("data").GetProperty("status").GetString().Should().Be("Active");
        created.GetProperty("data").GetProperty("expectedMediaType").GetString().Should().Be("Movie");

        JsonElement list = await GetAsync("/api/settings/parse-testcases");
        list.GetProperty("data").GetProperty("total").GetInt32().Should().Be(1);
        list.GetProperty("data").GetProperty("items").EnumerateArray()
            .Any(e => e.GetProperty("id").GetInt64() == id).Should().BeTrue();

        JsonElement detail = await GetAsync($"/api/settings/parse-testcases/{id}");
        detail.GetProperty("data").GetProperty("expectedTitle").GetString().Should().Be("Inception");

        JsonElement upd = await PostAsync("/api/settings/parse-testcases/update", new
        {
            id,
            samplePath = @"F:\迅雷下载\Inception.2010.4K.mkv",
            watchRootPath = @"F:\迅雷下载",
            expectedTitle = "Inception v2",
            expectedYear = 2010,
            expectedMediaType = "Movie",
            expectedSeason = (int?)null,
            expectedEpisode = (int?)null,
            notes = "改后",
            rowVersion,
        });
        upd.GetProperty("data").GetProperty("expectedTitle").GetString().Should().Be("Inception v2");
        upd.GetProperty("data").GetProperty("rowVersion").GetInt64().Should().BeGreaterThan(rowVersion);

        JsonElement del = await PostAsync("/api/settings/parse-testcases/delete", new { id });
        del.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
    }

    [Fact]
    public async Task Create_DuplicateSamplePath_Returns_1000()
    {
        await LoginAsAdminAsync();

        await PostAsync("/api/settings/parse-testcases", new
        {
            samplePath = @"F:\sample\dup.mkv",
        });
        JsonElement dup = await PostAsync("/api/settings/parse-testcases", new
        {
            samplePath = @"F:\sample\dup.mkv",
        });
        dup.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
    }

    [Fact]
    public async Task StaleRowVersion_Update_Returns_1000()
    {
        await LoginAsAdminAsync();

        JsonElement created = await PostAsync("/api/settings/parse-testcases", new
        {
            samplePath = @"F:\sample\v.mkv",
        });
        long id = created.GetProperty("data").GetProperty("id").GetInt64();
        long rowVersion = created.GetProperty("data").GetProperty("rowVersion").GetInt64();

        // 第一次 update 把 RowVersion 推进
        await PostAsync("/api/settings/parse-testcases/update", new
        {
            id,
            samplePath = @"F:\sample\v.mkv",
            watchRootPath = (string?)null,
            expectedTitle = "改一次",
            expectedYear = (int?)null,
            expectedMediaType = (string?)null,
            expectedSeason = (int?)null,
            expectedEpisode = (int?)null,
            notes = (string?)null,
            rowVersion,
        });

        // 仍用旧 RowVersion 提交 → 1000
        JsonElement stale = await PostAsync("/api/settings/parse-testcases/update", new
        {
            id,
            samplePath = @"F:\sample\v.mkv",
            watchRootPath = (string?)null,
            expectedTitle = "改两次",
            expectedYear = (int?)null,
            expectedMediaType = (string?)null,
            expectedSeason = (int?)null,
            expectedEpisode = (int?)null,
            notes = (string?)null,
            rowVersion,
        });
        stale.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
    }

    [Fact]
    public async Task StatusTransitions_Promote_Disable_Reset()
    {
        await LoginAsAdminAsync();

        // Manual 新增默认 Active；Disable → PendingTriage 非法（必须从 Disabled / Active 走 reset 才能回 PendingTriage）
        JsonElement created = await PostAsync("/api/settings/parse-testcases", new
        {
            samplePath = @"F:\sample\t.mkv",
        });
        long id = created.GetProperty("data").GetProperty("id").GetInt64();
        long rv = created.GetProperty("data").GetProperty("rowVersion").GetInt64();

        // Active → Disabled
        JsonElement disabled = await PostAsync("/api/settings/parse-testcases/disable", new { id, rowVersion = rv });
        disabled.GetProperty("data").GetProperty("status").GetString().Should().Be("Disabled");
        rv = disabled.GetProperty("data").GetProperty("rowVersion").GetInt64();

        // Disabled → Active（Promote）
        JsonElement promoted = await PostAsync("/api/settings/parse-testcases/promote", new { id, rowVersion = rv });
        promoted.GetProperty("data").GetProperty("status").GetString().Should().Be("Active");
        rv = promoted.GetProperty("data").GetProperty("rowVersion").GetInt64();

        // Active → PendingTriage（Reset）
        JsonElement triage = await PostAsync("/api/settings/parse-testcases/reset", new { id, rowVersion = rv });
        triage.GetProperty("data").GetProperty("status").GetString().Should().Be("PendingTriage");
        rv = triage.GetProperty("data").GetProperty("rowVersion").GetInt64();

        // 同状态再切换 → 1000（已处于该状态）
        JsonElement same = await PostAsync("/api/settings/parse-testcases/reset", new { id, rowVersion = rv });
        same.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
    }

    [Fact]
    public async Task ImportFromFailed_Imports_PendingTriage_With_WatchRoot()
    {
        await LoginAsAdminAsync();

        // 直接往库塞一个 Failed Media_Item 与一个 WatchFolder（监控根反查的依据）
        IDbContextFactory<PmmDbContext> factory = _factory.Services.GetRequiredService<IDbContextFactory<PmmDbContext>>();
        await using (PmmDbContext ctx = await factory.CreateDbContextAsync())
        {
            ctx.WatchFolders.Add(new WatchFolder
            {
                Alias = "下载",
                Path = @"F:\迅雷下载",
                Enabled = true,
            });
            MediaItem mi = MediaItem.CreateFixture(
                sourcePath: @"F:\迅雷下载\奇怪命名\episode01.mkv",
                fileName: "episode01.mkv",
                fileSize: 100,
                status: MediaItemStatus.Failed,
                errorMessage: "解析超时");
            ctx.MediaItems.Add(mi);
            await ctx.SaveChangesAsync();
        }

        JsonElement res = await PostAsync("/api/settings/parse-testcases/import-from-failed", new
        {
            limit = 10,
            sinceUtc = (DateTimeOffset?)null,
        });
        res.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        res.GetProperty("data").GetProperty("imported").GetInt32().Should().Be(1);
        res.GetProperty("data").GetProperty("skipped").GetInt32().Should().Be(0);

        // 第二次再灌：同 SamplePath 已存在 → skipped=1, imported=0
        JsonElement again = await PostAsync("/api/settings/parse-testcases/import-from-failed", new
        {
            limit = 10,
            sinceUtc = (DateTimeOffset?)null,
        });
        again.GetProperty("data").GetProperty("imported").GetInt32().Should().Be(0);
        again.GetProperty("data").GetProperty("skipped").GetInt32().Should().Be(1);

        // 列表过滤 PendingTriage 应能查到该条；WatchRootPath 反查命中 F:\迅雷下载
        JsonElement list = await GetAsync("/api/settings/parse-testcases?status=PendingTriage");
        list.GetProperty("data").GetProperty("total").GetInt32().Should().Be(1);
        JsonElement item = list.GetProperty("data").GetProperty("items")[0];
        item.GetProperty("source").GetString().Should().Be("FromFailed");
        item.GetProperty("watchRootPath").GetString().Should().Be(@"F:\迅雷下载");
        item.GetProperty("notes").GetString().Should().Contain("解析超时");
    }

    [Fact]
    public async Task Run_WithoutExpected_Status_NotRun_But_HasResult()
    {
        await LoginAsAdminAsync();

        JsonElement created = await PostAsync("/api/settings/parse-testcases", new
        {
            samplePath = "Inception.2010.1080p.BluRay.x264.mkv",
        });
        long id = created.GetProperty("data").GetProperty("id").GetInt64();

        JsonElement ran = await PostAsync("/api/settings/parse-testcases/run", new { id });
        ran.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        // 未设任何 Expected → 无可比基线 → NotRun
        ran.GetProperty("data").GetProperty("lastRunStatus").GetString().Should().Be("NotRun");
        // 但实测结果已写入（title 至少有 stem 兜底，never null）
        ran.GetProperty("data").GetProperty("lastRunResult").GetString().Should().NotBeNullOrWhiteSpace();
        ran.GetProperty("data").GetProperty("lastRunAt").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Approve_Then_Run_Yields_Pass()
    {
        await LoginAsAdminAsync();

        JsonElement created = await PostAsync("/api/settings/parse-testcases", new
        {
            samplePath = "Inception.2010.1080p.BluRay.x264.mkv",
        });
        long id = created.GetProperty("data").GetProperty("id").GetInt64();

        JsonElement ran = await PostAsync("/api/settings/parse-testcases/run", new { id });
        long rv = ran.GetProperty("data").GetProperty("rowVersion").GetInt64();

        // 批准实测为基线
        JsonElement approved = await PostAsync("/api/settings/parse-testcases/approve", new { id, rowVersion = rv });
        approved.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        approved.GetProperty("data").GetProperty("lastRunStatus").GetString().Should().Be("Pass");
        approved.GetProperty("data").GetProperty("expectedTitle").GetString().Should().NotBeNullOrWhiteSpace();

        // 再跑一次：期望 == 实测 → 必 Pass
        JsonElement rerun = await PostAsync("/api/settings/parse-testcases/run", new { id });
        rerun.GetProperty("data").GetProperty("lastRunStatus").GetString().Should().Be("Pass");
    }

    [Fact]
    public async Task Run_WrongExpected_Yields_Fail()
    {
        await LoginAsAdminAsync();

        JsonElement created = await PostAsync("/api/settings/parse-testcases", new
        {
            samplePath = "Inception.2010.1080p.BluRay.x264.mkv",
            expectedTitle = "绝对不可能匹配的标题ZZZ",
        });
        long id = created.GetProperty("data").GetProperty("id").GetInt64();

        JsonElement ran = await PostAsync("/api/settings/parse-testcases/run", new { id });
        ran.GetProperty("data").GetProperty("lastRunStatus").GetString().Should().Be("Fail");
    }

    [Fact]
    public async Task Approve_WithoutRun_Returns_1000()
    {
        await LoginAsAdminAsync();

        JsonElement created = await PostAsync("/api/settings/parse-testcases", new
        {
            samplePath = "NeverRun.2020.mkv",
        });
        long id = created.GetProperty("data").GetProperty("id").GetInt64();
        long rv = created.GetProperty("data").GetProperty("rowVersion").GetInt64();

        JsonElement approve = await PostAsync("/api/settings/parse-testcases/approve", new { id, rowVersion = rv });
        approve.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
    }

    [Fact]
    public async Task RunBatch_Active_Runs_All()
    {
        await LoginAsAdminAsync();

        await PostAsync("/api/settings/parse-testcases", new { samplePath = "A.2001.mkv" });
        await PostAsync("/api/settings/parse-testcases", new { samplePath = "B.S01E01.mkv" });

        JsonElement batch = await PostAsync("/api/settings/parse-testcases/run-all", new
        {
            status = "Active",
            limit = 500,
        });
        batch.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        batch.GetProperty("data").GetProperty("ran").GetInt32().Should().Be(2);
        // 两条都未设 Expected → 都计入 notComparable
        batch.GetProperty("data").GetProperty("notComparable").GetInt32().Should().Be(2);
        batch.GetProperty("data").GetProperty("failures").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Triage_Writes_AiVerdict()
    {
        await LoginAsAdminAsync();

        JsonElement created = await PostAsync("/api/settings/parse-testcases", new
        {
            samplePath = @"F:\迅雷下载\Inception.2010.1080p.mkv",
        });
        long id = created.GetProperty("data").GetProperty("id").GetInt64();

        JsonElement triaged = await PostAsync("/api/settings/parse-testcases/triage", new { id });
        triaged.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);

        string? verdictJson = triaged.GetProperty("data").GetProperty("aiVerdict").GetString();
        verdictJson.Should().NotBeNullOrWhiteSpace();

        using JsonDocument doc = JsonDocument.Parse(verdictJson!);
        JsonElement root = doc.RootElement;
        root.GetProperty("worthAdding").GetBoolean().Should().BeTrue();
        root.GetProperty("reason").GetString().Should().Contain("值得纳入");
        root.GetProperty("suggested").GetProperty("title").GetString().Should().Be("盗梦空间");
        root.GetProperty("suggested").GetProperty("mediaType").GetString().Should().Be("Movie");
        // Triage 不改 Status：仍保持 Manual 新增的 Active
        triaged.GetProperty("data").GetProperty("status").GetString().Should().Be("Active");
    }

    [Fact]
    public async Task SuggestRule_Returns_Pattern_And_Persists_AiSuggestedRulePattern()
    {
        await LoginAsAdminAsync();

        JsonElement created = await PostAsync("/api/settings/parse-testcases", new
        {
            samplePath = @"F:\迅雷下载\Inception.2010.1080p.mkv",
            expectedTitle = "Inception",
            expectedYear = 2010,
            expectedMediaType = "Movie",
        });
        long id = created.GetProperty("data").GetProperty("id").GetInt64();

        JsonElement res = await PostAsync("/api/settings/parse-testcases/suggest-rule", new { id });
        res.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        res.GetProperty("data").GetProperty("testCaseId").GetInt64().Should().Be(id);
        res.GetProperty("data").GetProperty("pattern").GetString().Should().Contain("(?<title>");
        res.GetProperty("data").GetProperty("scope").GetString().Should().Be("FileName");
        res.GetProperty("data").GetProperty("defaultType").GetString().Should().Be("movie");
        res.GetProperty("data").GetProperty("explanation").GetString().Should().NotBeNullOrWhiteSpace();

        // pattern 已落 AiSuggestedRulePattern
        JsonElement detail = await GetAsync($"/api/settings/parse-testcases/{id}");
        detail.GetProperty("data").GetProperty("aiSuggestedRulePattern").GetString().Should().Contain("(?<title>");
    }

    private async Task LoginAsAdminAsync()
    {
        await PostAsync("/api/setup/admin", new { username = "admin", password = "secret123" });
        await PostAsync("/api/setup/complete", new { });
        JsonElement login = await PostAsync("/api/auth/login", new { username = "admin", password = "secret123" });
        string token = login.GetProperty("data").GetProperty("token").GetString()!;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private async Task<JsonElement> GetAsync(string url) =>
        await (await _client.GetAsync(url)).Content.ReadFromJsonAsync<JsonElement>();

    private async Task<JsonElement> PostAsync(string url, object payload) =>
        await (await _client.PostAsJsonAsync(url, payload)).Content.ReadFromJsonAsync<JsonElement>();
}
