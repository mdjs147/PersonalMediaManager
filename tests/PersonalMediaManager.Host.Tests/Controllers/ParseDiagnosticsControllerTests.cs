using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Common.Diagnostics;
using PersonalMediaManager.Host.Middleware;
using PersonalMediaManager.Infrastructure.Persistence;
using PersonalMediaManager.Infrastructure.Platform.Diagnostics;

namespace PersonalMediaManager.Host.Tests.Controllers;

public sealed class ParseDiagnosticsControllerTests
{
    [Fact]
    public async Task ExportRequiresAdminAndRejectsTraversal()
    {
        SetupGuardMiddleware.ResetCacheForTest();
        using PmmHostFactory factory = new(); using HttpClient client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/setup/admin", new { username = "admin", password = "secret123" });
        await client.PostAsJsonAsync("/api/setup/complete", new { });
        using HttpResponseMessage anonymous = await client.GetAsync("/api/diagnostics/parse/export?mediaItemId=42");
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        JsonElement login = await (await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "secret123" })).Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.GetProperty("data").GetProperty("token").GetString());
        ParseDiagnosticFileSink sink = factory.Services.GetRequiredService<ParseDiagnosticFileSink>();
        using (ParseDiagnostics.Begin("parse", mediaItemId: 42, sink: sink)) ParseDiagnostics.Emit("synthetic.event", new { safe = true });
        JsonElement export = await (await client.GetAsync("/api/diagnostics/parse/export?mediaItemId=42")).Content.ReadFromJsonAsync<JsonElement>();
        export.GetProperty("code").GetInt32().Should().Be(0);
        export.GetProperty("data").GetProperty("events").GetArrayLength().Should().Be(3);
        JsonElement invalid = await (await client.GetAsync("/api/diagnostics/parse/export?runId=..%2F..%2Fsecret")).Content.ReadFromJsonAsync<JsonElement>();
        invalid.GetProperty("code").GetInt32().Should().Be(1000);
        await client.PostAsJsonAsync("/api/account/users/create", new { username = "viewer", password = "viewer123", role = "Viewer" });
        JsonElement viewer = await (await client.PostAsJsonAsync("/api/auth/login", new { username = "viewer", password = "viewer123" })).Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", viewer.GetProperty("data").GetProperty("token").GetString());
        using HttpResponseMessage denied = await client.GetAsync("/api/diagnostics/parse/export?mediaItemId=42");
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        SetupGuardMiddleware.ResetCacheForTest();
    }

    [Theory]
    [InlineData("Off", ParseDiagnosticLevel.Off)]
    [InlineData("Detailed", ParseDiagnosticLevel.Detailed)]
    public void LocalConfigurationBindsRealHostAndClampsCapacity(string configured, ParseDiagnosticLevel expected)
    {
        string root = Path.Combine(Path.GetTempPath(), "pmm-diag-config-" + Guid.NewGuid().ToString("N"));
        PrivateFileSystem.EnsureDirectory(root);
        SqliteConnection? databaseConnection = null;
        try
        {
            File.WriteAllText(Path.Combine(root, "local.json"), JsonSerializer.Serialize(new { ParseDiagnostics = new { Level = configured, MaxFiles = -4, MaxTextUtf8Bytes = 512 } }));
            using PmmHostFactory factory = new PmmHostFactory().UseFixedRoot(root);
            using HttpClient client = factory.CreateClient();
            using PmmDbContext db = factory.Services.GetRequiredService<IDbContextFactory<PmmDbContext>>().CreateDbContext();
            databaseConnection = (SqliteConnection)db.Database.GetDbConnection();
            ParseDiagnosticFileSink sink = factory.Services.GetRequiredService<ParseDiagnosticFileSink>();
            sink.Options.Level.Should().Be(expected); sink.Options.MaxFiles.Should().Be(1); sink.Options.MaxTextUtf8Bytes.Should().Be(512);
            using (ParseDiagnostics.Begin("parse", mediaItemId: 4, sink: sink)) ParseDiagnostics.Emit("synthetic.event");
            sink.Export(null, 4).Events.Count.Should().Be(expected == ParseDiagnosticLevel.Off ? 0 : 3);
        }
        finally
        {
            // Host 已释放，但池内原生连接仍可能占用 Windows 文件；只清本测试库的池，不干扰并行测试。
            if (databaseConnection is not null) SqliteConnection.ClearPool(databaseConnection);
            SetupGuardMiddleware.ResetCacheForTest();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
