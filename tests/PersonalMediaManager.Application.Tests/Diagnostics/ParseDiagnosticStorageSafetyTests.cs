using System.Text.Json;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Common.Diagnostics;
using PersonalMediaManager.Infrastructure.Platform.Diagnostics;

namespace PersonalMediaManager.Application.Tests.Diagnostics;

public sealed class ParseDiagnosticStorageSafetyTests
{
    [Theory]
    [InlineData("/srv/private/film.mkv")]
    [InlineData("/archive/film.mkv")]
    [InlineData("/data/private/film.mkv")]
    public void EveryUnixRootIsRemovedFromSourceAndTarget(string path)
    {
        JsonElement safe = DiagnosticPrivacy.Sanitize(new { source = path, target = path, sourceKind = "rule" });
        safe.GetRawText().Should().NotContain(path); safe.GetProperty("sourceKind").GetString().Should().Be("rule");
    }

    [Fact]
    public void SymlinkDirectoryDoesNotChangeOrPruneTarget()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = PrivateFileSystem.CreateTemporaryDirectory("pmm-diag-links-");
        try
        {
            string target = Path.Combine(root, "target"); Directory.CreateDirectory(target);
            string sentinel = Path.Combine(target, "parse-sentinel.jsonl"); File.WriteAllText(sentinel, "sentinel");
            UnixFileMode oldMode = File.GetUnixFileMode(target);
            string link = Path.Combine(root, "link"); Directory.CreateSymbolicLink(link, target);
            using ParseDiagnosticFileSink sink = new(link, new() { MaxFiles = 1 });
            using (ParseDiagnostics.Begin("parse", mediaItemId: 1, sink: sink)) ParseDiagnostics.Emit("test");
            File.ReadAllText(sentinel).Should().Be("sentinel");
            File.GetUnixFileMode(target).Should().Be(oldMode);
            Directory.GetFiles(target).Should().ContainSingle();
            Action export = () => sink.Export(null, 1); export.Should().Throw<UnauthorizedAccessException>();
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ReplacedActiveSymlinkIsNeverAppendedOrExported()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = PrivateFileSystem.CreateTemporaryDirectory("pmm-diag-active-");
        try
        {
            string logs = Path.Combine(root, "logs");
            using ParseDiagnosticFileSink sink = new(logs, new());
            using (ParseDiagnostics.Begin("parse", mediaItemId: 1, sink: sink)) ParseDiagnostics.Emit("test");
            string active = Directory.GetFiles(logs).Single();
            string target = Path.Combine(root, "sentinel"); File.WriteAllText(target, "sentinel");
            File.Delete(active); File.CreateSymbolicLink(active, target);
            using (ParseDiagnostics.Begin("parse", mediaItemId: 2, sink: sink)) ParseDiagnostics.Emit("test");
            File.ReadAllText(target).Should().Be("sentinel");
            sink.Export(null, 2).Events.Should().BeEmpty();
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RemovedActiveFileIsRecreatedInsteadOfWritingToUnlinkedHandle()
    {
        string root = PrivateFileSystem.CreateTemporaryDirectory("pmm-diag-deleted-");
        try
        {
            using ParseDiagnosticFileSink sink = new(root, new());
            using (ParseDiagnostics.Begin("parse", mediaItemId: 1, sink: sink)) ParseDiagnostics.Emit("before_delete");
            File.Delete(Directory.GetFiles(root).Single());
            using (ParseDiagnostics.Begin("parse", mediaItemId: 2, sink: sink)) ParseDiagnostics.Emit("after_delete");
            sink.Export(null, 2).Events.Should().Contain(e => e.Name == "after_delete");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExportReadsOutsideWriteLockAndHonorsCancellation()
    {
        string root = PrivateFileSystem.CreateTemporaryDirectory("pmm-diag-export-");
        using ManualResetEventSlim snapshot = new(); using ManualResetEventSlim resume = new();
        try
        {
            using ParseDiagnosticFileSink sink = new(root, new()) { BeforeExportReadForTest = () => { snapshot.Set(); resume.Wait(TimeSpan.FromSeconds(10)); } };
            using (ParseDiagnostics.Begin("parse", mediaItemId: 1, sink: sink)) ParseDiagnostics.Emit("test");
            using CancellationTokenSource cancellation = new();
            Task<ParseReplayExport> export = Task.Run(() => sink.Export(null, 1, ct: cancellation.Token));
            snapshot.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            Task write = Task.Run(() => { using IDisposable scope = ParseDiagnostics.Begin("parse", mediaItemId: 2, sink: sink); ParseDiagnostics.Emit("during_export"); });
            await write.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel(); resume.Set();
            await ((Func<Task>)(() => export)).Should().ThrowAsync<OperationCanceledException>();
        }
        finally { resume.Set(); Directory.Delete(root, true); }
    }

    [Fact]
    public void OversizedAndPartialLinesAreReportedWithoutBeingDeserialized()
    {
        string root = PrivateFileSystem.CreateTemporaryDirectory("pmm-diag-oversized-");
        try
        {
            using ParseDiagnosticFileSink sink = new(root, new() { MaxEventUtf8Bytes = 2048, MaxFileBytes = 8192 });
            string corrupted = Path.Combine(root, "parse-corrupted.jsonl");
            File.WriteAllText(corrupted, new string('x', 1_000_000));
            ParseReplayExport result = sink.Export(null, 1);
            result.Events.Should().BeEmpty();
            JsonElement info = JsonSerializer.SerializeToElement(result.Completeness);
            info.GetProperty("unreadableLines").GetInt32().Should().BeGreaterThan(0);
            info.GetProperty("sourceReadTruncated").GetBoolean().Should().BeTrue();
        }
        finally { Directory.Delete(root, true); }
    }
}
