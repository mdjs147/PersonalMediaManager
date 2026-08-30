using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Domain.Exceptions;

namespace PersonalMediaManager.Domain.Tests.Aggregates;

/// <summary>MediaItem.SetAudioProbe — 音频探测结果写入 + 终态守护</summary>
public sealed class MediaItemAudioProbeTests
{
    [Fact]
    public void SetAudioProbe_Writes_Codecs_And_Flag()
    {
        MediaItem m = MediaItem.CreateDetected("/x/y.mkv", "y.mkv", 1024);

        m.SetAudioProbe("av3a,aac", hasIncompatibleAudio: true);

        m.AudioCodecs.Should().Be("av3a,aac");
        m.HasIncompatibleAudio.Should().BeTrue();
    }

    [Fact]
    public void SetAudioProbe_Null_Codecs_Allowed()
    {
        MediaItem m = MediaItem.CreateDetected("/x/y.mkv", "y.mkv", 1024);

        m.SetAudioProbe(null, hasIncompatibleAudio: false);

        m.AudioCodecs.Should().BeNull();
        m.HasIncompatibleAudio.Should().BeFalse();
    }

    [Fact]
    public void SetAudioProbe_Allowed_During_Archiving()
    {
        // 归档前探测：Archiving 非终态，可写
        MediaItem m = MediaItem.CreateFixture("/x/y.mkv", "y.mkv", 1024, status: MediaItemStatus.Archiving);

        m.SetAudioProbe("av3a,dts", hasIncompatibleAudio: true);

        m.HasIncompatibleAudio.Should().BeTrue();
        m.AudioCodecs.Should().Be("av3a,dts");
    }

    [Theory]
    [InlineData(MediaItemStatus.Completed)]
    [InlineData(MediaItemStatus.Skipped)]
    [InlineData(MediaItemStatus.Ignored)]
    [InlineData(MediaItemStatus.Failed)]
    public void SetAudioProbe_Throws_On_Terminal_State(MediaItemStatus terminal)
    {
        MediaItem m = MediaItem.CreateFixture("/x/y.mkv", "y.mkv", 1024, status: terminal);

        Action act = () => m.SetAudioProbe("aac", hasIncompatibleAudio: false);

        act.Should().Throw<DomainException>().WithMessage("*终态*");
    }

    [Theory]
    [InlineData(MediaItemStatus.Completed)]
    [InlineData(MediaItemStatus.Skipped)]
    public void RefreshAudioProbe_Allowed_On_Terminal_State(MediaItemStatus terminal)
    {
        // 存量重扫：终态(Completed/Skipped)记录可刷新音频探测（不守护终态，仿 MarkFileChecked）
        MediaItem m = MediaItem.CreateFixture("/x/y.mkv", "y.mkv", 1024, status: terminal);

        m.RefreshAudioProbe("av3a,aac", hasIncompatibleAudio: true);

        m.HasIncompatibleAudio.Should().BeTrue();
        m.AudioCodecs.Should().Be("av3a,aac");
    }
}
