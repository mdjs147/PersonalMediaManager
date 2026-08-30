using Microsoft.Extensions.Logging.Abstractions;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Infrastructure.Platform.FileSystem;

namespace PersonalMediaManager.Application.Tests.FileSystem;

/// <summary>FileMover（D4.1）— 同卷 Move + Copy + 冲突 + 缺失源 + 目标目录自建 + .tmp 原子化</summary>
/// <remarks>
/// 跨卷 fallback 难在测试里造真实跨卷场景（单测环境只有一个临时盘），所以跨卷场景留给 Host.Tests E2E
/// 或人工跨盘验证。这里覆盖同卷场景 + 原子化 + 异常分支。
/// </remarks>
public sealed class FileMoverTests : IDisposable
{
    private readonly string _workDir;
    private readonly FileMover _sut;

    public FileMoverTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"pmm-filemover-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        _sut = new FileMover(NullLogger<FileMover>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { /* 忽略测试目录清理失败 */ }
    }

    [Fact]
    public async Task Move_SameVolume_SourceRemoved_TargetCreated()
    {
        string src = WriteFile("a.mkv", "hello");
        string dst = Path.Combine(_workDir, "out", "a.mkv");

        await _sut.MoveAsync(src, dst);

        File.Exists(src).Should().BeFalse("Move 后源应消失");
        File.Exists(dst).Should().BeTrue();
        (await File.ReadAllTextAsync(dst)).Should().Be("hello");
    }

    [Fact]
    public async Task Move_TargetDirectoryCreatedAutomatically()
    {
        string src = WriteFile("b.mkv", "x");
        string dst = Path.Combine(_workDir, "deep", "nested", "dir", "b.mkv");

        await _sut.MoveAsync(src, dst);
        Directory.Exists(Path.GetDirectoryName(dst)!).Should().BeTrue();
        File.Exists(dst).Should().BeTrue();
    }

    [Fact]
    public async Task Move_TargetExists_ThrowsFileConflictException_SourceUntouched()
    {
        string src = WriteFile("c.mkv", "src");
        string dst = WriteFile("dst/c.mkv", "existing");

        Func<Task> act = () => _sut.MoveAsync(src, dst);
        FileConflictException ex = (await act.Should().ThrowAsync<FileConflictException>()).Which;
        ex.TargetPath.Should().Be(dst);

        File.Exists(src).Should().BeTrue("冲突时源不应被删除");
        (await File.ReadAllTextAsync(dst)).Should().Be("existing", "冲突时目标不应被覆盖");
    }

    [Fact]
    public async Task Move_SourceMissing_ThrowsFileNotFoundException()
    {
        string src = Path.Combine(_workDir, "ghost.mkv");
        string dst = Path.Combine(_workDir, "out.mkv");

        await ((Func<Task>)(() => _sut.MoveAsync(src, dst)))
            .Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task Copy_SameVolume_BothExistAfter_ContentMatches()
    {
        string src = WriteFile("d.mkv", "payload-12345");
        string dst = Path.Combine(_workDir, "out", "d.mkv");

        await _sut.CopyAsync(src, dst);

        File.Exists(src).Should().BeTrue("Copy 保留源");
        File.Exists(dst).Should().BeTrue();
        (await File.ReadAllTextAsync(dst)).Should().Be("payload-12345");
    }

    [Fact]
    public async Task Copy_NoTempLeftBehind_AfterSuccess()
    {
        string src = WriteFile("e.mkv", "x");
        string dst = Path.Combine(_workDir, "out", "e.mkv");

        await _sut.CopyAsync(src, dst);

        string targetDir = Path.GetDirectoryName(dst)!;
        Directory.GetFiles(targetDir, "*.tmp").Should().BeEmpty("Copy 完成后不应有 .tmp 残留");
    }

    [Fact]
    public async Task Copy_LargeBinary_RoundTripsByteForByte()
    {
        // 模拟一个非平凡的二进制（512KB 随机数据）确保 CopyToAsync 走完整缓冲区路径
        byte[] data = new byte[512 * 1024];
        new Random(42).NextBytes(data);
        string src = Path.Combine(_workDir, "big.bin");
        await File.WriteAllBytesAsync(src, data);

        string dst = Path.Combine(_workDir, "out", "big.bin");
        await _sut.CopyAsync(src, dst);

        byte[] dstBytes = await File.ReadAllBytesAsync(dst);
        dstBytes.Should().Equal(data);
    }

    [Fact]
    public async Task Copy_EmptyPaths_ThrowArgumentException()
    {
        // 源空 → ValidateSource 抛 ArgumentException
        await ((Func<Task>)(() => _sut.CopyAsync("", "anything")))
            .Should().ThrowAsync<ArgumentException>();

        // 目标空（源真实存在）→ EnsureNoConflict 抛 ArgumentException
        string realSrc = WriteFile("g.mkv", "x");
        await ((Func<Task>)(() => _sut.CopyAsync(realSrc, "")))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Copy_TargetExists_ThrowsConflict_NoTempLeftBehind()
    {
        string src = WriteFile("f.mkv", "src");
        string dst = WriteFile("out/f.mkv", "old");

        Func<Task> act = () => _sut.CopyAsync(src, dst);
        await act.Should().ThrowAsync<FileConflictException>();

        string targetDir = Path.GetDirectoryName(dst)!;
        Directory.GetFiles(targetDir, "*.tmp").Should().BeEmpty("冲突早返回，连 .tmp 都不应创建");
        (await File.ReadAllTextAsync(dst)).Should().Be("old", "冲突时目标不应改动");
    }

    [Fact(DisplayName = "复制前自愈清扫同目标陈旧 .tmp 残留；其它目标的 .tmp 不受影响")]
    public async Task Copy_SweepsStaleTmp_OfSameTarget_BeforeCopy()
    {
        string src = WriteFile("h.mkv", "new-content");
        string dstDir = Path.Combine(_workDir, "out");
        Directory.CreateDirectory(dstDir);
        string dst = Path.Combine(dstDir, "h.mkv");
        // 陈旧残留：上次跨卷复制失败清理自身又失败 / 进程崩溃遗留的孤儿 .tmp
        string stale = Path.Combine(dstDir, $"h.mkv.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(stale, "stale-orphan");
        // 其它目标的 .tmp：不属本目标模式，不应被清扫
        string otherTmp = Path.Combine(dstDir, $"other.mkv.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(otherTmp, "other-orphan");

        await _sut.CopyAsync(src, dst);

        File.Exists(stale).Should().BeFalse("同目标陈旧 .tmp 应在复制前被自愈清扫");
        File.Exists(otherTmp).Should().BeTrue("不同目标的 .tmp 不应被波及");
        (await File.ReadAllTextAsync(dst)).Should().Be("new-content");
    }

    [Fact(DisplayName = "陈旧 .tmp 被锁定清扫失败 → 容错不阻断，本次复制照常成功")]
    public async Task Copy_StaleTmpLocked_SweepFailureTolerated_CopyStillSucceeds()
    {
        string src = WriteFile("i.mkv", "payload");
        string dstDir = Path.Combine(_workDir, "out2");
        Directory.CreateDirectory(dstDir);
        string dst = Path.Combine(dstDir, "i.mkv");
        string stale = Path.Combine(dstDir, $"i.mkv.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(stale, "locked-stale");

        // 用独占句柄锁住陈旧 .tmp，使 File.Delete 失败 —— 清扫必须容错且不阻断复制
        await using (FileStream @lock = new(stale, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await _sut.CopyAsync(src, dst);
        }

        File.Exists(dst).Should().BeTrue();
        (await File.ReadAllTextAsync(dst)).Should().Be("payload");
        File.Exists(stale).Should().BeTrue("被锁定的陈旧 .tmp 清扫失败但被容错保留");
    }

    private string WriteFile(string relativePath, string content)
    {
        string full = Path.Combine(_workDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }
}
