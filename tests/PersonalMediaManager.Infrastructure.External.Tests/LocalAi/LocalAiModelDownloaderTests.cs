using System.Net;
using System.Security.Cryptography;
using System.Text;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.LocalAi;
using PersonalMediaManager.Infrastructure.External.LocalAi;

namespace PersonalMediaManager.Infrastructure.External.Tests.LocalAi;

public sealed class LocalAiModelDownloaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pmm-model-test-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Content = Encoding.UTF8.GetBytes("固定测试模型数据");
    private static LocalAiModelArtifact Artifact => new("test", "测试模型", "https://huggingface.co/test", "fixed", "test.gguf",
        new Uri("https://huggingface.co/official/resolve/fixed/test.gguf"), Content.Length, Convert.ToHexString(SHA256.HashData(Content)));

    [Fact]
    public async Task ValidDownload_IsPublishedAndRetryDoesNotOverwrite()
    {
        int calls = 0;
        using LocalAiModelDownloader downloader = Create((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Content) });
        });
        await downloader.DownloadAsync(Artifact, _ => { }, CancellationToken.None);
        (await File.ReadAllBytesAsync(downloader.PathFor(Artifact))).Should().Equal(Content);
        (await downloader.IsValidAsync(Artifact, CancellationToken.None)).Should().BeTrue();
        await downloader.DownloadAsync(Artifact, _ => { }, CancellationToken.None);
        calls.Should().Be(1);
        Directory.GetFiles(_directory, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public void HuihuiCatalog_PinsLocalConversionWithoutOfficialDownload()
    {
        LocalAiModelArtifact model = LocalAiModelCatalog.Get(LocalAiModelIds.Huihui);
        model.CanVerify.Should().BeTrue();
        model.CanDownload.Should().BeFalse();
        model.DownloadUri.Should().BeNull();
        model.SizeBytes.Should().Be(531068416);
        model.Sha256.Should().Be("15c5d19fd98774df4fa4df949e4f8cc7c8b32ff366895e23d69f9222b4082816");
        model.Revision.Should().Be("3dee99dac7c99318ed2b4e9932bfbbac060fb024");
        model.ConversionRevision.Should().Be("13b4d7135a6351f81e1eccf6361a4eafd50350eb");
        model.FileName.Should().Be("huihui-qwen2.5-0.5b-v3-q8_0.gguf");
        LocalAiModelCatalog.Get(LocalAiModelIds.Qwen).CanDownload.Should().BeTrue();
    }

    [Fact]
    public async Task LocalOnlyArtifact_IsVerifiedWithoutNetworkOrDownloadPermission()
    {
        LocalAiModelArtifact model = Artifact with { DownloadUri = null, UnavailableReason = "仅支持本地固定文件" };
        int calls = 0;
        using LocalAiModelDownloader downloader = Create((_, _) => { calls++; throw new InvalidOperationException(); });
        PrivateFileSystem.EnsureDirectory(_directory);
        await File.WriteAllBytesAsync(downloader.PathFor(model), Content);
        model.CanVerify.Should().BeTrue();
        model.CanDownload.Should().BeFalse();
        downloader.IsPresent(model).Should().BeTrue();
        (await downloader.IsValidAsync(model, CancellationToken.None)).Should().BeTrue();
        await Assert.ThrowsAsync<BusinessException>(() => downloader.DownloadAsync(model, _ => { }, CancellationToken.None));
        calls.Should().Be(0);
        (await File.ReadAllBytesAsync(downloader.PathFor(model))).Should().Equal(Content);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidLocalOnlyArtifact_PreservesItselfAndOtherModels(bool matchingSize)
    {
        LocalAiModelArtifact model = Artifact with { DownloadUri = null };
        using LocalAiModelDownloader downloader = Create((_, _) => throw new InvalidOperationException());
        PrivateFileSystem.EnsureDirectory(_directory);
        byte[] invalid = new byte[matchingSize ? Content.Length : Content.Length + 1];
        await File.WriteAllBytesAsync(downloader.PathFor(model), invalid);
        string other = Path.Combine(_directory, "other.gguf");
        await File.WriteAllBytesAsync(other, Content);
        downloader.IsPresent(model).Should().Be(matchingSize);
        (await downloader.IsValidAsync(model, CancellationToken.None)).Should().BeFalse();
        (await File.ReadAllBytesAsync(downloader.PathFor(model))).Should().Equal(invalid);
        (await File.ReadAllBytesAsync(other)).Should().Equal(Content);
    }

    [Fact]
    public async Task LocalOnlyArtifact_RejectsSymbolicLinkWithoutDeletingTarget()
    {
        if (OperatingSystem.IsWindows()) return;
        LocalAiModelArtifact model = Artifact with { DownloadUri = null };
        using LocalAiModelDownloader downloader = Create((_, _) => throw new InvalidOperationException());
        PrivateFileSystem.EnsureDirectory(_directory);
        string source = Path.Combine(_directory, "source.gguf");
        await File.WriteAllBytesAsync(source, Content);
        File.CreateSymbolicLink(downloader.PathFor(model), source);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => downloader.IsValidAsync(model, CancellationToken.None));
        (await File.ReadAllBytesAsync(source)).Should().Equal(Content);
    }

    [Fact]
    public async Task HashMismatch_CleansTemporaryFile_AndCanRetry()
    {
        int calls = 0;
        using LocalAiModelDownloader downloader = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(++calls == 1 ? new byte[Content.Length] : Content)
        }));
        await Assert.ThrowsAsync<BusinessException>(() => downloader.DownloadAsync(Artifact, _ => { }, CancellationToken.None));
        Directory.GetFiles(_directory).Should().BeEmpty();
        await downloader.DownloadAsync(Artifact, _ => { }, CancellationToken.None);
        (await downloader.IsValidAsync(Artifact, CancellationToken.None)).Should().BeTrue();
    }

    [Theory]
    [InlineData("https://evil.example/model.gguf")]
    [InlineData("http://huggingface.co/model.gguf")]
    [InlineData("https://huggingface.co:8443/model.gguf")]
    [InlineData("https://user:password@huggingface.co/model.gguf")]
    [InlineData("https://huggingface.co.evil.example/model.gguf")]
    public async Task RedirectOutsideExactAllowlist_IsRejectedBeforeFollowing(string location)
    {
        int calls = 0;
        using LocalAiModelDownloader downloader = Create((_, _) =>
        {
            calls++;
            HttpResponseMessage result = new(HttpStatusCode.Redirect);
            result.Headers.Location = new Uri(location);
            return Task.FromResult(result);
        });
        await Assert.ThrowsAsync<BusinessException>(() => downloader.DownloadAsync(Artifact, _ => { }, CancellationToken.None));
        calls.Should().Be(1);
        Directory.GetFiles(_directory).Should().BeEmpty();
    }

    [Fact]
    public async Task AllowedCdnRedirect_DownloadsWithoutCredentials()
    {
        int calls = 0;
        using LocalAiModelDownloader downloader = Create((request, _) =>
        {
            request.Headers.Authorization.Should().BeNull();
            if (++calls == 1)
            {
                HttpResponseMessage redirect = new(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri("https://cas-bridge.xethub.hf.co/blob");
                return Task.FromResult(redirect);
            }
            request.RequestUri!.Host.Should().Be("cas-bridge.xethub.hf.co");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Content) });
        });
        await downloader.DownloadAsync(Artifact, _ => { }, CancellationToken.None);
        calls.Should().Be(2);
    }

    [Fact]
    public async Task CancellationDuringCopy_CleansTemporaryFile()
    {
        using CancellationTokenSource cancellation = new();
        using LocalAiModelDownloader downloader = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(Content) }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloader.DownloadAsync(Artifact,
            _ => cancellation.Cancel(), cancellation.Token));
        Directory.GetFiles(_directory).Should().BeEmpty();
    }

    [Fact]
    public async Task InsufficientDisk_DoesNotSendRequest()
    {
        int calls = 0;
        using LocalAiModelDownloader downloader = Create((_, _) => { calls++; throw new InvalidOperationException(); }, 1);
        await Assert.ThrowsAsync<BusinessException>(() => downloader.DownloadAsync(Artifact, _ => { }, CancellationToken.None));
        calls.Should().Be(0);
    }

    [Fact]
    public async Task OversizeBody_IsRejectedAndCleaned()
    {
        using LocalAiModelDownloader downloader = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new MemoryStream(new byte[Content.Length + 1])) }));
        await Assert.ThrowsAsync<BusinessException>(() => downloader.DownloadAsync(Artifact, _ => { }, CancellationToken.None));
        Directory.GetFiles(_directory).Should().BeEmpty();
    }

    [Fact]
    public async Task ExistingInvalidFile_IsPreserved()
    {
        PrivateFileSystem.EnsureDirectory(_directory);
        string file = Path.Combine(_directory, Artifact.FileName);
        await File.WriteAllTextAsync(file, "已有文件");
        using LocalAiModelDownloader downloader = Create((_, _) => throw new InvalidOperationException());
        await Assert.ThrowsAsync<BusinessException>(() => downloader.DownloadAsync(Artifact, _ => { }, CancellationToken.None));
        (await File.ReadAllTextAsync(file)).Should().Be("已有文件");
    }

    private LocalAiModelDownloader Create(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response, long free = long.MaxValue)
        => new(new HttpClient(new Handler(response)), _directory, () => free);

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request, cancellationToken);
    }
}
