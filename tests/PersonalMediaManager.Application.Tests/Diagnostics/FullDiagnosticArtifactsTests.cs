using System.Text;
using System.Text.Json;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Common.Diagnostics;
using PersonalMediaManager.Infrastructure.Platform.Diagnostics;

namespace PersonalMediaManager.Application.Tests.Diagnostics;

public sealed class FullDiagnosticArtifactsTests
{
    [Fact]
    public void RawJsonKeepsSpacingAndNewlinesOutsideRedactedValueSpans()
    {
        const string raw = "{\n  \"title\" : \"剧😀\",\n  \"token\" : \"unsafe\",\n  \"number\" : 1.00\n}";
        RawDiagnosticRedaction safe = DiagnosticRawPrivacy.Redact(raw);
        safe.State.Should().Be("recorded"); safe.FormatPreserved.Should().BeTrue(); safe.Redacted.Should().BeTrue();
        safe.Text.Should().Contain("\n  \"title\" : \"剧😀\",\n").And.Contain("\"number\" : 1.00\n}").And.NotContain("unsafe");
        const string good = "{\r\n  \"title\":\"剧😀\", \"list\":[ 1, 2 ]\r\n}";
        DiagnosticRawPrivacy.Redact(good).Text.Should().Be(good);
    }

    [Theory]
    [InlineData("{\"token\":\"secret-no-close", "secret-no-close")]
    [InlineData("{\"access_token\":\"fixture-secret", "fixture-secret")]
    [InlineData("{\"analysis\":\"private-thought", "private-thought")]
    [InlineData("```json\n{\"reasoning\":\"private-thought\"}\n```", "private-thought")]
    [InlineData("{\"content\":[{\"type\":\"thinking\",\"thinking\":\"private-thought\"}]}", "private-thought")]
    [InlineData("{\"parts\":[{\"thought\":true,\"text\":\"private-thought\"},{\"text\":\"safe\"}]}", "private-thought")]
    [InlineData("Authorization: Bearer unsafe\r\nbody", "unsafe")]
    [InlineData("Bearer unsafe-token", "unsafe-token")]
    [InlineData("<think>a<think>b</think>private-tail</think>{\"index\":null}", "private-tail")]
    [InlineData("private-orphan</think>{\"index\":null}", "private-orphan")]
    [InlineData("{\"parts\":[{\"THOUGHT\":true,\"text\":\"private-thought\"}]}", "private-thought")]
    [InlineData("{\"parts\":[{\"thoughtSignature\":\"private-signature\",\"text\":\"safe\"}]}", "private-signature")]
    public void SensitiveBodiesFailClosedOrRedact(string raw, string forbidden)
        => DiagnosticRawPrivacy.Redact(raw).Text.Should().NotContain(forbidden);

    [Fact]
    public void LegacyLevelsAlsoFailClosedForNestedThinkingTags()
    {
        DiagnosticPrivacy.RedactText("<think>a<think>b</think>private-tail</think>").Should().NotContain("private-tail");
        DiagnosticPrivacy.RedactText("private-orphan</think>").Should().NotContain("private-orphan");
    }

    [Fact]
    public void NestedJsonAndEscapedCredentialCannotBypassRedaction()
    {
        string raw = JsonSerializer.Serialize(new { content = "{\n\"token\":\"nested-secret\",\"title\":\"exact\\credential\"}" });
        RawDiagnosticRedaction result = DiagnosticRawPrivacy.Redact(raw, "exact\\credential");
        result.Text.Should().NotContain("nested-secret").And.NotContain("credential");
        string encodedRoot = JsonSerializer.Serialize("{\"access_token\":\"root-secret\"}");
        DiagnosticRawPrivacy.Redact(encodedRoot).Text.Should().NotContain("root-secret");
        DiagnosticRawPrivacy.Redact("prefix " + encodedRoot).Text.Should().NotContain("root-secret");
    }

    [Fact]
    public void FullSeparatesArtifactsPreservesUtf8AndExportsEveryStage()
    {
        string root = PrivateFileSystem.CreateTemporaryDirectory("pmm-full-diag-");
        try
        {
            using ParseDiagnosticFileSink sink = new(root, new() { Level = ParseDiagnosticLevel.Full });
            string run = Guid.NewGuid().ToString("N");
            const string raw = "{\n  \"title\" : \"剧😀\", \"type\": \"tv\"\n}";
            using (ParseDiagnostics.Begin("parse", run, 2, sink))
            using (ParseDiagnostics.BeginAiCall("request1", "batch1", "item2", 2, "exact-secret"))
            {
                foreach (string stage in new[] { "input", "system", "user", "raw", "cleaned", "final" })
                    ParseDiagnostics.Emit(stage, new { content = ParseDiagnostics.CaptureText(raw) });
            }
            ParseReplayExport exported = sink.Export(run, null);
            exported.Artifacts.Should().HaveCount(6).And.OnlyContain(a => a.State == "recorded" && a.Text == raw);
            exported.Artifacts!.Select(a => a.ArtifactId).Distinct().Should().HaveCount(6);
            exported.Events.Where(e => e.Name == "raw").Should().OnlyContain(e => e.RequestId == "request1" && e.BatchId == "batch1" && e.ItemId == "item2" && e.Attempt == 2);
            string lines = string.Concat(Directory.GetFiles(root, "parse-*.jsonl").Select(ReadActiveEventFile));
            lines.Should().NotContain("剧");
            if (!OperatingSystem.IsWindows())
                foreach (string path in Directory.GetFiles(root))
                    (File.GetUnixFileMode(path) & UnixFileMode.OtherRead).Should().Be(0);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void FullExportReadsActiveWriterSnapshotAndKeepsLaterArtifactsForNextExport()
    {
        string root = PrivateFileSystem.CreateTemporaryDirectory("pmm-full-active-export-");
        try
        {
            Action afterSnapshot = () => { };
            using ParseDiagnosticFileSink sink = new(root, new() { Level = ParseDiagnosticLevel.Full })
            {
                BeforeExportReadForTest = () => afterSnapshot(),
            };
            string run = Guid.NewGuid().ToString("N");
            const string raw = "{\r\n  \"title\" : \"剧😀\"\r\n}";
            const string cleaned = "{\"title\":\"剧😀\"}";
            using (ParseDiagnostics.Begin("parse", run, 1, sink))
            {
                ParseDiagnostics.Emit("raw", new { content = ParseDiagnostics.CaptureText(raw) });
                string active = Directory.GetFiles(root, "parse-*.jsonl").Should().ContainSingle().Which;
                long snapshotLength = new FileInfo(active).Length;
                afterSnapshot = () =>
                {
                    afterSnapshot = () => { };
                    // 生产导出句柄仍打开时继续写同一文件，不能靠提前释放 writer 避开 Windows 共享约束。
                    ParseDiagnostics.Emit("cleaned", new { content = ParseDiagnostics.CaptureText(cleaned) });
                };

                ParseReplayExport first = sink.Export(run, null);
                first.Events.Should().Contain(e => e.Name == "raw").And.NotContain(e => e.Name == "cleaned");
                first.Artifacts.Should().ContainSingle(a => a.State == "recorded" && a.Text == raw);
                Directory.GetFiles(root, "parse-*.jsonl").Should().ContainSingle().Which.Should().Be(active);
                new FileInfo(active).Length.Should().BeGreaterThan(snapshotLength);

                ParseReplayExport next = sink.Export(run, null);
                next.Events.Should().Contain(e => e.Name == "raw").And.Contain(e => e.Name == "cleaned");
                next.Artifacts.Should().HaveCount(2).And.OnlyContain(a => a.State == "recorded");
                next.Artifacts!.Select(a => a.Text).Should().BeEquivalentTo(raw, cleaned);
                foreach (ParseReplayExport exported in new[] { first, next })
                {
                    JsonElement completeness = JsonSerializer.SerializeToElement(exported.Completeness);
                    completeness.GetProperty("unreadableLines").GetInt32().Should().Be(0);
                    completeness.GetProperty("storageWriteFailures").GetInt64().Should().Be(0);
                    completeness.GetProperty("sourceReadTruncated").GetBoolean().Should().BeFalse();
                }
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static string ReadActiveEventFile(string path)
    {
        // 只读测试检查沿用生产 Export 的双向共享约定，不改变 writer 的权限或生命周期。
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public void SizeLimitsAndRotationAreExplicitAndDoNotDeleteUnownedFiles()
    {
        string root = PrivateFileSystem.CreateTemporaryDirectory("pmm-full-diag-");
        try
        {
            File.WriteAllText(Path.Combine(root, "parse-personal.jsonl"), "keep");
            File.WriteAllText(Path.Combine(root, "body-user.txt"), "keep");
            using ParseDiagnosticFileSink sink = new(root, new() { Level = ParseDiagnosticLevel.Full, MaxArtifactUtf8Bytes = 1024, MaxArtifactTotalBytes = 1024, MaxArtifacts = 1 });
            string run = Guid.NewGuid().ToString("N");
            using (ParseDiagnostics.Begin("parse", run, 1, sink))
            {
                DiagnosticText oversized = ParseDiagnostics.CaptureText(new string('字', 500));
                oversized.State.Should().Be("not_recorded"); oversized.Reason.Should().Be("artifact_size_limit"); oversized.Truncated.Should().BeTrue();
                ParseDiagnostics.Emit("size", new { content = oversized });
                ParseDiagnostics.Emit("old", new { content = ParseDiagnostics.CaptureText("first") });
                ParseDiagnostics.Emit("new", new { content = ParseDiagnostics.CaptureText("second") });
            }
            ParseReplayExport export = sink.Export(run, null);
            export.Artifacts.Should().Contain(a => a.State == "unavailable" && a.Reason == "expired_or_missing");
            export.Artifacts.Should().Contain(a => a.Text == "second");
            File.ReadAllText(Path.Combine(root, "parse-personal.jsonl")).Should().Be("keep");
            File.ReadAllText(Path.Combine(root, "body-user.txt")).Should().Be("keep");
        }
        finally { Directory.Delete(root, true); }
    }


    [Fact]
    public void ExportDetectsTamperingAndRejectsArtifactSymlinks()
    {
        string root = PrivateFileSystem.CreateTemporaryDirectory("pmm-full-integrity-");
        try
        {
            using ParseDiagnosticFileSink sink = new(root, new() { Level = ParseDiagnosticLevel.Full });
            string run = Guid.NewGuid().ToString("N");
            DiagnosticText text;
            using (ParseDiagnostics.Begin("parse", run, 1, sink))
            {
                text = ParseDiagnostics.CaptureText("original");
                ParseDiagnostics.Emit("raw", new { content = text });
            }
            string path = Path.Combine(root, "body-" + text.ArtifactId + ".txt");
            File.WriteAllText(path, "tampered");
            sink.Export(run, null).Artifacts.Should().ContainSingle(a => a.Reason == "artifact_integrity_mismatch" && a.Text == null);
            if (!OperatingSystem.IsWindows())
            {
                string sentinel = Path.Combine(root, "keep.txt"); File.WriteAllText(sentinel, "keep");
                File.Delete(path); File.CreateSymbolicLink(path, sentinel);
                sink.Export(run, null).Artifacts.Should().ContainSingle(a => a.Reason == "artifact_read_failed");
                File.ReadAllText(sentinel).Should().Be("keep");
            }
        }
        finally { Directory.Delete(root, true); }
    }


    [Theory]
    [InlineData("Off")]
    [InlineData("Standard")]
    public void SwitchingAwayFromFullStopsNewBodyCaptureWithoutDeletingExistingEvidence(string level)
    {
        string root = PrivateFileSystem.CreateTemporaryDirectory("pmm-full-toggle-");
        try
        {
            using ParseDiagnosticFileSink sink = new(root, new() { Level = ParseDiagnosticLevel.Full });
            using (ParseDiagnostics.Begin("parse", sink: sink))
            {
                ParseDiagnostics.CaptureText("first body").ArtifactId.Should().NotBeNull();
                sink.SetLevel(level);
                DiagnosticText next = ParseDiagnostics.CaptureText("second body");
                next.State.Should().Be("not_recorded"); next.ArtifactId.Should().BeNull(); next.Text.Should().BeNull();
            }
            Directory.GetFiles(root, "body-*.txt").Should().ContainSingle();
        }
        finally { Directory.Delete(root, true); }
    }


    [Fact]
    public void MalformedArtifactReferenceCannotBreakWholeExport()
    {
        string root = PrivateFileSystem.CreateTemporaryDirectory("pmm-invalid-ref-");
        try
        {
            using ParseDiagnosticFileSink sink = new(root, new() { Level = ParseDiagnosticLevel.Full });
            string run = Guid.NewGuid().ToString("N");
            using (ParseDiagnostics.Begin("parse", run, 1, sink))
                ParseDiagnostics.Emit("bad_reference", new { artifactId = Guid.NewGuid().ToString("N"), sha256 = 42, capturedUtf8Bytes = "wrong" });
            sink.Export(run, null).Artifacts.Should().ContainSingle(a => a.State == "unavailable" && a.Reason == "invalid_artifact_reference");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void StoreRechecksLevelAtTheWriteBoundary()
    {
        string root = PrivateFileSystem.CreateTemporaryDirectory("pmm-level-race-");
        try
        {
            using ParseDiagnosticFileSink sink = new(root, new() { Level = ParseDiagnosticLevel.Full });
            DiagnosticText prepared = new("recorded", null, null, 4, 0, false, false);
            sink.SetLevel("Off");
            sink.StoreArtifact(prepared, "body").Reason.Should().Be("level_changed");
            Directory.GetFiles(root, "body-*.txt").Should().BeEmpty();
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void OffPreventsWriterAlreadyWaitingForStorageLockFromPersistingEvents()
    {
        string root = PrivateFileSystem.CreateTemporaryDirectory("pmm-event-off-race-");
        try
        {
            using ParseDiagnosticFileSink sink = new(root, new() { Level = ParseDiagnosticLevel.Full });
            object gate = typeof(ParseDiagnosticFileSink).GetField("_gate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(sink)!;
            using ManualResetEventSlim started = new();
            Exception? error = null;
            ParseDiagnosticEvent pending = new(2, DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N"), null, 1, 1, "parse", "must_not_persist", JsonSerializer.SerializeToElement(new { safe = true }));
            Thread writer = new(() =>
            {
                try { started.Set(); sink.Write(pending); }
                catch (Exception ex) { error = ex; }
            });
            lock (gate)
            {
                writer.Start(); started.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                SpinWait.SpinUntil(() => (writer.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5)).Should().BeTrue();
                sink.SetLevel("Off");
            }
            writer.Join(TimeSpan.FromSeconds(5)).Should().BeTrue(); error.Should().BeNull();
            Directory.GetFiles(root, "parse-*.jsonl").Should().BeEmpty();
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void StorageFailureCannotEscapeAndSettingsPersist()
    {
        string root = PrivateFileSystem.CreateTemporaryDirectory("pmm-full-diag-");
        try
        {
            using (ParseDiagnosticFileSink sink = new(root, new())) sink.SetLevel("Full");
            using (ParseDiagnosticFileSink sink = new(root, new())) sink.Options.Level.Should().Be(ParseDiagnosticLevel.Full);
            string blocked = Path.Combine(root, "file-not-directory"); File.WriteAllText(blocked, "original");
            using ParseDiagnosticFileSink failing = new(blocked, new() { Level = ParseDiagnosticLevel.Full });
            using (ParseDiagnostics.Begin("parse", sink: failing))
                ParseDiagnostics.CaptureText("safe").Reason.Should().Be("artifact_write_failed");
            File.ReadAllText(blocked).Should().Be("original");
        }
        finally { Directory.Delete(root, true); }
    }
}
