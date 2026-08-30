using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Host.Middleware;

namespace PersonalMediaManager.Host.Tests.Settings;

/// <summary>MediaExtensions e2e：CRUD 闭环 + 边界校验（必填 / 格式 / 唯一）补测（原仅有 DTO 反射单测，无 Host 集成覆盖）</summary>
public sealed class MediaExtensionsTests : IDisposable
{
    private readonly PmmHostFactory _factory;
    private readonly HttpClient _client;

    public MediaExtensionsTests()
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
        await LoginAdminAsync();

        JsonElement created = await PostAsync("/api/settings/media-extensions", new { extension = ".hevc", description = "HEVC 编码", enabled = true });
        created.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        long id = created.GetProperty("data").GetProperty("id").GetInt64();
        created.GetProperty("data").GetProperty("extension").GetString().Should().Be(".hevc");

        JsonElement list = await GetAsync("/api/settings/media-extensions");
        list.GetProperty("data").EnumerateArray().Any(e => e.GetProperty("id").GetInt64() == id).Should().BeTrue();

        JsonElement upd = await PostAsync("/api/settings/media-extensions/update", new { id, extension = ".hevc", description = "改后", enabled = false });
        upd.GetProperty("data").GetProperty("enabled").GetBoolean().Should().BeFalse();

        JsonElement del = await PostAsync("/api/settings/media-extensions/delete", new { id });
        del.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);

        JsonElement after = await GetAsync("/api/settings/media-extensions");
        after.GetProperty("data").EnumerateArray().Any(e => e.GetProperty("id").GetInt64() == id).Should().BeFalse();
    }

    [Fact]
    public async Task Create_Duplicate_Returns1000()
    {
        await LoginAdminAsync();
        await PostAsync("/api/settings/media-extensions", new { extension = ".dup", enabled = true });
        JsonElement dup = await PostAsync("/api/settings/media-extensions", new { extension = ".dup", enabled = true });
        dup.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        dup.GetProperty("message").GetString().Should().Contain("已存在");
    }

    /// <summary>边界校验：扩展名纯空白 → [RequiredNotBlank] 拦截</summary>
    [Fact]
    public async Task Create_BlankExtension_RejectedByBoundaryValidation_Returns1000()
    {
        await LoginAdminAsync();
        JsonElement resp = await PostAsync("/api/settings/media-extensions", new { extension = "   ", enabled = true });
        resp.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        resp.GetProperty("message").GetString().Should().Contain("扩展名不能为空");
    }

    /// <summary>边界校验：扩展名不以 '.' 开头 → [RegularExpression] 拦截</summary>
    [Fact]
    public async Task Create_NoDotExtension_RejectedByBoundaryValidation_Returns1000()
    {
        await LoginAdminAsync();
        JsonElement resp = await PostAsync("/api/settings/media-extensions", new { extension = "mkv", enabled = true });
        resp.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        resp.GetProperty("message").GetString().Should().Contain("格式");
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
