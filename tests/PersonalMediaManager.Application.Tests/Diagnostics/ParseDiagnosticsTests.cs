using System.Text;
using System.Text.Json;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Common.Diagnostics;
using PersonalMediaManager.Infrastructure.Platform.Diagnostics;

namespace PersonalMediaManager.Application.Tests.Diagnostics;

public sealed class ParseDiagnosticsTests
{
    private sealed class Sink(ParseDiagnosticLevel level = ParseDiagnosticLevel.Standard) : IParseDiagnosticSink
    {
        public ParseDiagnosticOptions Options { get; } = new() { Level = level, MaxTextUtf8Bytes = 256 };
        public List<ParseDiagnosticEvent> Events { get; } = [];
        public void Write(ParseDiagnosticEvent value) => Events.Add(value);
    }
    private sealed class BrokenSink : IParseDiagnosticSink
    {
        public ParseDiagnosticOptions Options { get; } = new();
        public void Write(ParseDiagnosticEvent value) => throw new IOException("磁盘不可写");
    }

    [Fact]
    public void Standard_RecordsFingerprintsButNotAiBody()
    {
        Sink sink = new();
        using (ParseDiagnostics.Begin("parse", sink: sink))
        {
            DiagnosticText body = ParseDiagnostics.CaptureText("实际提示词内容");
            body.State.Should().Be("not_recorded"); body.Text.Should().BeNull();
            body.Sha256.Should().HaveLength(64); body.OriginalUtf8Bytes.Should().Be(21);
            ParseDiagnostics.CaptureText(null).State.Should().Be("missing");
            ParseDiagnostics.UnknownText().State.Should().Be("unknown");
            ParseDiagnostics.CaptureText("Episode01.mkv", true).State.Should().Be("recorded");
        }
    }

    [Fact]
    public void Detailed_RecordsUnchangedJsonAndTruncatesOnUnicodeBoundaries()
    {
        using IDisposable scope = ParseDiagnostics.Begin("parse", sink: new Sink(ParseDiagnosticLevel.Detailed));
        string json = "{ \"title\": \"示例\", \"year\": null }";
        DiagnosticText body = ParseDiagnostics.CaptureText(json);
        body.Text.Should().Be(json); body.Redacted.Should().BeFalse();
        body = ParseDiagnostics.CaptureText(string.Concat(Enumerable.Repeat("剧😀", 100)), maxUtf8Bytes: 11);
        body.Truncated.Should().BeTrue(); body.CapturedUtf8Bytes.Should().BeLessThanOrEqualTo(11);
        body.OriginalUtf8Bytes.Should().Be(700); body.Text.Should().Be("剧😀剧");
    }

    [Theory]
    [InlineData("Authorization: Basic dXNlcjpwYXNz", "dXNlcjpwYXNz")]
    [InlineData("{\"cookie\":\"session=COOKIE_SECRET", "COOKIE_SECRET")]
    [InlineData("token: TOKEN_SECRET", "TOKEN_SECRET")]
    [InlineData("HTTP 500: {\"thinking\":{\"content\":\"PRIVATE", "PRIVATE")]
    [InlineData("{\"thinking\":{\"content\":\"PRIVATE", "PRIVATE")]
    [InlineData("https://user:pw@example.test/api?token=abc&signature=SECRET", "SECRET")]
    [InlineData("{\"api_key\":\"abcdefgh\",\"thinking\":\"PRIVATE\"}", "PRIVATE")]
    [InlineData("{\"nested\":\"{\\\"reasoning_content\\\":\\\"PRIVATE\\\"}\"}", "PRIVATE")]
    [InlineData("<think>PRIVATE</think>{\"title\":\"Hello\"}", "PRIVATE")]
    [InlineData("{\"reasoning_content\":\"PRIVATE", "PRIVATE")]
    [InlineData("/home/alice/private/media.mkv", "alice")]
    [InlineData("C:\\Users\\alice\\movie.mkv", "alice")]
    public void Privacy_RemovesSecretsPathsAndPrivateReasoning(string input, string excluded)
    {
        using IDisposable scope = ParseDiagnostics.Begin("parse", sink: new Sink(ParseDiagnosticLevel.Detailed));
        DiagnosticText body = ParseDiagnostics.CaptureText(input);
        body.Text.Should().NotContain(excluded); body.Redacted.Should().BeTrue();
    }

    [Fact]
    public void DuplicateJsonKeysFailClosedWithoutBreakingPipeline()
    {
        using IDisposable scope = ParseDiagnostics.Begin("parse", sink: new Sink(ParseDiagnosticLevel.Detailed));
        Action action = () => ParseDiagnostics.CaptureText("{\"token\":\"a\",\"token\":\"b\"}");
        action.Should().NotThrow();
        ParseDiagnostics.CaptureText("{\"token\":\"a\",\"token\":\"b\"}").Text.Should().NotContain("\"a\"");
    }

    [Fact]
    public async Task QueueCarriesScanIdButNewFilesHaveDifferentRuns()
    {
        Sink sink = new(); PendingFileQueue queue = new(); string scanId = new('a', 32);
        using (ParseDiagnostics.Begin("scan", scanId, sink: sink))
            await queue.EnqueueAsync(new("/media/01.mkv", 2, PendingFileSource.FullScan));
        PendingFileItem queued = await queue.Reader.ReadAsync();
        queued.ScanRunId.Should().Be(scanId); ParseDiagnostics.CurrentRunId.Should().BeNull();
        using (ParseDiagnostics.Begin("parse", sink: sink, scanRunId: queued.ScanRunId))
        {
            ParseDiagnostics.SetMediaItem(1); ParseDiagnostics.Emit("test.file");
            await Task.Yield(); ParseDiagnostics.Emit("test.after_await");
        }
        using (ParseDiagnostics.Begin("parse", sink: sink))
        { ParseDiagnostics.SetMediaItem(2); ParseDiagnostics.Emit("test.file"); }
        ParseDiagnosticEvent first = sink.Events.Single(e => e.Name == "test.file" && e.MediaItemId == 1);
        ParseDiagnosticEvent later = sink.Events.Single(e => e.Name == "test.after_await");
        first.RunId.Should().Be(later.RunId); first.ScanRunId.Should().Be(scanId);
        sink.Events.Single(e => e.Name == "test.file" && e.MediaItemId == 2).RunId.Should().NotBe(first.RunId);
        sink.Events.Single(e => e.Name == "test.file" && e.MediaItemId == 2).ScanRunId.Should().BeNull();
    }

    [Fact]
    public void BrokenWriterNeverBreaksCallerAndScopeRestores()
    {
        Action action = () => { using IDisposable scope = ParseDiagnostics.Begin("parse", sink: new BrokenSink()); ParseDiagnostics.Emit("event", new { safe = true }); };
        action.Should().NotThrow(); ParseDiagnostics.CurrentRunId.Should().BeNull();
    }

    [Fact]
    public async Task ConcurrentScopesDoNotMixMediaOrRun()
    {
        async Task<(string? Run, long? Media)> Observe(long id)
        {
            Sink sink = new(); using IDisposable scope = ParseDiagnostics.Begin("parse", mediaItemId: id, sink: sink);
            await Task.Delay(5); ParseDiagnostics.Emit("observed");
            ParseDiagnosticEvent e = sink.Events.Single(x => x.Name == "observed"); return (e.RunId, e.MediaItemId);
        }
        (string? Run, long? Media)[] results = await Task.WhenAll(Observe(7), Observe(8));
        results[0].Run.Should().NotBe(results[1].Run); results[0].Media.Should().Be(7); results[1].Media.Should().Be(8);
    }

    [Fact]
    public void RotationExportAndIncompleteCaptureAreExplicit()
    {
        string root = Path.Combine(Path.GetTempPath(), "pmm-diagnostics-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            ParseDiagnosticOptions options = new ParseDiagnosticOptions { MaxEventUtf8Bytes = 2048, MaxFileBytes = 4096, MaxTotalBytes = 8192, MaxFiles = 2 };
            ParseDiagnosticFileSink sink = new ParseDiagnosticFileSink(root, options);
            for (int i = 0; i < 30; i++)
                using (ParseDiagnostics.Begin("parse", mediaItemId: 5, sink: sink))
                    ParseDiagnostics.Emit("payload", new { value = new string('a', 4000) });
            Directory.GetFiles(root).Length.Should().BeLessThanOrEqualTo(2);
            Directory.GetFiles(root).Sum(x => new FileInfo(x).Length).Should().BeLessThanOrEqualTo(8192);
            ParseReplayExport export = sink.Export(null, 5);
            export.Events.Should().NotBeEmpty();
            JsonElement completeness = JsonSerializer.SerializeToElement(export.Completeness);
            completeness.GetProperty("completePipelineReplay").GetBoolean().Should().BeFalse();
            completeness.GetProperty("truncatedEvents").GetInt32().Should().BeGreaterThan(0);
            Action invalid = () => sink.Export("../other", null); invalid.Should().Throw<ArgumentException>();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void MediaExportIncludesEarlyEventsAndAllowsReconstructingRuleInput()
    {
        string root = Path.Combine(Path.GetTempPath(), "pmm-diagnostics-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            ParseDiagnosticFileSink sink = new ParseDiagnosticFileSink(root, new());
            using (ParseDiagnostics.Begin("parse", sink: sink))
            {
                ParseDiagnostics.SetMediaItem(42);
                ParseDiagnostics.Emit("parse.input", new { fileName = ParseDiagnostics.CaptureText("Show.S01E02.mkv", true), relativeSegments = new[] { ParseDiagnostics.CaptureText("Show (2024)", true) } });
                ParseDiagnostics.Emit("rule.result", new { title = "Show", season = 1, episode = 2 });
                ParseDiagnostics.Emit("parse.finished", new { outcome = "AwaitingReview" });
            }
            ParseReplayExport result = sink.Export(null, 42);
            result.Events.Should().Contain(e => e.Name == "operation.started");
            JsonSerializer.SerializeToElement(result.Completeness).GetProperty("ruleInputReplay").GetString().Should().Be("captured_inputs_available");
            using (ParseDiagnostics.Begin("parse", mediaItemId: 42, sink: sink)) ParseDiagnostics.Emit("parse.cancelled");
            JsonSerializer.SerializeToElement(sink.Export(null, 42).Completeness).GetProperty("ruleInputReplay").GetString().Should().Be("incomplete");
            JsonElement input = result.Events.Single(e => e.Name == "parse.input").Data;
            input.GetProperty("fileName").GetProperty("text").GetString().Should().Be("Show.S01E02.mkv");
            input.GetProperty("relativeSegments")[0].GetProperty("text").GetString().Should().Be("Show (2024)");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
