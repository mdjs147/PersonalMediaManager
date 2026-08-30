using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PersonalMediaManager.Host.Middleware;

namespace PersonalMediaManager.Host.Tests.Diagnostics;

/// <summary>OpenAPI 文档与 Scalar UI 端点匿名可达性 + Setup 前可见性（C3.1）</summary>
public sealed class OpenApiAndScalarTests : IDisposable
{
    private readonly PmmHostFactory _factory;
    private readonly HttpClient _client;

    public OpenApiAndScalarTests()
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
    public async Task OpenApi_DocumentReachableAnonymously_BeforeSetup()
    {
        // SetupGuard 白名单 /openapi；AllowAnonymous 让全局 FallbackPolicy(Admin) 不拦截
        HttpResponseMessage resp = await _client.GetAsync("/openapi/v1.json");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        doc.GetProperty("openapi").GetString().Should().StartWith("3.", "应该是 OpenAPI 3.x 文档");
        doc.TryGetProperty("paths", out JsonElement paths).Should().BeTrue();
        // 至少包含一个我们注册的路由（任挑 /api/health；控制器统一 api/ 前缀）
        paths.TryGetProperty("/api/health", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Scalar_UiReachableAnonymously()
    {
        HttpResponseMessage resp = await _client.GetAsync("/scalar/v1");
        // Scalar 返回 HTML 页面（200 OK + text/html）
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
    }

    /// <summary>CI schema drift 钩子：当 PMM_OPENAPI_DUMP_PATH 环境变量指向一个目录时，把 /openapi/v1.json 落盘到该目录</summary>
    /// <remarks>
    /// 设计动机：
    /// - Launcher 的 WinForms 托盘在无桌面 Session 的 self-hosted agent（NetworkService）下起不来；
    /// - 这里复用 PmmHostFactory（TestServer，无 Kestrel / 无 WinForms），直接拿 OpenAPI 文档落盘；
    /// - scripts/check-schema-drift.ps1 据此跑 openapi-typescript 重生 schema.d.ts 后 git diff --exit-code。
    /// 本地无 env var 时直接 return，避免污染开发机临时目录。
    /// </remarks>
    [Fact]
    public async Task OpenApi_DumpToArtifact_ForCi()
    {
        string? dumpDir = Environment.GetEnvironmentVariable("PMM_OPENAPI_DUMP_PATH");
        if (string.IsNullOrEmpty(dumpDir))
        {
            return; // 本地未设环境变量：跳过
        }

        Directory.CreateDirectory(dumpDir);

        HttpResponseMessage resp = await _client.GetAsync("/openapi/v1.json");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        string json = await resp.Content.ReadAsStringAsync();

        string path = Path.Combine(dumpDir, "openapi.json");
        await File.WriteAllTextAsync(path, json);
    }
}
