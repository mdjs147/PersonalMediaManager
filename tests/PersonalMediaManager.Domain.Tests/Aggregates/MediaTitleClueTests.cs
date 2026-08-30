using PersonalMediaManager.Domain.Aggregates.ParseTasks;

namespace PersonalMediaManager.Domain.Tests.Aggregates;

/// <summary>MediaTitleClue.HasNoTitleClue — 文件名/路径剥离技术噪音后是否全无剧名线索（保守优先，宁走 AI 不误判）</summary>
public sealed class MediaTitleClueTests
{
    // ---- 判为「无剧名线索」(true)：文件名 + 路径剥离后全空，应前置转人工 ----

    [Theory]
    [InlineData("S01E06_4K_60fps.mkv")]
    [InlineData("S01E22_4K_60fps.mkv")]
    [InlineData("11.mp4")]                       // 纯数字文件名
    [InlineData("(92D171D4).mkv")]               // 纯 CRC/hash
    [InlineData("[DBD-Raws][1080P][BDRip][HEVC-10bit][FLAC].mkv")] // 全是发布组+技术标签
    public void NoClue_WhenFileNameStripsToEmpty_AndNoSegments(string fileName)
    {
        MediaTitleClue.HasNoTitleClue(fileName, null).Should().BeTrue();
    }

    // ---- 判为「有剧名线索」(false)：任一处剩实质文本，应照常走 AI ----

    [Fact]
    public void HasClue_WhenSegmentHasCjkTitle()
    {
        // 文件名无剧名，但父目录「南部档案」是中文剧名 → 走 AI（AI 能用父目录补 title），绝不误转人工
        MediaTitleClue.HasNoTitleClue("11.mp4", ["南部档案"]).Should().BeFalse();
        MediaTitleClue.HasNoTitleClue("S01E06 4KHQHDR60FPS-GyWEB.mp4", ["悬案"]).Should().BeFalse();
    }

    [Fact]
    public void HasClue_WhenFileNameHasPinyinOrRomajiText()
    {
        // 拼音缩写 / 罗马音虽非真剧名，但保守起见剩文本就走 AI（AI 结合上下文可能救）
        MediaTitleClue.HasNoTitleClue("DACZLNF-09.mkv", null).Should().BeFalse();
        MediaTitleClue.HasNoTitleClue("YTYHXBYL-30.mkv", null).Should().BeFalse();
        MediaTitleClue.HasNoTitleClue("Koukaku Kidoutai - The Ghost in the Shell [02].mp4", null).Should().BeFalse();
    }

    [Fact]
    public void HasClue_WhenRealTitlePresent()
    {
        MediaTitleClue.HasNoTitleClue("鬼灭之刃 01.mkv", null).Should().BeFalse();
        MediaTitleClue.HasNoTitleClue("[DBD-Raws][机动战士高达SEED][01][1080P][HEVC].mkv", null).Should().BeFalse();
        MediaTitleClue.HasNoTitleClue("Inception.2010.1080p.mkv", null).Should().BeFalse();
    }

    [Fact]
    public void HasClue_WhenFileNameEmptyButAncestorSegmentHasTitle()
    {
        // 纯季集文件名 + 上级目录含剧名 → 有线索
        MediaTitleClue.HasNoTitleClue("S01E06_4K_60fps.mkv", ["进击的巨人", "Season 1"]).Should().BeFalse();
    }
}
