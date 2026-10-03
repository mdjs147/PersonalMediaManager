using System.Net;
using System.Text;
using System.Text.Json;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Common.Diagnostics;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Infrastructure.External.Ai;
using PersonalMediaManager.Infrastructure.Platform.Diagnostics;

namespace PersonalMediaManager.Infrastructure.External.Tests.Ai;

public sealed class FullAiResponseDiagnosticsTests
{
    [Fact]
    public async Task ActualMultilineEnvelopeSurvivesChunkedHttpReadWithoutReconstruction()
    {
        const string wire = "{\r\n \"choices\": [ { \"message\": { \"content\":\"hello 剧😀\" } } ],\r\n \"other\": 1.00\r\n}";
        await Check(wire, async (sink, run) =>
        {
            using HttpContent content = new StreamContent(new TinyChunks(Encoding.UTF8.GetBytes(wire)));
            (await AiResponseReader.ReadAsync(content, 4096, default, "a-secret", 200)).Should().Be(wire);
            ParseReplayExport export = sink.Export(run, null);
            export.Artifacts.Should().ContainSingle(a => a.Text == wire);
            ParseDiagnosticEvent e = export.Events.Single(e => e.Name == "ai.http_response");
            e.Data.GetProperty("bodyComplete").GetBoolean().Should().BeTrue();
            e.Data.GetProperty("httpStatus").GetInt32().Should().Be(200);
        });
    }

    [Fact]
    public async Task OverLimitPartialResponseIsExplicitAndStillThrowsLogicalFailure()
    {
        await Check("", async (sink, run) =>
        {
            using HttpContent content = new StreamContent(new TinyChunks(Encoding.UTF8.GetBytes(new string('字', 600))));
            Func<Task> action = () => AiResponseReader.ReadAsync(content, 1024, default, httpStatus: 200);
            await action.Should().ThrowAsync<AiProviderLogicalException>();
            ParseDiagnosticEvent e = sink.Export(run, null).Events.Single(e => e.Name == "ai.http_response");
            e.Data.GetProperty("bodyComplete").GetBoolean().Should().BeFalse();
            e.Data.GetProperty("reason").GetString().Should().Be("transport_byte_limit");
            e.Data.GetProperty("content").GetProperty("truncated").GetBoolean().Should().BeTrue();
        });
    }

    [Fact]
    public async Task Http429EnvelopeRedactsAuthAndPrivateReasoningAndKeepsStatus()
    {
        const string wire = "{\n\"error\":{\"code\":429,\"message\":\"exact-secret\"},\n\"reasoning\":\"private-content\",\"token\":\"unsafe\"}";
        await Check(wire, async (sink, run) =>
        {
            using HttpContent content = new StringContent(wire);
            await AiResponseReader.ReadAsync(content, 4096, default, "exact-secret", 429);
            ParseReplayExport export = sink.Export(run, null);
            export.Artifacts.Should().ContainSingle();
            export.Artifacts![0].Text.Should().NotContain("exact-secret").And.NotContain("private-content").And.NotContain("unsafe").And.Contain("\n\"error\"");
            export.Events.Single(e => e.Name == "ai.http_response").Data.GetProperty("httpStatus").GetInt32().Should().Be(429);
        });
    }

    [Fact]
    public async Task CancelledStreamIsStillCancellationWithExplicitPartialEvidence()
    {
        await Check("", async (sink, run) =>
        {
            using CancellationTokenSource cancellation = new();
            using HttpContent content = new StreamContent(new TinyChunks(Encoding.UTF8.GetBytes("hello"), cancellation));
            Func<Task> action = () => AiResponseReader.ReadAsync(content, 4096, cancellation.Token, httpStatus: 200);
            await action.Should().ThrowAsync<OperationCanceledException>();
            sink.Export(run, null).Events.Single(e => e.Name == "ai.http_response").Data.GetProperty("reason").GetString().Should().Be("transport_cancelled");
        });
    }

    [Theory]
    [InlineData("oversize", "artifact_size_limit", true, 0)]
    [InlineData("privacy", "privacy_filter_failed", false, 0)]
    [InlineData("write", "artifact_write_failed", false, 1)]
    public async Task CompleteTransportPreservesArtifactCaptureFailure(string scenario, string expectedReason, bool truncated, int writes)
    {
        string wire = scenario switch
        {
            "oversize" => new string('a', 2048),
            "privacy" => "{\"access_token\":\"synthetic-private-token",
            _ => "safe body",
        };
        FailedArtifactSink sink = new();
        using IDisposable scope = ParseDiagnostics.Begin("parse", sink: sink);
        using HttpContent content = new StringContent(wire);
        (await AiResponseReader.ReadAsync(content, 4096, default, httpStatus: 200)).Should().Be(wire);
        JsonElement evidence = sink.Events.Single(e => e.Name == "ai.http_response").Data;
        evidence.GetProperty("bodyComplete").GetBoolean().Should().BeTrue();
        evidence.GetProperty("reason").ValueKind.Should().Be(JsonValueKind.Null);
        JsonElement capture = evidence.GetProperty("content");
        capture.GetProperty("state").GetString().Should().Be("not_recorded");
        capture.GetProperty("reason").GetString().Should().Be(expectedReason);
        capture.GetProperty("truncated").GetBoolean().Should().Be(truncated);
        sink.ArtifactWrites.Should().Be(writes);
        JsonSerializer.Serialize(sink.Events).Should().NotContain("synthetic-private-token");
    }

    [Fact]
    public async Task PartialTransportKeepsItsReasonSeparateFromArtifactWriteFailure()
    {
        FailedArtifactSink sink = new();
        using IDisposable scope = ParseDiagnostics.Begin("parse", sink: sink);
        using HttpContent content = new StreamContent(new TinyChunks(Encoding.UTF8.GetBytes(new string('a', 2048))));
        Func<Task> action = () => AiResponseReader.ReadAsync(content, 1024, default, httpStatus: 200);
        await action.Should().ThrowAsync<AiProviderLogicalException>();
        JsonElement evidence = sink.Events.Single(e => e.Name == "ai.http_response").Data;
        evidence.GetProperty("bodyComplete").GetBoolean().Should().BeFalse();
        evidence.GetProperty("reason").GetString().Should().Be("transport_byte_limit");
        evidence.GetProperty("content").GetProperty("reason").GetString().Should().Be("artifact_write_failed");
        evidence.GetProperty("content").GetProperty("truncated").GetBoolean().Should().BeTrue();
        sink.ArtifactWrites.Should().Be(1);
    }

    [Fact]
    public async Task TransportAboveDefaultArtifactLimitIsCompleteButExplicitlyNotRecorded()
    {
        string wire = new('x', 2 * 1024 * 1024 + 1);
        await Check(wire, async (sink, run) =>
        {
            using HttpContent content = new StringContent(wire);
            (await AiResponseReader.ReadAsync(content, 4 * 1024 * 1024, default, httpStatus: 200)).Should().Be(wire);
            ParseReplayExport export = sink.Export(run, null);
            export.Artifacts.Should().BeEmpty();
            JsonElement evidence = export.Events.Single(e => e.Name == "ai.http_response").Data;
            evidence.GetProperty("bodyComplete").GetBoolean().Should().BeTrue();
            JsonElement capture = evidence.GetProperty("content");
            capture.GetProperty("state").GetString().Should().Be("not_recorded");
            capture.GetProperty("reason").GetString().Should().Be("artifact_size_limit");
            capture.GetProperty("truncated").GetBoolean().Should().BeTrue();
            capture.GetProperty("text").ValueKind.Should().Be(JsonValueKind.Null);
        });
    }

    private sealed class FailedArtifactSink : IParseDiagnosticSink, IParseDiagnosticArtifactSink
    {
        public ParseDiagnosticOptions Options { get; } = new() { Level = ParseDiagnosticLevel.Full, MaxArtifactUtf8Bytes = 1024 };
        public List<ParseDiagnosticEvent> Events { get; } = [];
        public int ArtifactWrites { get; private set; }
        public void Write(ParseDiagnosticEvent value) => Events.Add(value);
        public DiagnosticText StoreArtifact(DiagnosticText metadata, string redactedText)
        {
            ArtifactWrites++;
            return metadata with { State = "not_recorded", Reason = "artifact_write_failed" };
        }
    }

    private static async Task Check(string _, Func<ParseDiagnosticFileSink, string, Task> test)
    {
        string root = PrivateFileSystem.CreateTemporaryDirectory("pmm-wire-diag-");
        try
        {
            using ParseDiagnosticFileSink sink = new(root, new() { Level = ParseDiagnosticLevel.Full });
            string run = Guid.NewGuid().ToString("N");
            using (ParseDiagnostics.Begin("parse", run, 1, sink)) await test(sink, run);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class TinyChunks(byte[] bytes, CancellationTokenSource? cancel = null) : Stream
    {
        private int _offset;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _offset; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_offset > 0 && cancel is not null) { cancel.Cancel(); cancellationToken.ThrowIfCancellationRequested(); }
            int count = Math.Min(3, Math.Min(buffer.Length, bytes.Length - _offset));
            bytes.AsMemory(_offset, count).CopyTo(buffer); _offset += count; return ValueTask.FromResult(count);
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
