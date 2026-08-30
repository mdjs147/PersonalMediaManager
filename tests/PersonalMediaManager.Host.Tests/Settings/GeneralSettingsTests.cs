using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Host.Middleware;

namespace PersonalMediaManager.Host.Tests.Settings;

/// <summary>GeneralSettings：列表 + 批量更新闭环（含 Setup → Login → 携带 token 测 Admin 端点）</summary>
public sealed class GeneralSettingsTests : IDisposable
{
    private readonly PmmHostFactory _factory;
    private readonly HttpClient _client;

    public GeneralSettingsTests()
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
    public async Task ListAndUpdate_RoundTrip()
    {
        await PostAsync("/api/setup/admin", new { username = "admin", password = "secret123" });
        await PostAsync("/api/setup/complete", new { });

        JsonElement login = await PostAsync("/api/auth/login", new { username = "admin", password = "secret123" });
        string token = login.GetProperty("data").GetProperty("token").GetString()!;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        JsonElement list1 = await GetAsync("/api/settings/general");
        list1.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        JsonElement groups = list1.GetProperty("data").GetProperty("groups");
        groups.TryGetProperty("General", out JsonElement generalGroup).Should().BeTrue();
        generalGroup.EnumerateArray().Any(e => e.GetProperty("key").GetString() == "Web.Port").Should().BeTrue();

        JsonElement upd = await PostAsync("/api/settings/general/update", new
        {
            items = new[] { new { key = "Web.Port", value = "9999" }, new { key = "Custom.New", value = "v1" } },
        });
        upd.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);

        JsonElement list2 = await GetAsync("/api/settings/general");
        JsonElement allItems = list2.GetProperty("data").GetProperty("groups");
        bool foundUpdated = allItems.EnumerateObject()
            .SelectMany(p => p.Value.EnumerateArray())
            .Any(e => e.GetProperty("key").GetString() == "Web.Port" && e.GetProperty("value").GetString() == "9999");
        foundUpdated.Should().BeTrue("更新后 Web.Port 应反映为 9999");

        bool foundNew = allItems.EnumerateObject()
            .SelectMany(p => p.Value.EnumerateArray())
            .Any(e => e.GetProperty("key").GetString() == "Custom.New" && e.GetProperty("value").GetString() == "v1");
        foundNew.Should().BeTrue("新增 Key=Custom.New 应被持久化");
    }

    [Fact]
    public async Task Update_ProtectedKey_Returns1000()
    {
        await PostAsync("/api/setup/admin", new { username = "admin", password = "secret123" });
        await PostAsync("/api/setup/complete", new { });
        JsonElement login = await PostAsync("/api/auth/login", new { username = "admin", password = "secret123" });
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.GetProperty("data").GetProperty("token").GetString());

        JsonElement resp = await PostAsync("/api/settings/general/update", new
        {
            items = new[] { new { key = "System.SetupCompleted", value = "false" } },
        });
        resp.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        resp.GetProperty("message").GetString().Should().Contain("受保护");
    }

    /// <summary>档2 边界校验实验：空 Items → 集合级 [MinLength(1)] 拦截，返 1000（不进 service）</summary>
    [Fact]
    public async Task Update_EmptyItems_RejectedByBoundaryValidation_Returns1000()
    {
        await LoginAdminAsync();
        JsonElement resp = await PostAsync("/api/settings/general/update", new { items = Array.Empty<object>() });
        resp.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        resp.GetProperty("message").GetString().Should().Contain("没有需要更新的配置项");
    }

    /// <summary>档2 边界校验实验：元素 Key 空白 → 集合元素递归 [RequiredNotBlank] 拦截，返 1000（验证 MVC 是否递归校验集合元素）</summary>
    [Fact]
    public async Task Update_BlankItemKey_RejectedByBoundaryValidation_Returns1000()
    {
        await LoginAdminAsync();
        JsonElement resp = await PostAsync("/api/settings/general/update", new { items = new[] { new { key = "   ", value = "x" } } });
        resp.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        resp.GetProperty("message").GetString().Should().Contain("配置项 Key 不能为空");
    }

    private async Task LoginAdminAsync()
    {
        await PostAsync("/api/setup/admin", new { username = "admin", password = "secret123" });
        await PostAsync("/api/setup/complete", new { });
        JsonElement login = await PostAsync("/api/auth/login", new { username = "admin", password = "secret123" });
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.GetProperty("data").GetProperty("token").GetString());
    }

    private async Task<JsonElement> GetAsync(string url) =>
        await (await _client.GetAsync(url)).Content.ReadFromJsonAsync<JsonElement>();

    private async Task<JsonElement> PostAsync(string url, object payload) =>
        await (await _client.PostAsJsonAsync(url, payload)).Content.ReadFromJsonAsync<JsonElement>();
}
