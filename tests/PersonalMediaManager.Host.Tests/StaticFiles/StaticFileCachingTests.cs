using System.Net;
using Microsoft.Extensions.FileProviders;
using PersonalMediaManager.Host.Composition;
using PersonalMediaManager.Host.Middleware;

namespace PersonalMediaManager.Host.Tests.StaticFiles;

/// <summary>前端静态资源缓存分级：index.html no-cache、hash 资源 immutable（修复「发新版必须强刷」）</summary>
/// <remarks>
/// 根因：默认静态文件中间件不发 Cache-Control，浏览器对固定名 index.html 启用启发式缓存，
/// 普通刷新拿旧 index → 引用旧 hash 资源 → 看到旧页面。本测试守护 PmmHost 的缓存头分级策略。
/// </remarks>
public sealed class StaticFileCachingTests : IDisposable
{
    private readonly PmmHostFactory _factory;
    private readonly HttpClient _client;

    public StaticFileCachingTests()
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

    [Theory]
    [InlineData("/")]            // UseDefaultFiles 把 / 改写到 index.html
    [InlineData("/index.html")] // 直接命中
    [InlineData("/review")]     // 深链接刷新走 MapFallbackToFile 仍吐 index.html
    public async Task IndexHtml_IsNoCache(string path)
    {
        HttpResponseMessage resp = await _client.GetAsync(path);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Headers.CacheControl.Should().NotBeNull("index.html 必须显式带 Cache-Control，否则浏览器启发式缓存导致旧页面");
        resp.Headers.CacheControl!.NoCache.Should().BeTrue("入口文件须每次向服务器校验");
    }

    [Fact]
    public async Task HashedAsset_IsImmutableLongCache()
    {
        // 带内容 hash 的资源改名即换文件，可安全长缓存；hash 文件名随前端重建变化，
        // 故从嵌入程序集的 wwwroot/assets 动态挑一个 .js，避免硬编码文件名导致测试变脆。
        // wwwroot 已嵌入 Host 程序集（不再有外置物理 wwwroot 目录），用 ManifestEmbeddedFileProvider 读。
        ManifestEmbeddedFileProvider provider = new(typeof(PmmHost).Assembly, "wwwroot");
        string assetName = provider.GetDirectoryContents("assets")
            .First(f => !f.IsDirectory && f.Name.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            .Name;

        HttpResponseMessage resp = await _client.GetAsync($"/assets/{assetName}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Headers.CacheControl.Should().NotBeNull();
        resp.Headers.CacheControl!.MaxAge.Should().Be(TimeSpan.FromSeconds(31536000));
        resp.Headers.CacheControl.Public.Should().BeTrue();
        // immutable 是 Cache-Control 扩展指令，HttpClient 解析进 Extensions
        resp.Headers.CacheControl.Extensions.Should().Contain(e => e.Name == "immutable");
    }
}
