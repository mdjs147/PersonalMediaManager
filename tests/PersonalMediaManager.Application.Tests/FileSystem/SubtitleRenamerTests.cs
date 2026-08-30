using PersonalMediaManager.Application.Common.Archiving;

namespace PersonalMediaManager.Application.Tests.FileSystem;

/// <summary>SubtitleRenamer（D4.3，§3.6.3）— 5 种语言 + 同语言 3 文件取最大 + zip 跳过 + 季集检测/归属匹配 + PathHint 语言回退</summary>
public sealed class SubtitleRenamerTests
{
    [Theory]
    [InlineData("show.CHS.srt", "zh")]
    [InlineData("show.SC.srt", "zh")]
    [InlineData("show.简.srt", "zh")]
    [InlineData("show.Chinese.srt", "zh")]
    [InlineData("show.CHT.srt", "zh-TW")]
    [InlineData("show.TC.srt", "zh-TW")]
    [InlineData("show.繁.srt", "zh-TW")]
    [InlineData("show.Traditional.srt", "zh-TW")]
    [InlineData("show.ENG.srt", "en")]
    [InlineData("show.EN.srt", "en")]
    [InlineData("show.English.srt", "en")]
    [InlineData("show.JPN.srt", "ja")]
    [InlineData("show.JP.srt", "ja")]
    [InlineData("show.Japanese.srt", "ja")]
    public void DetectLanguage_KnownTokens_MapsToIsoCode(string fileName, string expected)
    {
        SubtitleRenamer.DetectLanguage(fileName).Should().Be(expected);
    }

    [Fact]
    public void DetectLanguage_UnknownLang_ReturnsUnd()
    {
        SubtitleRenamer.DetectLanguage("show.weirdo.srt").Should().Be("und");
    }

    [Fact]
    public void DetectLanguage_TraditionalBeforeSimplified_DoesNotMisfireTcAsC()
    {
        // 「TC」必须识别为 zh-TW 而不是 zh（避免被 SC 的 'C' 抢匹配）
        SubtitleRenamer.DetectLanguage("show.TC.ass").Should().Be("zh-TW");
    }

    [Fact]
    public void Plan_SingleSubtitle_RenamedWithPlexBase()
    {
        SubtitleCandidate sub = new("/in/show.zh.srt", "show.zh.srt", 1024);
        IReadOnlyList<SubtitleRenameDecision> plan = SubtitleRenamer.Plan("Inception (2010)", [sub]);

        plan.Should().HaveCount(1);
        plan[0].Action.Should().Be(SubtitleAction.Rename);
        plan[0].TargetFileName.Should().Be("Inception (2010).zh.srt");
    }

    [Fact]
    public void Plan_MultiLanguage_AllRenamedKeepsOwnLang()
    {
        SubtitleCandidate zh = new("/in/show.zh.srt", "show.zh.srt", 1000);
        SubtitleCandidate en = new("/in/show.en.srt", "show.en.srt", 500);
        IReadOnlyList<SubtitleRenameDecision> plan = SubtitleRenamer.Plan("X (2020)", [zh, en]);

        plan.Should().HaveCount(2);
        plan.Should().Contain(d => d.TargetFileName == "X (2020).zh.srt" && d.Action == SubtitleAction.Rename);
        plan.Should().Contain(d => d.TargetFileName == "X (2020).en.srt" && d.Action == SubtitleAction.Rename);
    }

    [Fact]
    public void Plan_SameLanguage_ThreeFiles_LargestNoOrdinal_RestSerialized()
    {
        SubtitleCandidate small = new("/in/show.CHS.srt", "show.CHS.srt", 100);
        SubtitleCandidate big = new("/in/show.zh.srt", "show.zh.srt", 5000);
        SubtitleCandidate medium = new("/in/show.简.srt", "show.简.srt", 2000);

        IReadOnlyList<SubtitleRenameDecision> plan = SubtitleRenamer.Plan("Foo (2021)", [small, big, medium]);

        Dictionary<string, string?> map = plan.ToDictionary(p => p.Source.FileName, p => p.TargetFileName);
        map["show.zh.srt"].Should().Be("Foo (2021).zh.srt", "5000 字节最大，不加序号");
        map["show.简.srt"].Should().Be("Foo (2021).2.zh.srt", "2000 字节第 2 大");
        map["show.CHS.srt"].Should().Be("Foo (2021).3.zh.srt", "100 字节第 3 大");
    }

    [Fact]
    public void Plan_ArchiveSubtitle_Skipped_WithReason()
    {
        SubtitleCandidate zip = new("/in/pack.zip", "pack.zip", 99999);
        IReadOnlyList<SubtitleRenameDecision> plan = SubtitleRenamer.Plan("Anything", [zip]);

        plan.Should().HaveCount(1);
        plan[0].Action.Should().Be(SubtitleAction.SkipArchive);
        plan[0].TargetFileName.Should().BeNull();
        plan[0].Reason.Should().Contain("压缩包");
    }

    [Fact]
    public void Plan_RarAnd7z_AlsoSkipped()
    {
        IReadOnlyList<SubtitleRenameDecision> plan = SubtitleRenamer.Plan("X", new[]
        {
            new SubtitleCandidate("/a.rar", "a.rar", 1),
            new SubtitleCandidate("/b.7z", "b.7z", 1),
        });
        plan.Should().AllSatisfy(d => d.Action.Should().Be(SubtitleAction.SkipArchive));
    }

    [Fact]
    public void Plan_UnsupportedExtension_Skipped()
    {
        SubtitleCandidate txt = new("/notes.txt", "notes.txt", 1);
        IReadOnlyList<SubtitleRenameDecision> plan = SubtitleRenamer.Plan("X", [txt]);
        plan[0].Action.Should().Be(SubtitleAction.SkipUnsupported);
    }

    [Fact]
    public void Plan_UndLanguage_PreservedInTarget()
    {
        SubtitleCandidate weird = new("/in/show.srt", "show.srt", 100);
        IReadOnlyList<SubtitleRenameDecision> plan = SubtitleRenamer.Plan("Bar (2022)", [weird]);
        plan[0].TargetFileName.Should().Be("Bar (2022).und.srt");
    }

    [Fact]
    public void Plan_MixedExtensions_AssAndSrt_BothRenamed()
    {
        SubtitleCandidate srt = new("/in/show.zh.srt", "show.zh.srt", 1000);
        SubtitleCandidate ass = new("/in/show.zh.ass", "show.zh.ass", 2000);
        IReadOnlyList<SubtitleRenameDecision> plan = SubtitleRenamer.Plan("X", [srt, ass]);

        Dictionary<string, string?> map = plan.ToDictionary(p => p.Source.FileName, p => p.TargetFileName);
        // ass 更大 → ass 拿不带序号的 .zh，srt 拿 .2.zh
        map["show.zh.ass"].Should().Be("X.zh.ass");
        map["show.zh.srt"].Should().Be("X.2.zh.srt");
    }

    [Fact]
    public void Plan_EmptyInput_ReturnsEmpty()
    {
        SubtitleRenamer.Plan("X", []).Should().BeEmpty();
    }

    // ---------- DetectSeasonEpisode：单段文本季集检测（§3.6.3 启发式匹配） ----------

    [Theory]
    // 季集一体（强模式，命中即定）
    [InlineData("Show.S01E02.chs", 1, 2)]
    [InlineData("s1e1", 1, 1)]
    [InlineData("S01EP02", 1, 2)]
    [InlineData("S01.E02", 1, 2)]
    [InlineData("S01E01.[1080P]", 1, 1)] // 季集一体优先，后续弱模式不再参与
    // 仅集：第N集/话 → EP/E → Episode N → [NN] → 整段纯数字
    [InlineData("第3集", null, 3)]
    [InlineData("第03话", null, 3)]
    [InlineData("EP01", null, 1)]
    [InlineData("E05", null, 5)]
    [InlineData("Episode 7", null, 7)]
    [InlineData("[03]", null, 3)]
    [InlineData("01", null, 1)] // 目录段「01」整段纯数字
    // 仅季：第N季 → Season N → 独立 Sxx 词
    [InlineData("第2季", 2, null)]
    [InlineData("Season 2", 2, null)]
    [InlineData("S2", 2, null)]
    // 弱模式（[NN] / 纯数字段）排除分辨率与年份
    [InlineData("[1080]", null, null)]
    [InlineData("[2023]", null, null)]
    [InlineData("1080", null, null)]
    [InlineData("2023", null, null)]
    // 干扰串不误抓
    [InlineData("Movie.2023.chs", null, null)] // 电影年份不当集数
    [InlineData("DTS5.1", null, null)]         // 音轨标记的 S 前有字母，不当季号
    [InlineData("x265", null, null)]           // 编码标记不当集号
    [InlineData("H265", null, null)]
    public void DetectSeasonEpisode_SingleSegment_ExtractsExpectedMark(string text, int? expectedSeason, int? expectedEpisode)
    {
        (int? season, int? episode) = SubtitleRenamer.DetectSeasonEpisode(text);
        season.Should().Be(expectedSeason);
        episode.Should().Be(expectedEpisode);
    }

    // ---------- DetectSeasonEpisode：多段合并（文件名优先，目录段从深到浅） ----------

    [Fact]
    public void DetectSeasonEpisode_MultiSegments_EpisodeFoundInDirectorySegment()
    {
        // 文件名段「chs」无标记，集号落在目录段「EP02」
        (int? season, int? episode) = SubtitleRenamer.DetectSeasonEpisode(["chs", "EP02", "Subs"]);
        season.Should().BeNull();
        episode.Should().Be(2);
    }

    [Fact]
    public void DetectSeasonEpisode_MultiSegments_SeasonAndEpisodeFromDifferentSegments()
    {
        // 文件名给集、目录段给季（「Subs/S2/EP01.srt」拆分结构）
        (int? season, int? episode) = SubtitleRenamer.DetectSeasonEpisode(["EP01", "S2", "Subs"]);
        season.Should().Be(2);
        episode.Should().Be(1);
    }

    [Fact]
    public void DetectSeasonEpisode_MultiSegments_FirstHitWins_LaterSegmentDoesNotOverride()
    {
        // 文件名已给出季集一体标记，后续目录段「EP02」不覆盖
        (int? season, int? episode) = SubtitleRenamer.DetectSeasonEpisode(["S01E01", "EP02"]);
        season.Should().Be(1);
        episode.Should().Be(1);
    }

    // ---------- MatchesEpisode：字幕归属目标集判定 ----------

    [Theory]
    [InlineData(null, 2, 1, 2, 2, true)]      // 字幕无季号视为同季（「EP02.srt」常省略季）
    [InlineData(1, 2, 1, 2, 2, true)]         // 季集全等
    [InlineData(2, 2, 1, 2, 2, false)]        // 有季号必须相等：S2E2 不归属 S1E2
    [InlineData(null, null, 1, 2, 2, false)]  // 无集号一律不归属
    [InlineData(null, 3, 1, 2, 4, true)]      // 双集合并区间 [2,4] 内命中
    [InlineData(null, 5, 1, 2, 4, false)]     // 超出双集区间
    public void MatchesEpisode_SeasonEpisodeRules_ReturnsExpected(
        int? subSeason, int? subEpisode, int season, int episode, int episodeEnd, bool expected)
    {
        SubtitleRenamer.MatchesEpisode(subSeason, subEpisode, season, episode, episodeEnd).Should().Be(expected);
    }

    // ---------- PathHint 语言回退（「Subs/CHS/xxx.srt」按语言分目录结构，走 Plan） ----------

    [Fact]
    public void Plan_UndFileNameWithPathHint_FallsBackToDirectorySegmentLanguage()
    {
        // 文件名「track1」识别为 und，回退用目录段「CHS」识别为 zh
        SubtitleCandidate sub = new("/in/Subs/CHS/track1.srt", "track1.srt", 100, PathHint: "Subs/CHS");
        IReadOnlyList<SubtitleRenameDecision> plan = SubtitleRenamer.Plan("Foo (2020)", [sub]);

        plan.Should().HaveCount(1);
        plan[0].TargetFileName.Should().Be("Foo (2020).zh.srt");
    }

    [Fact]
    public void Plan_FileNameLanguage_TakesPriorityOverPathHint()
    {
        // 文件名已识别出 CHS → zh，目录段「ENG」不覆盖
        SubtitleCandidate sub = new("/in/Subs/ENG/Foo.CHS.srt", "Foo.CHS.srt", 100, PathHint: "Subs/ENG");
        IReadOnlyList<SubtitleRenameDecision> plan = SubtitleRenamer.Plan("Bar (2021)", [sub]);

        plan[0].TargetFileName.Should().Be("Bar (2021).zh.srt");
    }

    [Fact]
    public void Plan_UndFileNameWithoutPathHint_KeepsUnd()
    {
        // 无 PathHint（视频同目录字幕）时不回退，保持 und
        SubtitleCandidate sub = new("/in/track1.srt", "track1.srt", 100, PathHint: null);
        IReadOnlyList<SubtitleRenameDecision> plan = SubtitleRenamer.Plan("Baz (2022)", [sub]);

        plan[0].TargetFileName.Should().Be("Baz (2022).und.srt");
    }

    [Fact]
    public void Plan_PathHintChineseSegment_RecognizedAsZh()
    {
        // 中文目录段「简体」按「简」token 识别为 zh
        SubtitleCandidate sub = new("/in/Subs/简体/track1.srt", "track1.srt", 100, PathHint: "Subs/简体");
        IReadOnlyList<SubtitleRenameDecision> plan = SubtitleRenamer.Plan("Qux (2023)", [sub]);

        plan[0].TargetFileName.Should().Be("Qux (2023).zh.srt");
    }
}
