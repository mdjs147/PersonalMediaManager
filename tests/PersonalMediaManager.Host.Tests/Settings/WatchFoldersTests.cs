using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Host.Middleware;
using PersonalMediaManager.Infrastructure.Persistence;

namespace PersonalMediaManager.Host.Tests.Settings;

/// <summary>WatchFolders e2e：CRUD 闭环 + /test 端点 + 删除前 in-flight Media_Item 校验</summary>
public sealed class WatchFoldersTests : IDisposable
{
    private readonly PmmHostFactory _factory;
    private readonly HttpClient _client;
    private readonly string _tmpDir;

    public WatchFoldersTests()
    {
        SetupGuardMiddleware.ResetCacheForTest();
        _factory = new PmmHostFactory();
        _client = _factory.CreateClient();
        _tmpDir = Path.Combine(Path.GetTempPath(), $"pmm-watch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
        File.WriteAllText(Path.Combine(_tmpDir, "sample.mkv"), "stub");
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        SetupGuardMiddleware.ResetCacheForTest();
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* 测试目录清理失败不阻断 */ }
    }

    [Fact]
    public async Task Crud_And_Test_Endpoint_RoundTrip()
    {
        await LoginAsAdminAsync();

        // Create
        JsonElement created = await PostAsync("/api/settings/watch/folders", new
        {
            path = _tmpDir,
            alias = "测试目录",
            isTransit = true,
            isNetworkShare = false,
            enabled = true,
            priority = 50,
        });
        created.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        long id = created.GetProperty("data").GetProperty("id").GetInt64();
        long rowVersion = created.GetProperty("data").GetProperty("rowVersion").GetInt64();
        id.Should().BeGreaterThan(0);

        // List 应包含
        JsonElement list = await GetAsync("/api/settings/watch/folders");
        list.GetProperty("data").EnumerateArray()
            .Any(e => e.GetProperty("id").GetInt64() == id).Should().BeTrue();

        // Update：改 alias 与 priority（携带 rowVersion 完成乐观锁校验）
        JsonElement upd = await PostAsync("/api/settings/watch/folders/update", new
        {
            id,
            path = _tmpDir,
            alias = "改后名",
            isTransit = false,
            isNetworkShare = false,
            enabled = true,
            priority = 80,
            rowVersion,
        });
        upd.GetProperty("data").GetProperty("alias").GetString().Should().Be("改后名");
        upd.GetProperty("data").GetProperty("priority").GetInt32().Should().Be(80);
        upd.GetProperty("data").GetProperty("isTransit").GetBoolean().Should().BeFalse();
        upd.GetProperty("data").GetProperty("rowVersion").GetInt64().Should().BeGreaterThan(rowVersion);

        // /test 端点：目录存在且能读到至少 1 个 entry
        JsonElement test = await GetAsync($"/api/settings/watch/folders/{id}/test");
        test.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        test.GetProperty("data").GetProperty("reachable").GetBoolean().Should().BeTrue();
        test.GetProperty("data").GetProperty("directoryExists").GetBoolean().Should().BeTrue();
        test.GetProperty("data").GetProperty("sampleEntry").GetString().Should().NotBeNullOrEmpty();

        // Delete 成功
        JsonElement del = await PostAsync("/api/settings/watch/folders/delete", new { id });
        del.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);

        // 再 List 不含此 id
        JsonElement listAfter = await GetAsync("/api/settings/watch/folders");
        listAfter.GetProperty("data").EnumerateArray()
            .Any(e => e.GetProperty("id").GetInt64() == id).Should().BeFalse();
    }

    [Fact]
    public async Task AutoScan_Defaults_True_And_RoundTrips_Through_Update()
    {
        await LoginAsAdminAsync();

        // 创建时不传 autoScan → 默认 true（存量/新建目录默认保持自动监控）
        JsonElement created = await PostAsync("/api/settings/watch/folders", new
        {
            path = _tmpDir,
            isTransit = false,
            isNetworkShare = false,
        });
        created.GetProperty("data").GetProperty("autoScan").GetBoolean()
            .Should().BeTrue("未传 autoScan 时应默认开启自动监控");
        long id = created.GetProperty("data").GetProperty("id").GetInt64();
        long rowVersion = created.GetProperty("data").GetProperty("rowVersion").GetInt64();

        // 更新关闭 autoScan
        JsonElement upd = await PostAsync("/api/settings/watch/folders/update", new
        {
            id,
            path = _tmpDir,
            alias = (string?)null,
            isTransit = false,
            isNetworkShare = false,
            enabled = true,
            autoScan = false,
            priority = 100,
            rowVersion,
        });
        upd.GetProperty("data").GetProperty("autoScan").GetBoolean()
            .Should().BeFalse("更新应把 autoScan 落库为 false");

        // List 回显 false
        JsonElement list = await GetAsync("/api/settings/watch/folders");
        JsonElement row = list.GetProperty("data").EnumerateArray().Single(e => e.GetProperty("id").GetInt64() == id);
        row.GetProperty("autoScan").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task List_Aggregates_MediaCount_ByPathPrefix()
    {
        await LoginAsAdminAsync();
        JsonElement created = await PostAsync("/api/settings/watch/folders", new
        {
            path = _tmpDir,
            isTransit = false,
            isNetworkShare = false,
        });
        long id = created.GetProperty("data").GetProperty("id").GetInt64();

        // 直接 seed：3 条在该目录下、1 条在别的目录
        IDbContextFactory<PmmDbContext> dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<PmmDbContext>>();
        string sep = _tmpDir.Contains('\\') ? "\\" : "/";
        await using (PmmDbContext ctx = await dbFactory.CreateDbContextAsync())
        {
            ctx.MediaItems.Add(MediaItem.CreateDetected($"{_tmpDir}{sep}a.mkv", "a.mkv", 1));
            ctx.MediaItems.Add(MediaItem.CreateDetected($"{_tmpDir}{sep}sub{sep}b.mkv", "b.mkv", 1));
            ctx.MediaItems.Add(MediaItem.CreateDetected($"{_tmpDir}{sep}c.mkv", "c.mkv", 1));
            ctx.MediaItems.Add(MediaItem.CreateDetected($"{Path.GetTempPath()}other-folder{sep}z.mkv", "z.mkv", 1));
            await ctx.SaveChangesAsync();
        }

        JsonElement list = await GetAsync("/api/settings/watch/folders");
        JsonElement row = list.GetProperty("data").EnumerateArray().Single(e => e.GetProperty("id").GetInt64() == id);
        row.GetProperty("mediaCount").GetInt32().Should().Be(3, "仅 _tmpDir 路径前缀下的 3 条计入");
    }

    [Fact]
    public async Task Create_DuplicatePath_Returns1000()
    {
        await LoginAsAdminAsync();
        await PostAsync("/api/settings/watch/folders", new { path = _tmpDir, isTransit = false, isNetworkShare = false });

        JsonElement dup = await PostAsync("/api/settings/watch/folders", new { path = _tmpDir, isTransit = false, isNetworkShare = false });
        dup.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        dup.GetProperty("message").GetString().Should().Contain("已存在");
    }

    [Fact]
    public async Task Delete_WithInFlightMediaItem_Returns1000()
    {
        await LoginAsAdminAsync();
        JsonElement created = await PostAsync("/api/settings/watch/folders", new
        {
            path = _tmpDir,
            isTransit = false,
            isNetworkShare = false,
        });
        long id = created.GetProperty("data").GetProperty("id").GetInt64();

        // 通过 DI 直接写入一条 in-flight Media_Item，SourcePath 在该目录下
        IDbContextFactory<PmmDbContext> dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<PmmDbContext>>();
        await using (PmmDbContext ctx = await dbFactory.CreateDbContextAsync())
        {
            string sep = _tmpDir.Contains('\\') ? "\\" : "/";
            MediaItem item = MediaItem.CreateDetected(
                sourcePath: $"{_tmpDir}{sep}movie.mkv",
                fileName: "movie.mkv",
                fileSize: 1234);
            ctx.MediaItems.Add(item);
            await ctx.SaveChangesAsync();
        }

        JsonElement del = await PostAsync("/api/settings/watch/folders/delete", new { id });
        del.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        del.GetProperty("message").GetString().Should().Contain("未完成");
    }

    /// <summary>r3 第一波：并发冲突场景——B 端持旧 rowVersion 提交应被服务端 1000 拒绝</summary>
    [Fact]
    public async Task ConcurrentUpdate_StaleRowVersion_Returns_1000()
    {
        await LoginAsAdminAsync();
        JsonElement created = await PostAsync("/api/settings/watch/folders", new
        {
            path = _tmpDir,
            alias = "并发样本",
            isTransit = false,
            isNetworkShare = false,
            enabled = true,
            priority = 100,
        });
        long id = created.GetProperty("data").GetProperty("id").GetInt64();
        long staleVersion = created.GetProperty("data").GetProperty("rowVersion").GetInt64();

        // A 先以正确 rowVersion 更新
        JsonElement firstUpd = await PostAsync("/api/settings/watch/folders/update", new
        {
            id, path = _tmpDir, alias = "A 改",
            isTransit = false, isNetworkShare = false, enabled = true, priority = 100,
            rowVersion = staleVersion,
        });
        firstUpd.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);

        // B 持旧 rowVersion 仍尝试提交 → 1000
        JsonElement secondUpd = await PostAsync("/api/settings/watch/folders/update", new
        {
            id, path = _tmpDir, alias = "B 后到",
            isTransit = false, isNetworkShare = false, enabled = true, priority = 100,
            rowVersion = staleVersion,
        });
        secondUpd.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        secondUpd.GetProperty("message").GetString().Should().Contain("请刷新");
    }

    [Fact]
    public async Task Test_NonExistentDirectory_ReturnsReachableFalse()
    {
        await LoginAsAdminAsync();
        string missing = Path.Combine(Path.GetTempPath(), $"pmm-missing-{Guid.NewGuid():N}");
        JsonElement created = await PostAsync("/api/settings/watch/folders", new
        {
            path = missing,
            isTransit = false,
            isNetworkShare = false,
        });
        long id = created.GetProperty("data").GetProperty("id").GetInt64();

        JsonElement test = await GetAsync($"/api/settings/watch/folders/{id}/test");
        test.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        test.GetProperty("data").GetProperty("reachable").GetBoolean().Should().BeFalse();
        test.GetProperty("data").GetProperty("directoryExists").GetBoolean().Should().BeFalse();
        test.GetProperty("data").GetProperty("errorMessage").GetString().Should().NotBeNullOrEmpty();
    }

    /// <summary>档 2 边界校验：路径纯空白 → [ApiController] 模型校验拦截，返 1000 + 字段清单（不进 service）</summary>
    [Fact]
    public async Task Create_BlankPath_RejectedByBoundaryValidation_Returns1000()
    {
        await LoginAsAdminAsync();
        JsonElement resp = await PostAsync("/api/settings/watch/folders", new
        {
            path = "   ",
            isTransit = false,
            isNetworkShare = false,
        });
        resp.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        resp.GetProperty("message").GetString().Should().Contain("路径不能为空");
        // data 携带结构化字段错误（额外收益：前端可按字段定位）
        resp.GetProperty("data").EnumerateArray().Should().NotBeEmpty();
    }

    /// <summary>档 2 边界校验：Priority 越界 → 模型校验拦截，返 1000</summary>
    [Fact]
    public async Task Create_PriorityOutOfRange_RejectedByBoundaryValidation_Returns1000()
    {
        await LoginAsAdminAsync();
        JsonElement resp = await PostAsync("/api/settings/watch/folders", new
        {
            path = _tmpDir,
            isTransit = false,
            isNetworkShare = false,
            priority = 100000,
        });
        resp.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        resp.GetProperty("message").GetString().Should().Contain("Priority");
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
