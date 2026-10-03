using System.Net;
using PersonalMediaManager.Application.Common.Diagnostics;
using PersonalMediaManager.Application.Dtos.LocalAi;

namespace PersonalMediaManager.Infrastructure.External.Tests.LocalAi;

public sealed partial class LocalAiRuntimeManagerTests
{
    [Theory]
    [InlineData(ParseDiagnosticLevel.Standard, false)]
    [InlineData(ParseDiagnosticLevel.Standard, true)]
    [InlineData(ParseDiagnosticLevel.Full, false)]
    [InlineData(ParseDiagnosticLevel.Full, true)]
    public async Task HttpErrorOutcomeCannotChangeBecauseDiagnosticBodyCaptureFails(ParseDiagnosticLevel level, bool readFails)
    {
        RuntimeDiagnosticSink sink = new(); sink.Options.Level = level;
        using IDisposable scope = ParseDiagnostics.Begin("parse", sink: sink);
        _completion = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = readFails ? new StreamContent(new ErrorBodyStream(_ => ValueTask.FromException<int>(new IOException("synthetic read failure"))))
                : new StringContent(new string('x', 70 * 1024)),
        });
        await _manager.StartAsync();
        LocalAiInferenceResult result = await _manager.GenerateAsync(new("instruction", "data"));
        result.FailureReason.Should().Be("http_error"); result.Attempted.Should().BeTrue();
    }

    [Fact]
    public async Task SlowErrorBodyDiagnosticsCannotHoldInferenceForTheRuntimeBudget()
    {
        RuntimeDiagnosticSink sink = new(); sink.Options.Level = ParseDiagnosticLevel.Full;
        using IDisposable scope = ParseDiagnostics.Begin("parse", sink: sink);
        using CancellationTokenSource cancellation = new();
        _completion = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StreamContent(new ErrorBodyStream(async ct => { await Task.Delay(2000, ct); return 0; })),
        });
        await _manager.StartAsync();
        Task<LocalAiInferenceResult> pending = _manager.GenerateAsync(new("instruction", "data"), cancellation.Token);
        Task winner = await Task.WhenAny(pending, Task.Delay(500));
        try { winner.Should().Be(pending); (await pending).FailureReason.Should().Be("http_error"); }
        finally { cancellation.Cancel(); try { await pending; } catch (OperationCanceledException) { } }
    }

    [Fact]
    public async Task CallerCancellationDuringErrorBodyCaptureStillPropagates()
    {
        RuntimeDiagnosticSink sink = new(); sink.Options.Level = ParseDiagnosticLevel.Full;
        using IDisposable scope = ParseDiagnostics.Begin("parse", sink: sink);
        using CancellationTokenSource cancellation = new();
        _completion = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StreamContent(new ErrorBodyStream(ct => { cancellation.Cancel(); return ValueTask.FromCanceled<int>(ct); })),
        });
        await _manager.StartAsync();
        Func<Task> action = () => _manager.GenerateAsync(new("instruction", "data"), cancellation.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task CancellationDuringLocalRequestPreparationDoesNotClaimAnAttempt()
    {
        using CancellationTokenSource cancellation = new();
        CancelLocalPreparationSink sink = new(cancellation);
        using IDisposable scope = ParseDiagnostics.Begin("parse", sink: sink);
        int sent = 0;
        _completion = (_, _) => { sent++; return Task.FromResult(Completion()); };
        await _manager.StartAsync();
        Func<Task> action = () => _manager.GenerateAsync(new("instruction", "data"), cancellation.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
        sent.Should().Be(0);
        sink.Events.Single(e => e.Name == "local_ai.runtime_cancelled").Data.GetProperty("attempted").GetBoolean().Should().BeFalse();
    }

    private sealed class CancelLocalPreparationSink(CancellationTokenSource cancellation) : IParseDiagnosticSink
    {
        public ParseDiagnosticOptions Options { get; } = new() { Level = ParseDiagnosticLevel.Full };
        public List<ParseDiagnosticEvent> Events { get; } = [];
        public void Write(ParseDiagnosticEvent value)
        {
            Events.Add(value);
            if (value.Name is "ai.http_request_prepared" or "ai.http_request_dispatch") cancellation.Cancel();
        }
    }

    private sealed class ErrorBodyStream(Func<CancellationToken, ValueTask<int>> read) : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => read(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
