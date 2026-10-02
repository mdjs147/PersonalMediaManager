using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PersonalMediaManager.Application.Dtos.LocalAi;
using PersonalMediaManager.Host.Middleware;

namespace PersonalMediaManager.Host.Tests.Controllers;

public sealed class LocalAiControllerTests : IDisposable
{
    private readonly PmmHostFactory _factory;
    private readonly HttpClient _client;

    public LocalAiControllerTests()
    {
        SetupGuardMiddleware.ResetCacheForTest();
        _factory = new();
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task AnonymousCannotInspectOrMutateLocalRuntime()
    {
        await _client.PostAsJsonAsync("/api/setup/admin", new { username = "admin", password = "secret123" });
        await _client.PostAsJsonAsync("/api/setup/complete", new { });
        foreach (string suffix in new[] { "", "/models", "/status" })
        {
            using HttpResponseMessage response = await _client.GetAsync("/api/settings/local-ai" + suffix);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        foreach (string suffix in new[] { "/start", "/stop", "/download/cancel" })
        {
            using HttpResponseMessage response = await _client.PostAsJsonAsync("/api/settings/local-ai" + suffix, new { });
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        using HttpResponseMessage update = await _client.PostAsJsonAsync("/api/settings/local-ai/update", new LocalAiSettingsDto());
        update.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using HttpResponseMessage download = await _client.PostAsJsonAsync("/api/settings/local-ai/download", new { modelId = LocalAiModelIds.Qwen });
        download.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ViewerCannotStartRuntimeOrReadExecutablePath()
    {
        await LoginAsync();
        JsonElement created = await ReadAsync(await _client.PostAsJsonAsync("/api/account/users/create",
            new { username = "viewer", password = "viewer123", role = "Viewer" }));
        created.GetProperty("code").GetInt32().Should().Be(0);
        JsonElement login = await ReadAsync(await _client.PostAsJsonAsync("/api/auth/login", new { username = "viewer", password = "viewer123" }));
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.GetProperty("data").GetProperty("token").GetString());
        using HttpResponseMessage get = await _client.GetAsync("/api/settings/local-ai");
        get.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using HttpResponseMessage start = await _client.PostAsJsonAsync("/api/settings/local-ai/start", new { });
        start.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AdminGetsDisabledDefault_AndInvalidSettingsCannotEnableRuntime()
    {
        await LoginAsync();
        JsonElement initial = await ReadAsync(await _client.GetAsync("/api/settings/local-ai"));
        initial.GetProperty("data").GetProperty("mode").GetString().Should().Be("Disabled");
        JsonElement invalid = await ReadAsync(await _client.PostAsJsonAsync("/api/settings/local-ai/update",
            new LocalAiSettingsDto { Mode = LocalAiMode.BeforeRules, Port = 80 }));
        invalid.GetProperty("code").GetInt32().Should().Be(1000);
        JsonElement final = await ReadAsync(await _client.GetAsync("/api/settings/local-ai"));
        final.GetProperty("data").GetProperty("mode").GetString().Should().Be("Disabled");
        JsonElement start = await ReadAsync(await _client.PostAsJsonAsync("/api/settings/local-ai/start", new { }));
        start.GetProperty("code").GetInt32().Should().Be(1000);
        JsonElement status = await ReadAsync(await _client.GetAsync("/api/settings/local-ai/status"));
        status.GetProperty("data").GetProperty("runtimeConfigured").GetBoolean().Should().BeFalse();
        status.GetProperty("data").GetProperty("state").GetString().Should().Be("Stopped");
    }

    [Fact]
    public async Task ModelCatalogAndActions_DoNotSilentlyEnableMode()
    {
        await LoginAsync();
        JsonElement models = await ReadAsync(await _client.GetAsync("/api/settings/local-ai/models"));
        models.GetProperty("data").EnumerateArray().Should().Contain(x => x.GetProperty("id").GetString() == LocalAiModelIds.Qwen
            && x.GetProperty("canDownload").GetBoolean());
        JsonElement blocked = await ReadAsync(await _client.PostAsJsonAsync("/api/settings/local-ai/download", new { modelId = "https://evil.example/model" }));
        blocked.GetProperty("code").GetInt32().Should().Be(1000);
        await _client.PostAsJsonAsync("/api/settings/local-ai/stop", new { });
        await _client.PostAsJsonAsync("/api/settings/local-ai/download/cancel", new { });
        JsonElement settings = await ReadAsync(await _client.GetAsync("/api/settings/local-ai"));
        settings.GetProperty("data").GetProperty("mode").GetString().Should().Be("Disabled");
    }

    [Fact]
    public async Task ExperimentalLocalModel_AdvertisesFixedFileAndCanBeSelectedWithoutDownloadOrEnablement()
    {
        await LoginAsync();
        JsonElement models = await ReadAsync(await _client.GetAsync("/api/settings/local-ai/models"));
        JsonElement model = models.GetProperty("data").EnumerateArray().Single(x => x.GetProperty("id").GetString() == LocalAiModelIds.Huihui);
        model.GetProperty("canVerify").GetBoolean().Should().BeTrue();
        model.GetProperty("canDownload").GetBoolean().Should().BeFalse();
        model.GetProperty("installed").GetBoolean().Should().BeFalse();
        model.GetProperty("sizeBytes").GetInt64().Should().Be(531068416);
        model.GetProperty("sha256").GetString().Should().Be("15c5d19fd98774df4fa4df949e4f8cc7c8b32ff366895e23d69f9222b4082816");
        string fileName = model.GetProperty("fileName").GetString()!;
        fileName.Should().Be("huihui-qwen2.5-0.5b-v3-q8_0.gguf");
        string path = model.GetProperty("localPath").GetString()!;
        Path.IsPathFullyQualified(path).Should().BeTrue();
        Path.GetFileName(Path.GetDirectoryName(path)).Should().Be("local-ai-models");
        Path.GetFileName(path).Should().Be(fileName);
        model.GetProperty("conversionRevision").GetString().Should().Be("13b4d7135a6351f81e1eccf6361a4eafd50350eb");
        model.GetProperty("unavailableReason").GetString().Should().Contain("本地自转");

        JsonElement download = await ReadAsync(await _client.PostAsJsonAsync("/api/settings/local-ai/download", new { modelId = LocalAiModelIds.Huihui }));
        download.GetProperty("code").GetInt32().Should().Be(1000);
        JsonElement update = await ReadAsync(await _client.PostAsJsonAsync("/api/settings/local-ai/update",
            new LocalAiSettingsDto { ModelId = LocalAiModelIds.Huihui }));
        update.GetProperty("code").GetInt32().Should().Be(0);
        JsonElement settings = await ReadAsync(await _client.GetAsync("/api/settings/local-ai"));
        settings.GetProperty("data").GetProperty("modelId").GetString().Should().Be(LocalAiModelIds.Huihui);
        settings.GetProperty("data").GetProperty("mode").GetString().Should().Be("Disabled");
        JsonElement status = await ReadAsync(await _client.GetAsync("/api/settings/local-ai/status"));
        status.GetProperty("data").GetProperty("state").GetString().Should().Be("Stopped");
        status.GetProperty("data").GetProperty("downloadState").GetString().Should().Be("Idle");
    }

    private async Task LoginAsync()
    {
        await _client.PostAsJsonAsync("/api/setup/admin", new { username = "admin", password = "secret123" });
        await _client.PostAsJsonAsync("/api/setup/complete", new { });
        JsonElement login = await ReadAsync(await _client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "secret123" }));
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.GetProperty("data").GetProperty("token").GetString());
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        using (response)
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            json.RootElement.TryGetProperty("requestId", out _).Should().BeTrue();
            return json.RootElement.Clone();
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        SetupGuardMiddleware.ResetCacheForTest();
    }
}
