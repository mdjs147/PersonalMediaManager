using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Host.Middleware;

namespace PersonalMediaManager.Host.Tests.SystemModule;

/// <summary>E.2.4 System 模块 E2E：info / export → import 回环 + Zip Slip 守卫</summary>
/// <remarks>
/// 每用例独立 PmmHostFactory：临时 AppPaths + 干净 db。
/// Setup → Login 拿 token 后访问 /system/* (要求 Admin)。
/// </remarks>
public sealed class SystemControllerTests : IDisposable
{
    private readonly PmmHostFactory _factory;
    private readonly HttpClient _client;

    public SystemControllerTests()
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

    [Fact(DisplayName = "GET /system/info 返回版本 / OS / 运行时 / 数据目录")]
    public async Task GetInfo_ReturnsBasicMetadata()
    {
        await LoginAsAdmin();

        HttpResponseMessage resp = await _client.GetAsync("/api/system/info");
        JsonElement body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);

        JsonElement data = body.GetProperty("data");
        data.GetProperty("version").GetString().Should().NotBeNullOrWhiteSpace();
        data.GetProperty("os").GetString().Should().NotBeNullOrWhiteSpace();
        data.GetProperty("runtimeVersion").GetString().Should().Contain(".NET");
        data.GetProperty("dataRoot").GetString().Should().Be(_factory.Paths.Root);
        data.GetProperty("dbSizeBytes").GetInt64().Should().BeGreaterThan(0, "Migration 已建表");
    }

    [Fact(DisplayName = "POST /system/export 返回 zip 流且条目含 pmm.db")]
    public async Task Export_ReturnsZipContainingDb()
    {
        await LoginAsAdmin();

        HttpResponseMessage resp = await _client.PostAsync("/api/system/export", content: null);
        resp.IsSuccessStatusCode.Should().BeTrue();
        resp.Content.Headers.ContentType?.MediaType.Should().Be("application/zip");

        byte[] bytes = await resp.Content.ReadAsByteArrayAsync();
        bytes.Length.Should().BeGreaterThan(100);

        using MemoryStream ms = new(bytes);
        using global::System.IO.Compression.ZipArchive zip = new(ms, global::System.IO.Compression.ZipArchiveMode.Read);
        zip.Entries.Should().Contain(e => e.FullName == "pmm.db", "导出必含 pmm.db");
    }

    [Fact(DisplayName = "Import 拒绝 Zip Slip 路径（条目名含 ../）")]
    public async Task Import_RejectsZipSlip()
    {
        await LoginAsAdmin();

        using MemoryStream zipStream = new();
        using (global::System.IO.Compression.ZipArchive zip = new(zipStream, global::System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            global::System.IO.Compression.ZipArchiveEntry entry = zip.CreateEntry("../../evil.db");
            await using Stream es = entry.Open();
            await es.WriteAsync(Encoding.UTF8.GetBytes("malicious"));
        }
        zipStream.Position = 0;

        using MultipartFormDataContent form = new();
        form.Add(new StreamContent(zipStream), "file", "evil.zip");
        HttpResponseMessage resp = await _client.PostAsync("/api/system/import", form);
        JsonElement body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        body.GetProperty("message").GetString().Should().Contain("Zip Slip");
    }

    [Fact(DisplayName = "Import 拒绝缺少 pmm.db 的 zip")]
    public async Task Import_RejectsMissingDb()
    {
        await LoginAsAdmin();

        using MemoryStream zipStream = new();
        using (global::System.IO.Compression.ZipArchive zip = new(zipStream, global::System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            global::System.IO.Compression.ZipArchiveEntry entry = zip.CreateEntry("other.txt");
            await using Stream es = entry.Open();
            await es.WriteAsync(Encoding.UTF8.GetBytes("noise"));
        }
        zipStream.Position = 0;

        using MultipartFormDataContent form = new();
        form.Add(new StreamContent(zipStream), "file", "bad.zip");
        HttpResponseMessage resp = await _client.PostAsync("/api/system/import", form);
        JsonElement body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        body.GetProperty("message").GetString().Should().Contain("pmm.db");
    }

    [Fact(DisplayName = "Export → Import 回环：导出的 zip 被导入后只暂存（生成 .import-pending），RequiresRestart=true")]
    public async Task ExportImport_Roundtrip_StagesPending()
    {
        await LoginAsAdmin();

        // 1. 导出
        HttpResponseMessage exportResp = await _client.PostAsync("/api/system/export", content: null);
        exportResp.IsSuccessStatusCode.Should().BeTrue();
        byte[] zipBytes = await exportResp.Content.ReadAsByteArrayAsync();

        // 2. 立即把导出的 zip 当输入 import 回去 —— 新行为：仅暂存，不在运行中热替换主库
        using MemoryStream importStream = new(zipBytes);
        using MultipartFormDataContent form = new();
        form.Add(new StreamContent(importStream), "file", "export.zip");
        HttpResponseMessage importResp = await _client.PostAsync("/api/system/import", form);
        JsonElement body = await importResp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        body.GetProperty("data").GetProperty("success").GetBoolean().Should().BeTrue();
        body.GetProperty("data").GetProperty("requiresRestart").GetBoolean().Should().BeTrue();
        body.GetProperty("data").GetProperty("stagedPath").GetString().Should().Contain(".import-pending");

        // 暂存文件落在数据目录；运行中的主库未被替换（仍可正常服务）
        File.Exists(Path.Combine(_factory.Paths.Root, "pmm.db" + ImportStaging.PendingSuffix))
            .Should().BeTrue("导入应只暂存到 pmm.db.import-pending");
        (await _client.GetAsync("/api/system/info")).IsSuccessStatusCode.Should().BeTrue("运行中的库未被热替换，服务正常");
    }

    [Fact(DisplayName = "Import 暂存 → 重启（同 Root 新 Host）→ 启动期换库：pending 被消费且生成 preimport 备份")]
    public async Task Import_AppliesPendingSwapOnNextStart()
    {
        string root = Path.Combine(Path.GetTempPath(), $"pmm-restart-{Guid.NewGuid():N}");
        string pendingPath = Path.Combine(root, "pmm.db" + ImportStaging.PendingSuffix);
        try
        {
            // ---- 第一次启动：导出并导入回去（仅暂存）----
            using (PmmHostFactory f1 = new PmmHostFactory().UseFixedRoot(root))
            using (HttpClient c1 = f1.CreateClient())
            {
                await LoginAsAdmin(c1);
                HttpResponseMessage exportResp = await c1.PostAsync("/api/system/export", content: null);
                byte[] zipBytes = await exportResp.Content.ReadAsByteArrayAsync();

                using MemoryStream importStream = new(zipBytes);
                using MultipartFormDataContent form = new();
                form.Add(new StreamContent(importStream), "file", "a.zip");
                HttpResponseMessage importResp = await c1.PostAsync("/api/system/import", form);
                JsonElement body = await importResp.Content.ReadFromJsonAsync<JsonElement>();
                body.GetProperty("data").GetProperty("requiresRestart").GetBoolean().Should().BeTrue();
                File.Exists(pendingPath).Should().BeTrue("导入只暂存，pending 文件应存在");
            }

            // 模拟进程重启：清掉本进程内的 SQLite 连接池（真实场景是全新进程 = 空池，文件句柄已释放）
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            // ---- 第二次启动：同 Root 新 Host → CreateApp 在开库前应用换库 ----
            using (PmmHostFactory f2 = new PmmHostFactory().UseFixedRoot(root))
            using (HttpClient c2 = f2.CreateClient())
            {
                File.Exists(pendingPath).Should().BeFalse("换库后 pending 应被消费");
                Directory.GetFiles(root, "pmm.db.preimport-*").Should().NotBeEmpty("换库应备份旧库");

                // 换库后新库可正常服务（匿名版本端点足以证明 Host 起来且库可读）
                (await c2.GetAsync("/api/system/version")).IsSuccessStatusCode.Should().BeTrue();
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* 清理失败不阻断 */ }
        }
    }

    [Fact(DisplayName = "Import 拒绝空文件请求（零字节 file）")]
    public async Task Import_RejectsEmptyFile()
    {
        await LoginAsAdmin();

        using MemoryStream emptyStream = new(new byte[0]);
        using MultipartFormDataContent form = new();
        form.Add(new StreamContent(emptyStream), "file", "empty.zip");
        HttpResponseMessage resp = await _client.PostAsync("/api/system/import", form);
        JsonElement body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        body.GetProperty("message").GetString().Should().Contain("未上传");
    }

    [Fact(DisplayName = "POST /system/backup → GET /system/backups 列出刚创建的备份")]
    public async Task Backup_ThenBackups_ListsCreatedBackup()
    {
        await LoginAsAdmin();

        HttpResponseMessage backupResp = await _client.PostAsync("/api/system/backup", content: null);
        JsonElement backupBody = await backupResp.Content.ReadFromJsonAsync<JsonElement>();
        backupBody.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        backupBody.GetProperty("data").GetProperty("performed").GetBoolean().Should().BeTrue();
        string fileName = backupBody.GetProperty("data").GetProperty("fileName").GetString()!;
        fileName.Should().StartWith("pmm-backup-").And.EndWith(".zip");

        HttpResponseMessage listResp = await _client.GetAsync("/api/system/backups");
        JsonElement listBody = await listResp.Content.ReadFromJsonAsync<JsonElement>();
        listBody.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        JsonElement arr = listBody.GetProperty("data");
        arr.GetArrayLength().Should().BeGreaterThan(0);
        arr.EnumerateArray().Select(e => e.GetProperty("fileName").GetString())
            .Should().Contain(fileName, "列表应含刚创建的备份");
    }

    [Theory(DisplayName = "POST /system/restore 拒绝非法文件名（路径穿越 / 绝对路径 / 非备份命名）")]
    [InlineData("../../evil.zip")]
    [InlineData("..\\..\\evil.zip")]
    [InlineData("C:\\Windows\\system32\\config.zip")]
    [InlineData("not-a-backup.zip")]
    [InlineData("")]
    public async Task Restore_RejectsIllegalFileName(string fileName)
    {
        await LoginAsAdmin();

        HttpResponseMessage resp = await _client.PostAsJsonAsync("/api/system/restore", new { fileName });
        JsonElement body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
    }

    [Fact(DisplayName = "POST /system/backup → POST /system/restore 复用导入暂存（生成 .import-pending，RequiresRestart=true）")]
    public async Task Backup_ThenRestore_StagesPending()
    {
        await LoginAsAdmin();

        HttpResponseMessage backupResp = await _client.PostAsync("/api/system/backup", content: null);
        JsonElement backupBody = await backupResp.Content.ReadFromJsonAsync<JsonElement>();
        string fileName = backupBody.GetProperty("data").GetProperty("fileName").GetString()!;

        HttpResponseMessage restoreResp = await _client.PostAsJsonAsync("/api/system/restore", new { fileName });
        JsonElement body = await restoreResp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        body.GetProperty("data").GetProperty("requiresRestart").GetBoolean().Should().BeTrue();
        body.GetProperty("data").GetProperty("stagedPath").GetString().Should().Contain(".import-pending");

        File.Exists(Path.Combine(_factory.Paths.Root, "pmm.db" + ImportStaging.PendingSuffix))
            .Should().BeTrue("恢复应只暂存到 pmm.db.import-pending");
    }

    private Task LoginAsAdmin() => LoginAsAdmin(_client);

    private static async Task LoginAsAdmin(HttpClient client)
    {
        await client.PostAsJsonAsync("/api/setup/admin", new { username = "admin", password = "secret123" });
        await client.PostAsJsonAsync("/api/setup/complete", new { });
        HttpResponseMessage loginResp = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "secret123" });
        JsonElement login = await loginResp.Content.ReadFromJsonAsync<JsonElement>();
        string token = login.GetProperty("data").GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}
