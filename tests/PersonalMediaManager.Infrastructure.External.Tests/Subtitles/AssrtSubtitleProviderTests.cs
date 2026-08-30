using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Infrastructure.External.Subtitles;
using PersonalMediaManager.Infrastructure.External.Tests.Tmdb;
using PersonalMediaManager.Infrastructure.External.Tmdb;

namespace PersonalMediaManager.Infrastructure.External.Tests.Subtitles;

/// <summary>AssrtSubtitleProvider：搜索解析 + status 错误 + 文件清单展开/回退 + 下载安全阀 + 连通性归集 + Bearer 注入</summary>
public sealed class AssrtSubtitleProviderTests
{
    private const string SearchJson = """
        {
          "status": 0,
          "sub": {
            "action": "search",
            "subs": [
              {
                "id": 618925,
                "native_name": "权力的游戏 第八季",
                "videoname": "Game.of.Thrones.S08E01",
                "release_site": "衣柜字幕组",
                "lang": { "desc": "简体" },
                "subtype": "ass",
                "upload_time": "2019-04-15 10:00:00",
                "vote_score": 4.5
              },
              {
                "id": 618926,
                "native_name": "",
                "videoname": "GoT.S08E02",
                "lang": { "desc": "双语" },
                "subtype": "srt"
              }
            ]
          }
        }
        """;

    private const string DetailWithFileListJson = """
        {
          "status": 0,
          "sub": {
            "subs": [
              {
                "id": 618925,
                "filename": "GoT.S08.zip",
                "url": "https://dl.assrt.net/download/618925",
                "size": 123456,
                "lang": { "desc": "简体&英文" },
                "filelist": [
                  { "f": "GoT.S08E01.chs.ass", "s": "164KB", "url": "https://dl.assrt.net/1" },
                  { "f": "GoT.S08E02.chs.ass", "s": "1.2MB", "url": "https://dl.assrt.net/2" },
                  { "f": "GoT.S08E03.chs.ass", "s": "不可解析", "url": "https://dl.assrt.net/3" }
                ]
              }
            ]
          }
        }
        """;

    private const string DetailTopFileOnlyJson = """
        {
          "status": 0,
          "sub": {
            "subs": [
              {
                "id": 700001,
                "filename": "movie.chs.srt",
                "url": "https://dl.assrt.net/top/700001",
                "size": 52428,
                "lang": { "desc": "简体" },
                "filelist": []
              }
            ]
          }
        }
        """;

    private static SubtitleProviderEndpoint Ep(int timeoutSeconds = 30) =>
        new("token-123", "https://api.assrt.net", timeoutSeconds, UseProxy: false);

    /// <summary>构造测试客户端（限流器换成 1000 token/50ms 等价不节流，单测秒过）</summary>
    private static AssrtSubtitleProvider NewProvider(StubHttpMessageHandler handler) =>
        new(new StubHttpClientFactory(handler), NullLogger<AssrtSubtitleProvider>.Instance,
            new TokenBucketRateLimiter(1000, TimeSpan.FromMilliseconds(50)));

    // ── SearchAsync ──

    [Fact]
    public async Task Search_NormalJson_MapsAllFields()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(HttpStatusCode.OK, SearchJson);

        AssrtSubtitleProvider provider = NewProvider(h);
        IReadOnlyList<SubtitleSearchEntry> entries = await provider.SearchAsync("权力的游戏", Ep());

        entries.Should().HaveCount(2);
        entries[0].SubtitleId.Should().Be("618925");
        entries[0].Name.Should().Be("权力的游戏 第八季");
        entries[0].VideoName.Should().Be("Game.of.Thrones.S08E01");
        entries[0].LanguageHint.Should().Be("简体");
        entries[0].Format.Should().Be("ass");
        entries[0].UploadTime.Should().Be("2019-04-15 10:00:00");
        entries[0].VoteScore.Should().Be(4.5);
        entries[1].Name.Should().Be("GoT.S08E02", "native_name 空时回退 videoname");
        entries[1].VoteScore.Should().BeNull("vote_score 缺失容忍为 null");

        h.Requests[0].RequestUri!.AbsoluteUri.Should()
            .Contain("/v1/sub/search").And.Contain("q=").And.Contain("cnt=15").And.Contain("pos=0");
    }

    [Fact]
    public async Task Search_StatusNonZero_ThrowsWithErrMsg()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(HttpStatusCode.OK, """{"status":30900,"errmsg":"token invalid"}""");

        AssrtSubtitleProvider provider = NewProvider(h);
        Func<Task> act = () => provider.SearchAsync("x", Ep());

        SubtitleProviderException ex = (await act.Should().ThrowAsync<SubtitleProviderException>()).Which;
        ex.Message.Should().Contain("30900").And.Contain("token invalid");
    }

    [Fact]
    public async Task Search_SubsNull_ReturnsEmpty()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(HttpStatusCode.OK, """{"status":0,"sub":{"subs":null}}""");

        AssrtSubtitleProvider provider = NewProvider(h);
        IReadOnlyList<SubtitleSearchEntry> entries = await provider.SearchAsync("x", Ep());
        entries.Should().BeEmpty();
    }

    // ── GetFilesAsync ──

    [Fact]
    public async Task GetFiles_FileListExpanded_Index0ToN()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(HttpStatusCode.OK, DetailWithFileListJson);

        AssrtSubtitleProvider provider = NewProvider(h);
        SubtitleDetailEntry detail = await provider.GetFilesAsync("618925", Ep());

        detail.SubtitleId.Should().Be("618925");
        detail.LanguageHint.Should().Be("简体&英文");
        detail.Files.Should().HaveCount(3);
        detail.Files.Select(f => f.Index).Should().Equal(0, 1, 2);
        detail.Files[0].FileName.Should().Be("GoT.S08E01.chs.ass");
        detail.Files[0].Url.Should().Be("https://dl.assrt.net/1");
        detail.Files[0].SizeBytes.Should().Be(164 * 1024);
        detail.Files[1].SizeBytes.Should().Be((long)Math.Round(1.2 * 1024 * 1024));
        detail.Files[2].SizeBytes.Should().BeNull("大小描述串解析不了置 null");
        h.Requests[0].RequestUri!.AbsoluteUri.Should().Contain("/v1/sub/detail").And.Contain("id=618925");
    }

    [Fact]
    public async Task GetFiles_EmptyFileListWithTopUrl_SingleEntryIndexMinus1()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(HttpStatusCode.OK, DetailTopFileOnlyJson);

        AssrtSubtitleProvider provider = NewProvider(h);
        SubtitleDetailEntry detail = await provider.GetFilesAsync("700001", Ep());

        detail.Files.Should().ContainSingle();
        detail.Files[0].Index.Should().Be(-1, "顶层单文件按 -1 约定");
        detail.Files[0].FileName.Should().Be("movie.chs.srt");
        detail.Files[0].Url.Should().Be("https://dl.assrt.net/top/700001");
        detail.Files[0].SizeBytes.Should().Be(52428);
    }

    [Fact]
    public async Task GetFiles_NoSubs_ThrowsNotFound()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(HttpStatusCode.OK, """{"status":0,"sub":{"subs":[]}}""");

        AssrtSubtitleProvider provider = NewProvider(h);
        Func<Task> act = () => provider.GetFilesAsync("999", Ep());

        SubtitleProviderException ex = (await act.Should().ThrowAsync<SubtitleProviderException>()).Which;
        ex.Message.Should().Contain("不存在或已删除");
    }

    // ── DownloadAsync 安全阀 ──

    [Fact]
    public async Task Download_ContentLengthOver10Mb_Throws()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(_ =>
        {
            HttpResponseMessage resp = new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[1]) };
            // 仅伪造声明长度超限，不真实分配 10MB —— 命中「安全阀一」时根本不读 body
            resp.Content.Headers.ContentLength = AssrtSubtitleProvider.MaxDownloadBytes + 1;
            return resp;
        });

        AssrtSubtitleProvider provider = NewProvider(h);
        Func<Task> act = () => provider.DownloadAsync("https://dl.assrt.net/1", Ep());

        SubtitleProviderException ex = (await act.Should().ThrowAsync<SubtitleProviderException>()).Which;
        ex.Message.Should().Contain("过大");
    }

    [Fact]
    public async Task Download_StreamOver10MbWithoutContentLength_Throws()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            // 无 Content-Length 的 chunked 形态：靠「安全阀二」边读边计数拦截
            Content = new UnknownLengthContent(AssrtSubtitleProvider.MaxDownloadBytes + 1),
        });

        AssrtSubtitleProvider provider = NewProvider(h);
        Func<Task> act = () => provider.DownloadAsync("https://dl.assrt.net/1", Ep());

        SubtitleProviderException ex = (await act.Should().ThrowAsync<SubtitleProviderException>()).Which;
        ex.Message.Should().Contain("过大");
    }

    [Fact]
    public async Task Download_EmptyContent_Throws()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Array.Empty<byte>()),
        });

        AssrtSubtitleProvider provider = NewProvider(h);
        Func<Task> act = () => provider.DownloadAsync("https://dl.assrt.net/1", Ep());

        SubtitleProviderException ex = (await act.Should().ThrowAsync<SubtitleProviderException>()).Which;
        ex.Message.Should().Contain("内容为空");
    }

    [Fact]
    public async Task Download_Normal_ReturnsBytes_WithoutAuthorizationHeader()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 1, 2, 3 }),
        });

        AssrtSubtitleProvider provider = NewProvider(h);
        byte[] data = await provider.DownloadAsync("https://dl.assrt.net/1", Ep());

        data.Should().Equal(new byte[] { 1, 2, 3 });
        h.Requests[0].Headers.Authorization.Should().BeNull("直链下载不得携带 token（防外泄第三方分发主机）");
    }

    // ── TestAsync 归集不抛 ──

    [Fact]
    public async Task Test_NetworkException_ReturnsFailureNotThrow()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(_ => throw new HttpRequestException("连接被重置"));

        AssrtSubtitleProvider provider = NewProvider(h);
        SubtitleProviderTestResult result = await provider.TestAsync(Ep());

        result.Success.Should().BeFalse();
        result.Quota.Should().BeNull();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Test_StatusNonZero_ReturnsFailureNotThrow()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(HttpStatusCode.OK, """{"status":30300,"errmsg":"quota exceeded"}""");

        AssrtSubtitleProvider provider = NewProvider(h);
        SubtitleProviderTestResult result = await provider.TestAsync(Ep());

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("30300").And.Contain("quota exceeded");
    }

    [Fact]
    public async Task Test_Ok_ReturnsQuota()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(HttpStatusCode.OK, """{"status":0,"user":{"quota":42}}""");

        AssrtSubtitleProvider provider = NewProvider(h);
        SubtitleProviderTestResult result = await provider.TestAsync(Ep());

        result.Success.Should().BeTrue();
        result.Quota.Should().Be(42);
        result.ErrorMessage.Should().BeNull();
        h.Requests[0].RequestUri!.AbsoluteUri.Should().Contain("/v1/user/quota");
    }

    // ── 请求头 ──

    [Fact]
    public async Task ApiRequest_AuthorizationBearer_Injected()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(HttpStatusCode.OK, SearchJson);

        AssrtSubtitleProvider provider = NewProvider(h);
        await provider.SearchAsync("x", Ep());

        h.Requests[0].Headers.Authorization.Should().NotBeNull();
        h.Requests[0].Headers.Authorization!.Scheme.Should().Be("Bearer");
        h.Requests[0].Headers.Authorization!.Parameter.Should().Be("token-123");
        h.Requests[0].RequestUri!.Query.Should().NotContain("token", "token 不得进 query（防日志泄漏）");
    }

    /// <summary>不上报 Content-Length 的流式内容（模拟 chunked 响应，触发流式读安全阀）</summary>
    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly long _totalBytes;

        public UnknownLengthContent(long totalBytes) => _totalBytes = totalBytes;

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            byte[] chunk = new byte[81920];
            long written = 0;
            while (written < _totalBytes)
            {
                int take = (int)Math.Min(chunk.Length, _totalBytes - written);
                await stream.WriteAsync(chunk.AsMemory(0, take));
                written += take;
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }
}
