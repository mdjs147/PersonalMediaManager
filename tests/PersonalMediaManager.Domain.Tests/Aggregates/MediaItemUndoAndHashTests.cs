using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Domain.Exceptions;

namespace PersonalMediaManager.Domain.Tests.Aggregates;

/// <summary>MediaItem.UndoArchive / SetFileHash — 撤销归档回退 + 内容哈希写入守护</summary>
/// <remarks>
/// UndoArchive：仅 Completed 可撤销，回退到 Skipped 并清空 TargetPath/ArchivedAt/FileMissing/FileCheckedAt；
/// SetFileHash：空白拒绝、终态拒绝，否则写入哈希。两者均经聚合方法守护，覆盖正常与非法路径。
/// </remarks>
public sealed class MediaItemUndoAndHashTests
{
    /// <summary>构造一条「已完成归档」记录（带目标路径，便于验证撤销清空）；测试辅助</summary>
    private static MediaItem CompletedItem() => MediaItem.CreateFixture(
        sourcePath: "/src/movie.mkv",
        fileName: "movie.mkv",
        fileSize: 2048,
        status: MediaItemStatus.Completed,
        targetPath: "/library/Movies/movie.mkv");

    // ───────────────────────── UndoArchive ─────────────────────────

    [Fact(DisplayName = "UndoArchive：Completed → Skipped 且清空目标/归档时间/缺失标记")]
    public void UndoArchive_FromCompleted_ResetsToSkippedAndClearsFields()
    {
        MediaItem m = CompletedItem();
        // 先制造 FileMissing=true + FileCheckedAt 有值，验证撤销后被一并清空
        DateTimeOffset checkedAt = DateTimeOffset.UtcNow;
        m.MarkFileChecked(missing: true, checkedAt);
        m.FileMissing.Should().BeTrue();
        m.FileCheckedAt.Should().Be(checkedAt);
        m.TargetPath.Should().NotBeNull();

        m.UndoArchive();

        m.Status.Should().Be(MediaItemStatus.Skipped);
        m.TargetPath.Should().BeNull();
        m.ArchivedAt.Should().BeNull();
        m.FileMissing.Should().BeFalse();
        m.FileCheckedAt.Should().BeNull();
    }

    [Theory(DisplayName = "UndoArchive：非 Completed 来源抛 DomainException")]
    [InlineData(MediaItemStatus.Detected)]
    [InlineData(MediaItemStatus.Failed)]
    [InlineData(MediaItemStatus.Skipped)]
    public void UndoArchive_FromNonCompleted_ThrowsDomainException(MediaItemStatus from)
    {
        MediaItem m = MediaItem.CreateFixture(
            sourcePath: "/src/x.mkv",
            fileName: "x.mkv",
            fileSize: 1024,
            status: from);

        Action act = () => m.UndoArchive();

        act.Should().Throw<DomainException>().WithMessage("*UndoArchive 非法来源状态*");
    }

    // ───────────────────────── SetFileHash ─────────────────────────

    [Fact(DisplayName = "SetFileHash：非终态正常写入哈希")]
    public void SetFileHash_NonTerminal_SetsValue()
    {
        // CreateDetected 处于 Detected（非终态），允许写入
        MediaItem m = MediaItem.CreateDetected("/src/y.mkv", "y.mkv", 4096);
        const string hash = "a1b2c3d4e5f6";

        m.SetFileHash(hash);

        m.FileHash.Should().Be(hash);
    }

    [Theory(DisplayName = "SetFileHash：null/空白抛 DomainException")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SetFileHash_NullOrWhitespace_ThrowsDomainException(string? hash)
    {
        MediaItem m = MediaItem.CreateDetected("/src/z.mkv", "z.mkv", 1024);

        Action act = () => m.SetFileHash(hash!);

        act.Should().Throw<DomainException>().WithMessage("*FileHash 不能为空*");
    }

    [Theory(DisplayName = "SetFileHash：终态调用抛 DomainException")]
    [InlineData(MediaItemStatus.Completed)]
    [InlineData(MediaItemStatus.Skipped)]
    [InlineData(MediaItemStatus.Ignored)]
    [InlineData(MediaItemStatus.Failed)]
    public void SetFileHash_Terminal_ThrowsDomainException(MediaItemStatus terminal)
    {
        MediaItem m = MediaItem.CreateFixture(
            sourcePath: "/src/term.mkv",
            fileName: "term.mkv",
            fileSize: 1024,
            status: terminal);

        Action act = () => m.SetFileHash("deadbeef");

        act.Should().Throw<DomainException>().WithMessage("*终态*不允许再修改 FileHash*");
    }
}
