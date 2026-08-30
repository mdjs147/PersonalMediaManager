using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Host.Middleware;

namespace PersonalMediaManager.Host.Tests.Auth;

/// <summary>Setup → Login → Me → 改密 → 登出 → 之前 token 仍合法 端到端冒烟</summary>
/// <remarks>
/// 每个测试方法用独立 PmmHostFactory（独立 LocalAppData 临时目录 + 干净数据库）；
/// SetupGuardMiddleware._completedCache 是 static 字段会跨方法污染，故 ctor 重置 + 每测试自带 fixture。
/// </remarks>
public sealed class SetupAuthAccountE2ETests : IDisposable
{
    private readonly PmmHostFactory _factory;
    private readonly HttpClient _client;

    public SetupAuthAccountE2ETests()
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
    public async Task BeforeSetup_AdminEndpoint_Returns1000WithSetupRequiredMessage()
    {
        HttpResponseMessage resp = await _client.GetAsync("/api/account/users");
        JsonElement body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        body.GetProperty("message").GetString().Should().Be("请先完成初始化");
    }

    [Fact]
    public async Task FullFlow_Setup_Login_Me_ChangePassword_Logout()
    {
        JsonElement statusBefore = await GetJsonAsync("/api/setup/status");
        statusBefore.GetProperty("data").GetProperty("isCompleted").GetBoolean().Should().BeFalse();

        JsonElement adminCreated = await PostJsonAsync("/api/setup/admin", new { username = "admin", password = "secret123" });
        adminCreated.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);

        JsonElement completed = await PostJsonAsync("/api/setup/complete", new { });
        completed.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);

        JsonElement login = await PostJsonAsync("/api/auth/login", new { username = "admin", password = "secret123" });
        login.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        string token = login.GetProperty("data").GetProperty("token").GetString()!;
        token.Should().NotBeNullOrWhiteSpace();

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        JsonElement me = await GetJsonAsync("/api/auth/me");
        me.GetProperty("data").GetProperty("username").GetString().Should().Be("admin");
        me.GetProperty("data").GetProperty("role").GetString().Should().Be("Admin");

        JsonElement wrongOld = await PostJsonAsync("/api/account/password/change", new { oldPassword = "wrong", newPassword = "newsecret123" });
        wrongOld.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);

        JsonElement changeOk = await PostJsonAsync("/api/account/password/change", new { oldPassword = "secret123", newPassword = "newsecret123" });
        changeOk.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);

        JsonElement logout = await PostJsonAsync("/api/auth/logout", new { });
        logout.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
    }

    [Fact]
    public async Task AfterSetup_DeleteLastAdmin_Returns1000()
    {
        await PostJsonAsync("/api/setup/admin", new { username = "soloadmin", password = "secret123" });
        await PostJsonAsync("/api/setup/complete", new { });

        JsonElement login = await PostJsonAsync("/api/auth/login", new { username = "soloadmin", password = "secret123" });
        string token = login.GetProperty("data").GetProperty("token").GetString()!;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        JsonElement list = await GetJsonAsync("/api/account/users");
        long adminId = list.GetProperty("data")[0].GetProperty("id").GetInt64();

        JsonElement del = await PostJsonAsync($"/api/account/users/{adminId}/delete", new { });
        del.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        del.GetProperty("message").GetString().Should().Contain("最后一个管理员");
    }

    private async Task<JsonElement> GetJsonAsync(string url)
    {
        HttpResponseMessage resp = await _client.GetAsync(url);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> PostJsonAsync(string url, object payload)
    {
        HttpResponseMessage resp = await _client.PostAsJsonAsync(url, payload);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }
}
