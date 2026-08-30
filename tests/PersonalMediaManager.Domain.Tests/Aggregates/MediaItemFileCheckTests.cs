using PersonalMediaManager.Domain.Aggregates.MediaItems;

namespace PersonalMediaManager.Domain.Tests.Aggregates;

/// <summary>MediaItem.MarkFileChecked — 文件存在性标注（不动状态机）</summary>
public sealed class MediaItemFileCheckTests
{
    private static MediaItem New() => MediaItem.CreateDetected("/x/y.mkv", "y.mkv", 1024);

    [Fact(DisplayName = "默认未检查：FileMissing=false，FileCheckedAt=null")]
    public void Defaults_Unchecked()
    {
        MediaItem m = New();
        m.FileMissing.Should().BeFalse();
        m.FileCheckedAt.Should().BeNull();
    }

    [Fact(DisplayName = "标记缺失：写 FileMissing + FileCheckedAt")]
    public void MarkFileChecked_Missing()
    {
        MediaItem m = New();
        DateTimeOffset at = DateTimeOffset.UtcNow;
        m.MarkFileChecked(missing: true, at);
        m.FileMissing.Should().BeTrue();
        m.FileCheckedAt.Should().Be(at);
    }

    [Fact(DisplayName = "标记存在后再缺失：可重复更新")]
    public void MarkFileChecked_Updatable()
    {
        MediaItem m = New();
        m.MarkFileChecked(missing: true, DateTimeOffset.UtcNow);
        DateTimeOffset later = DateTimeOffset.UtcNow.AddMinutes(1);
        m.MarkFileChecked(missing: false, later);
        m.FileMissing.Should().BeFalse();
        m.FileCheckedAt.Should().Be(later);
    }
}
