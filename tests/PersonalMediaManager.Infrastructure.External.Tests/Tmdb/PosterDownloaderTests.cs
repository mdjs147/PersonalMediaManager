using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Infrastructure.External.Tmdb;

namespace PersonalMediaManager.Infrastructure.External.Tests.Tmdb;

/// <summary>PosterDownloader：已存在跳过 / 不存在则下载 / 失败抛 TmdbClientException</summary>
public sealed class PosterDownloaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pmm-poster-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 清理失败不阻断 */ }
    }

    private static PosterDownloader NewDownloader(StubHttpMessageHandler handler)
    {
        StubHttpClientFactory factory = new(handler);
        return new PosterDownloader(factory, NullLogger<PosterDownloader>.Instance);
    }

    [Fact]
    public async Task Download_FileAlreadyExists_SkipsAndReturnsExistingBytes()
    {
        Directory.CreateDirectory(Path.Combine(_root, "posters"));
        string existing = Path.Combine(_root, "posters", "12345.jpg");
        await File.WriteAllBytesAsync(existing, new byte[] { 1, 2, 3, 4 });

        StubHttpMessageHandler h = new(); // 不入队任何响应，证明根本没发请求
        PosterDownloader d = NewDownloader(h);

        PosterDownloadResult res = await d.DownloadAsync(12345, "/x.jpg", _root);
        res.Downloaded.Should().BeFalse();
        res.LocalPath.Should().Be(existing);
        res.BytesWritten.Should().Be(4);
        h.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Download_New_WritesToCacheRoot()
    {
        byte[] payload = new byte[] { 9, 8, 7, 6, 5 };
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload),
        });

        PosterDownloader d = NewDownloader(h);
        PosterDownloadResult res = await d.DownloadAsync(999, "/p.jpg", _root);

        res.Downloaded.Should().BeTrue();
        res.LocalPath.Should().Be(Path.Combine(_root, "posters", "999.jpg"));
        File.ReadAllBytes(res.LocalPath).Should().BeEquivalentTo(payload);
        h.Requests[0].RequestUri!.AbsoluteUri.Should().Contain("/w500/p.jpg");
    }

    [Fact]
    public async Task Download_Non2xx_ThrowsTmdbClientException()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(HttpStatusCode.NotFound, "{}");

        PosterDownloader d = NewDownloader(h);
        Func<Task> act = () => d.DownloadAsync(1, "/p.jpg", _root);
        TmdbClientException ex = (await act.Should().ThrowAsync<TmdbClientException>()).Which;
        ex.HttpStatus.Should().Be(404);
    }

    [Fact(DisplayName = "HttpClient 超时抛 TaskCanceledException（ct 未取消）→ 源头翻译为 TmdbClientException，不伪装取消")]
    public async Task Download_Timeout_FakeCancellation_TranslatedTo_TmdbClientException()
    {
        StubHttpMessageHandler h = new();
        // 模拟 HttpClient 30s 超时：抛 TaskCanceledException 但调用方 ct 并未取消（伪装取消）
        h.EnqueueResponse(_ => throw new TaskCanceledException("模拟 30s 超时"));

        PosterDownloader d = NewDownloader(h);
        Func<Task> act = () => d.DownloadAsync(7, "/p.jpg", _root);

        TmdbClientException ex = (await act.Should().ThrowAsync<TmdbClientException>(
            "伪装取消必须在源头翻译为业务异常，避免上游误判为真取消")).Which;
        ex.Message.Should().Contain("超时");
        ex.InnerException.Should().BeOfType<TaskCanceledException>();
        File.Exists(Path.Combine(_root, "posters", "7.jpg")).Should().BeFalse("失败不得遗留缓存文件");
    }

    [Fact(DisplayName = "传输中断遗留半成品 → 失败时清理，不污染「已存在跳过」幂等判定")]
    public async Task Download_StreamInterrupted_PartialFile_IsCleanedUp()
    {
        StubHttpMessageHandler h = new();
        // 头部 200 成功、正文复制中途抛 IOException（模拟网络中断）：半成品已部分写入本地
        h.EnqueueResponse(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new InterruptedContent() });

        PosterDownloader d = NewDownloader(h);
        Func<Task> act = () => d.DownloadAsync(8, "/p.jpg", _root);

        // HttpContent.CopyToAsync 把内容流异常包装为 HttpRequestException（内层 IOException）
        HttpRequestException ex = (await act.Should().ThrowAsync<HttpRequestException>()).Which;
        ex.InnerException.Should().BeOfType<IOException>();
        File.Exists(Path.Combine(_root, "posters", "8.jpg")).Should().BeFalse(
            "半成品必须清理——否则下次按「文件已存在」跳过下载，损坏海报永不修复");
    }

    /// <summary>写出少量字节后抛 IOException 的内容体（模拟传输中断产生半成品）</summary>
    private sealed class InterruptedContent : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            await stream.WriteAsync(new byte[] { 1, 2, 3 });
            throw new IOException("模拟海报传输中断");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }
}
