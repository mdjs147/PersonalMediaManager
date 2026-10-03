using Microsoft.Extensions.Logging;
using NSubstitute;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Common.Diagnostics;
using PersonalMediaManager.Application.Dtos.Review;
using PersonalMediaManager.Application.Services.Library;
using PersonalMediaManager.Infrastructure.Persistence.Services.Review;
using PersonalMediaManager.Infrastructure.Platform.Diagnostics;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

public sealed partial class ReviewServiceTests
{
    [Theory]
    [InlineData(ParseDiagnosticLevel.Full)]
    [InlineData(ParseDiagnosticLevel.Standard)]
    public async Task Metadata_RefreshEntryCreatesOwnScopeAndHonorsCaptureLevel(ParseDiagnosticLevel level)
    {
        const string raw = "合成故障\r\n伪造日志：OK\t\u001b[31m";
        long id = SeedAssistItem();
        MockCompleteCatalogue();
        IWorkEnrichmentService enrichment = Substitute.For<IWorkEnrichmentService>();
        enrichment.EnrichAsync(101, "tv", true, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(new IOException(raw)));
        MetadataLogCapture<ReviewService> logger = new();
        string root = PrivateFileSystem.CreateTemporaryDirectory("pmm-log-safety-");
        try
        {
            using ParseDiagnosticFileSink sink = new(root, new() { Level = level });
            ReviewService service = new(_dbFactory, _tmdb, _archive, _fileProbe, _folderCache, _webhook,
                logger, diagnostics: sink, enrichment: enrichment);
            ParseDiagnostics.CurrentRunId.Should().BeNull();
            TmdbDetailItem result = await service.TmdbDetailAsync(id, new(101, "tv", 2, true));
            result.RefreshError.Should().Be("媒体库元数据刷新失败，已保留已有资料，可稍后重试");
            ParseDiagnostics.CurrentRunId.Should().BeNull();
            logger.Entries.Should().ContainSingle().Which.Exception.Should().BeNull();
            logger.Entries[0].Message.Should().Contain("TmdbId=101 Type=tv Season=2 Code=LibraryRefreshFailed");
            logger.AssertSingleLineWithout("伪造日志");
            ParseReplayExport exported = sink.Export(null, id);
            exported.Events.Should().NotBeEmpty().And.OnlyContain(e => e.MediaItemId == id && e.Operation == "manual_metadata_refresh");
            exported.Events.Select(e => e.RunId).Distinct().Should().ContainSingle();
            exported.Events.Should().Contain(e => e.Name == "operation.started").And.Contain(e => e.Name == "operation.ended");
            ParseDiagnosticEvent failed = exported.Events.Single(e => e.Name == "manual.metadata_refresh_failed");
            failed.Data.GetProperty("untrusted").GetBoolean().Should().BeTrue();
            if (level == ParseDiagnosticLevel.Full)
                exported.Artifacts.Should().ContainSingle(a => a.State == "recorded" && a.Text == raw);
            else
            {
                exported.Artifacts.Should().BeEmpty();
                failed.Data.GetProperty("error").GetProperty("state").GetString().Should().Be("not_recorded");
                failed.Data.GetProperty("error").GetProperty("capturedUtf8Bytes").GetInt32().Should().Be(0);
                failed.Data.GetRawText().Should().NotContain("伪造日志");
            }
        }
        finally { Directory.Delete(root, true); }
    }
}

/// <summary>保存普通日志的实际渲染文本与异常参数</summary>
internal sealed class MetadataLogCapture<T> : ILogger<T>
{
    internal sealed record Entry(string Message, Exception? Exception);
    public List<Entry> Entries { get; } = [];
    public bool IsEnabled(LogLevel logLevel) => true;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => Entries.Add(new(formatter(state, exception), exception));

    public void AssertSingleLineWithout(string forbidden)
    {
        Entries.Should().NotBeEmpty().And.OnlyContain(e => !e.Message.Contains(forbidden)
            && e.Message.All(c => !char.IsControl(c) && c != '\u2028' && c != '\u2029'));
    }
}
