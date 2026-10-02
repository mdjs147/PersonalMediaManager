using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Archive;
using PersonalMediaManager.Application.Services.Classify;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Common.Diagnostics;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Persistence.Services.Parse;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

public sealed partial class ProcessFileServiceTests
{
    private sealed class DiagnosticSink(bool fail = false) : IParseDiagnosticSink
    {
        public ParseDiagnosticOptions Options { get; } = new();
        public List<ParseDiagnosticEvent> Events { get; } = [];
        public void Write(ParseDiagnosticEvent value)
        {
            if (fail) throw new IOException("诊断磁盘故障");
            Events.Add(value);
        }
    }
    private ProcessFileService DiagnosticSut(IParseDiagnosticSink sink) => new(
        _dbFactory, _writeDetector, _ruleEngine, _forcedMatch, _tmdb, _aiOrchestrator, _folderCache,
        _classify, _archive, _fileHasher, _fileProbe, _audioProbe, new NullTaskNotifier(), _webhook, new SystemClock(),
        NullLogger<ProcessFileService>.Instance, diagnostics: sink);

    [Fact]
    public async Task Diagnostics_RealPipelineCarriesInputRuleRankingAndFinalUnderOneRun()
    {
        ConfigureRule(0.9, false); ConfigureTmdb(1);
        ConfigureClassify(ClassifyDecision.Matched, 7); ConfigureArchive(ArchiveOutcome.Completed, "/archive/Sample.mkv");
        DiagnosticSink sink = new(); string scan = new('b', 32);
        ProcessFileOutcome result = await DiagnosticSut(sink).ProcessAsync(new(_tempFile, 1, PendingFileSource.FullScan, scan), CancellationToken.None);
        result.Outcome.Should().Be(ProcessOutcome.Completed);
        sink.Events.Select(e => e.RunId).Distinct().Should().ContainSingle();
        sink.Events.Should().OnlyContain(e => e.ScanRunId == scan);
        sink.Events.Should().Contain(e => e.Name == "parse.input" && e.MediaItemId == result.MediaItemId);
        sink.Events.Should().Contain(e => e.Name == "rule.result");
        sink.Events.Should().Contain(e => e.Name == "tmdb.ranking");
        sink.Events.Should().Contain(e => e.Name == "pipeline.terminal");
        JsonSerializer.Serialize(sink.Events).Should().NotContain(_tempFile).And.NotContain("/archive/Sample.mkv");
    }

    [Fact]
    public async Task Diagnostics_FailedSinkDoesNotChangeBusinessOutcome()
    {
        ConfigureRule(0.9, false); ConfigureTmdb(1);
        ConfigureClassify(ClassifyDecision.Matched, 7); ConfigureArchive(ArchiveOutcome.Completed, "/archive/Sample.mkv");
        ProcessFileOutcome result = await DiagnosticSut(new DiagnosticSink(true)).ProcessAsync(new(_tempFile, 1, PendingFileSource.Manual), CancellationToken.None);
        result.Outcome.Should().Be(ProcessOutcome.Completed);
    }

    [Fact]
    public async Task Diagnostics_CancellationBeforeMediaCreationIsRecordedAndRethrown()
    {
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        _writeDetector.WaitUntilCompleteAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException(cancelled.Token));
        DiagnosticSink sink = new();
        Func<Task> action = () => DiagnosticSut(sink).ProcessAsync(new(_tempFile, 1, PendingFileSource.Manual), cancelled.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
        sink.Events.Should().Contain(e => e.Name == "parse.cancelled");
        sink.Events.Last().Name.Should().Be("operation.ended");
        ParseDiagnostics.CurrentRunId.Should().BeNull();
    }
}
