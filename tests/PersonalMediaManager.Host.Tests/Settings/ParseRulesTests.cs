using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Host.Middleware;

namespace PersonalMediaManager.Host.Tests.Settings;

/// <summary>ParseRules e2e：CRUD 闭环 + 正则编译失败 1000 + /test 命名捕获 + 非法 DefaultType 1000</summary>
public sealed class ParseRulesTests : IDisposable
{
    private readonly PmmHostFactory _factory;
    private readonly HttpClient _client;

    public ParseRulesTests()
    {
        SetupGuardMiddleware.ResetCacheForTest();
        _factory = new PmmHostFactory();
        _client = _factory.CreateClient();
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

        JsonElement created = await PostAsync("/api/settings/parse-rules", new
        {
            name = "标准电影",
            scope = "FileName",
            pattern = "^(?<title>.+?)\\.(?<year>\\d{4})\\.",
            defaultType = "movie",
            forceType = false,
            priority = 100,
            confidenceBonus = 0.1,
            enabled = true,
            description = (string?)null,
        });
        created.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        long id = created.GetProperty("data").GetProperty("id").GetInt64();
        long rowVersion = created.GetProperty("data").GetProperty("rowVersion").GetInt64();
        created.GetProperty("data").GetProperty("defaultType").GetString().Should().Be("movie");

        JsonElement list = await GetAsync("/api/settings/parse-rules");
        list.GetProperty("data").EnumerateArray()
            .Any(e => e.GetProperty("id").GetInt64() == id).Should().BeTrue();

        JsonElement upd = await PostAsync("/api/settings/parse-rules/update", new
        {
            id,
            name = "标准电影 v2",
            scope = "FullPath",
            pattern = "(?<title>.+?)\\.(?<year>\\d{4})",
            defaultType = "movie",
            forceType = true,
            priority = 10,
            confidenceBonus = 0.2,
            enabled = false,
            description = "改后",
            rowVersion,
        });
        upd.GetProperty("data").GetProperty("forceType").GetBoolean().Should().BeTrue();
        upd.GetProperty("data").GetProperty("scope").GetString().Should().Be("FullPath");
        upd.GetProperty("data").GetProperty("rowVersion").GetInt64().Should().BeGreaterThan(rowVersion);

        JsonElement del = await PostAsync("/api/settings/parse-rules/delete", new { id });
        del.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
    }

    /// <summary>r3 第一波：并发冲突场景——B 端拿到 v1 后 A 端先改成 v2，B 仍用 v1 提交应被服务端 1000 拒绝</summary>
    [Fact]
    public async Task ConcurrentUpdate_StaleRowVersion_Returns_1000()
    {
        await LoginAsAdminAsync();

        JsonElement created = await PostAsync("/api/settings/parse-rules", new
        {
            name = "并发样本",
            scope = "FileName",
            pattern = "(?<title>.+)",
            defaultType = (string?)null,
            forceType = false,
            priority = 100,
            confidenceBonus = 0.0,
            enabled = true,
            description = (string?)null,
        });
        long id = created.GetProperty("data").GetProperty("id").GetInt64();
        long staleVersion = created.GetProperty("data").GetProperty("rowVersion").GetInt64();

        // A 端先以正确版本号更新成功
        JsonElement firstUpd = await PostAsync("/api/settings/parse-rules/update", new
        {
            id, name = "并发样本-A", scope = "FileName", pattern = "(?<title>.+)",
            defaultType = (string?)null, forceType = false, priority = 100, confidenceBonus = 0.0,
            enabled = true, description = "A 先改",
            rowVersion = staleVersion,
        });
        firstUpd.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);

        // B 端仍持旧 rowVersion 提交：服务端应拒绝并返 1000
        JsonElement secondUpd = await PostAsync("/api/settings/parse-rules/update", new
        {
            id, name = "并发样本-B", scope = "FileName", pattern = "(?<title>.+)",
            defaultType = (string?)null, forceType = false, priority = 100, confidenceBonus = 0.0,
            enabled = true, description = "B 后来",
            rowVersion = staleVersion,
        });
        secondUpd.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        secondUpd.GetProperty("message").GetString().Should().Contain("请刷新");
    }

    [Fact]
    public async Task Create_InvalidRegex_Returns1000()
    {
        await LoginAsAdminAsync();
        JsonElement resp = await PostAsync("/api/settings/parse-rules", new
        {
            name = "坏正则",
            scope = "FileName",
            pattern = "(?<title>[", // 未闭合括号
            defaultType = (string?)null,
            forceType = false,
            priority = 100,
            confidenceBonus = 0.0,
            enabled = true,
            description = (string?)null,
        });
        resp.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        resp.GetProperty("message").GetString().Should().Contain("正则编译失败");
    }

    [Fact]
    public async Task Create_InvalidDefaultType_Returns1000()
    {
        await LoginAsAdminAsync();
        JsonElement resp = await PostAsync("/api/settings/parse-rules", new
        {
            name = "类型不合法",
            scope = "FileName",
            pattern = ".*",
            defaultType = "anime", // 仅允许 movie / tv
            forceType = false,
            priority = 100,
            confidenceBonus = 0.0,
            enabled = true,
            description = (string?)null,
        });
        resp.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        resp.GetProperty("message").GetString().Should().Contain("movie / tv");
    }

    [Fact]
    public async Task Test_EndpointReturnsNamedGroups()
    {
        await LoginAsAdminAsync();
        JsonElement resp = await PostAsync("/api/settings/parse-rules/test", new
        {
            pattern = "^(?<title>.+?)\\.(?<year>\\d{4})\\.",
            sample = "Inception.2010.1080p.BluRay.x264.mkv",
        });
        resp.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        resp.GetProperty("data").GetProperty("matched").GetBoolean().Should().BeTrue();
        JsonElement groups = resp.GetProperty("data").GetProperty("groups");
        groups.GetProperty("title").GetString().Should().Be("Inception");
        groups.GetProperty("year").GetString().Should().Be("2010");
    }

    [Fact]
    public async Task Test_InvalidPattern_ReturnsMatchedFalseWithError()
    {
        await LoginAsAdminAsync();
        JsonElement resp = await PostAsync("/api/settings/parse-rules/test", new
        {
            pattern = "(?<unclosed",
            sample = "abc",
        });
        resp.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        resp.GetProperty("data").GetProperty("matched").GetBoolean().Should().BeFalse();
        resp.GetProperty("data").GetProperty("errorMessage").GetString().Should().Contain("正则编译失败");
    }

    [Fact]
    public async Task Builtin_Endpoint_Returns16StaticRules()
    {
        await LoginAsAdminAsync();
        JsonElement resp = await GetAsync("/api/settings/parse-rules/builtin");
        resp.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);

        JsonElement data = resp.GetProperty("data");
        data.GetArrayLength().Should().Be(16, "RuleEngineService 内置 16 条静态正则（季识别增强 + 压制代号-集号整串兜底 ReleaseTagEpisode），全部从 BuiltinRulesCatalog 透出");

        // 每条必备字段 + 升序排序（按 order）
        List<int> orders = data.EnumerateArray().Select(e => e.GetProperty("order").GetInt32()).ToList();
        orders.Should().BeInAscendingOrder();
        orders.Should().Equal(10, 20, 30, 40, 50, 52, 55, 56, 57, 58, 60, 70, 75, 80, 85, 90);

        // 16 条 key 与 catalog 对齐（含季识别增强的 SeasonRoman + SeasonArc + SeasonWordLatin 与 ReleaseTagEpisode）
        IEnumerable<string?> keys = data.EnumerateArray().Select(e => e.GetProperty("key").GetString());
        keys.Should().BeEquivalentTo(new[]
        {
            "SeasonEpisodeLatin", "SeasonChinese", "EpisodeChinese", "EpisodeOnly",
            "BracketEpisode", "ReleaseTagEpisode", "SeasonOnlyLatin", "SeasonRoman", "SeasonArc", "SeasonWordLatin", "Year", "Noise", "TotalCountNoise", "GroupBracket", "ReleaseGroupSuffix", "Separator",
        });

        // 抽查 pattern + samples 非空
        JsonElement first = data.EnumerateArray().First();
        first.GetProperty("name").GetString().Should().NotBeNullOrWhiteSpace();
        first.GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace();
        first.GetProperty("pattern").GetString().Should().NotBeNullOrWhiteSpace();
        first.GetProperty("samples").GetArrayLength().Should().BeGreaterThan(0);
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
