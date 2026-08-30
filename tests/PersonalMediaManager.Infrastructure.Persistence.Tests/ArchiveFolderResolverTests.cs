using Microsoft.Extensions.Logging.Abstractions;
using PersonalMediaManager.Infrastructure.Persistence.Services.Archive;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>归档落点解析（ArchiveFolderResolver）纯单元测试</summary>
/// <remarks>
/// 覆盖 FirstNonBlank / ResolveSeasonNaming / ResolveShowFolder / BuildRelativeTargetPath 的实际行为：
/// {tmdb-NNN} 唯一性复用、前缀粘连防护、多匹配确定性 tie-break、大小写兼容、季年/季标题、
/// 扫描失败回退、电影 vs 剧集路径形态、双集范围文件名。
/// 涉及目录扫描的用例用临时目录（构造时建、Dispose 递归删），不依赖数据库。
/// 路径断言一律用 Path.Combine 构造期望值，与运行平台分隔符保持一致。
/// </remarks>
public sealed class ArchiveFolderResolverTests : IDisposable
{
    /// <summary>本测试类独占的临时分类根（每实例一个 GUID 目录，Dispose 时递归删）</summary>
    private readonly string _tempRoot;

    public ArchiveFolderResolverTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // 清理失败不影响测试结论（临时目录最终由 OS 回收）
        }
    }

    /// <summary>在临时分类根下建一个子目录并返回其名字（便于构造「已存在目录」场景）</summary>
    private void MakeChildDir(string name) => Directory.CreateDirectory(Path.Combine(_tempRoot, name));

    // ─────────────────────────── FirstNonBlank ───────────────────────────

    [Fact(DisplayName = "FirstNonBlank：取首个非空白值，跳过 null / 空串 / 纯空白")]
    public void FirstNonBlank_取首个非空白值()
    {
        ArchiveFolderResolver.FirstNonBlank(null, "", "   ", "命中", "其后").Should().Be("命中");
    }

    [Fact(DisplayName = "FirstNonBlank：全空白返回 null")]
    public void FirstNonBlank_全空白返回Null()
    {
        ArchiveFolderResolver.FirstNonBlank(null, "", "  ").Should().BeNull();
    }

    // ───────────────────── ResolveShowFolder（① 复用） ─────────────────────

    [Fact(DisplayName = "① {tmdb-NNN} 唯一性复用：已存在同 tmdbId 目录则复用，不新建第二个")]
    public void ResolveShowFolder_命中已存在tmdb目录则复用()
    {
        // 历史目录标题段与本次规范名不同（中文剧名），但 {tmdb-1396} 相同
        const string existing = "绝命毒师 (2008) {tmdb-1396}";
        MakeChildDir(existing);
        const string canonical = "Breaking Bad (2008) {tmdb-1396}";

        string chosen = ArchiveFolderResolver.ResolveShowFolder(
            _tempRoot, tmdbId: 1396, canonicalShowFolder: canonical, NullLogger.Instance);

        // 复用已存在目录，绝不落到规范名（杜绝同一作品散落两个文件夹）
        chosen.Should().Be(existing);
    }

    [Fact(DisplayName = "② 前缀粘连防护：{tmdb-1} 不误命中 {tmdb-1396}（闭合花括号边界）")]
    public void ResolveShowFolder_闭合花括号防前缀粘连()
    {
        // 只存在 {tmdb-1396} 目录；查 tmdbId=1 时 marker 为 {tmdb-1}，闭合花括号使其无法命中 {tmdb-1396}
        MakeChildDir("Breaking Bad (2008) {tmdb-1396}");
        const string canonical = "Some Show (2020) {tmdb-1}";

        string chosen = ArchiveFolderResolver.ResolveShowFolder(
            _tempRoot, tmdbId: 1, canonicalShowFolder: canonical, NullLogger.Instance);

        // 未命中 → 回退规范名新建，不会错误复用 1396
        chosen.Should().Be(canonical);
    }

    [Fact(DisplayName = "③ 多匹配 tie-break：精确等于规范名者优先（即便字典序不是最小）")]
    public void ResolveShowFolder_多匹配优先精确等于规范名()
    {
        const string canonical = "Breaking Bad (2008) {tmdb-1396}";
        // 同 tmdbId 散落多个目录：一个恰为规范名，另两个字典序更靠前
        MakeChildDir("AAA Other (2008) {tmdb-1396}");
        MakeChildDir(canonical);
        MakeChildDir("绝命毒师 (2008) {tmdb-1396}");

        string chosen = ArchiveFolderResolver.ResolveShowFolder(
            _tempRoot, tmdbId: 1396, canonicalShowFolder: canonical, NullLogger.Instance);

        // 多匹配时精确等于规范名优先，而非字典序最小（"AAA..."）
        chosen.Should().Be(canonical);
    }

    [Fact(DisplayName = "③ 多匹配 tie-break：无精确规范名则取字典序（Ordinal）最小")]
    public void ResolveShowFolder_多匹配无规范名取字典序最小()
    {
        // 三个散落目录均不等于规范名 → 取 Ordinal 排序最小者，保证后续文件落点确定
        MakeChildDir("ZZZ Show (2008) {tmdb-1396}");
        MakeChildDir("AAA Show (2008) {tmdb-1396}");
        MakeChildDir("MMM Show (2008) {tmdb-1396}");
        const string canonical = "Breaking Bad (2008) {tmdb-1396}";

        string chosen = ArchiveFolderResolver.ResolveShowFolder(
            _tempRoot, tmdbId: 1396, canonicalShowFolder: canonical, NullLogger.Instance);

        chosen.Should().Be("AAA Show (2008) {tmdb-1396}");
    }

    [Fact(DisplayName = "④ 大小写兼容：历史目录写成大写 {TMDB-NNN} 仍按 OrdinalIgnoreCase 命中复用")]
    public void ResolveShowFolder_大写标记忽略大小写命中复用()
    {
        // 历史目录用大写 {TMDB-1396}；marker 匹配走 OrdinalIgnoreCase 应命中
        const string existing = "Breaking Bad (2008) {TMDB-1396}";
        MakeChildDir(existing);
        const string canonical = "Breaking Bad (2008) {tmdb-1396}";

        string chosen = ArchiveFolderResolver.ResolveShowFolder(
            _tempRoot, tmdbId: 1396, canonicalShowFolder: canonical, NullLogger.Instance);

        // 命中但与规范名大小写不同（tie-break 精确比较用 Ordinal，故不等）→ 复用已存在大写目录
        chosen.Should().Be(existing);
    }

    [Fact(DisplayName = "⑥ 分类根不存在 → 回退规范名新建，不抛异常")]
    public void ResolveShowFolder_分类根不存在回退规范名()
    {
        string notExist = Path.Combine(_tempRoot, "不存在的子目录", Guid.NewGuid().ToString("N"));
        const string canonical = "Breaking Bad (2008) {tmdb-1396}";

        Func<string> act = () => ArchiveFolderResolver.ResolveShowFolder(
            notExist, tmdbId: 1396, canonicalShowFolder: canonical, NullLogger.Instance);

        act.Should().NotThrow();
        act().Should().Be(canonical);
    }

    [Fact(DisplayName = "⑥ 分类根存在但无任何匹配目录 → 回退规范名新建")]
    public void ResolveShowFolder_无匹配目录回退规范名()
    {
        // 根下有别的作品目录，但没有 {tmdb-1396}
        MakeChildDir("Other Show (2010) {tmdb-9999}");
        const string canonical = "Breaking Bad (2008) {tmdb-1396}";

        string chosen = ArchiveFolderResolver.ResolveShowFolder(
            _tempRoot, tmdbId: 1396, canonicalShowFolder: canonical, NullLogger.Instance);

        chosen.Should().Be(canonical);
    }

    [Fact(DisplayName = "⑥ 扫描失败（传入的是文件而非目录）→ 回退规范名，绝不抛异常")]
    public void ResolveShowFolder_目标是文件时回退规范名()
    {
        // 用一个已存在的文件路径冒充分类根：Directory.Exists 为 false → 直接回退（同样验证不抛）
        string filePath = Path.Combine(_tempRoot, "我是文件.txt");
        File.WriteAllText(filePath, "占位");
        const string canonical = "Breaking Bad (2008) {tmdb-1396}";

        Func<string> act = () => ArchiveFolderResolver.ResolveShowFolder(
            filePath, tmdbId: 1396, canonicalShowFolder: canonical, NullLogger.Instance);

        act.Should().NotThrow();
        act().Should().Be(canonical);
    }

    [Fact(DisplayName = "ResolveShowFolder：logger 传 null 也安全（预览侧无 logger）")]
    public void ResolveShowFolder_NullLogger安全()
    {
        MakeChildDir("绝命毒师 (2008) {tmdb-1396}");
        const string canonical = "Breaking Bad (2008) {tmdb-1396}";

        Func<string> act = () => ArchiveFolderResolver.ResolveShowFolder(
            _tempRoot, tmdbId: 1396, canonicalShowFolder: canonical, logger: null);

        act.Should().NotThrow();
        act().Should().Be("绝命毒师 (2008) {tmdb-1396}");
    }

    // ───────────────── ResolveSeasonNaming（⑤ 季年 / 季标题） ─────────────────

    [Fact(DisplayName = "⑤ ResolveSeasonNaming：TMDB 季名 + 首播年命中该季")]
    public void ResolveSeasonNaming_命中季取TMDB季名与首播年()
    {
        // seasons[] 含 season 2：air_date 2021、季名「锻刀村篇」（非默认季名 → 保留）
        const string rawJson = """
        {
          "seasons": [
            { "season_number": 1, "episode_count": 26, "name": "无限列车篇", "air_date": "2019-04-06" },
            { "season_number": 2, "episode_count": 18, "name": "锻刀村篇", "air_date": "2021-12-05" }
          ]
        }
        """;

        (int? year, string? title) = ArchiveFolderResolver.ResolveSeasonNaming(rawJson, parsedSeasonTitle: null, season: 2);

        year.Should().Be(2021);
        title.Should().Be("锻刀村篇");
    }

    [Fact(DisplayName = "⑤ ResolveSeasonNaming：TMDB 默认季名（第2季）被过滤 → 回退解析篇章名")]
    public void ResolveSeasonNaming_默认季名过滤回退解析篇章名()
    {
        // TMDB 季名是「第 2 季」这类默认季号别名 → 过滤；回退到文件解析的篇章名
        const string rawJson = """
        {
          "seasons": [
            { "season_number": 2, "episode_count": 12, "name": "第 2 季", "air_date": "2020-10-01" }
          ]
        }
        """;

        (int? year, string? title) = ArchiveFolderResolver.ResolveSeasonNaming(rawJson, parsedSeasonTitle: "游郭篇", season: 2);

        // 年份仍取 TMDB air_date 年；标题回退解析篇章名
        year.Should().Be(2020);
        title.Should().Be("游郭篇");
    }

    [Fact(DisplayName = "⑤ ResolveSeasonNaming：rawJson 为空 → 季年 null + 仅看解析篇章名")]
    public void ResolveSeasonNaming_空Json回退解析篇章名年份Null()
    {
        (int? year, string? title) = ArchiveFolderResolver.ResolveSeasonNaming(rawJson: null, parsedSeasonTitle: "锻刀村篇", season: 2);

        year.Should().BeNull();
        title.Should().Be("锻刀村篇");
    }

    [Fact(DisplayName = "⑤ ResolveSeasonNaming：rawJson 无该季 → 季年 null，标题回退解析篇章名")]
    public void ResolveSeasonNaming_无该季回退()
    {
        const string rawJson = """
        { "seasons": [ { "season_number": 1, "episode_count": 26, "name": "无限列车篇", "air_date": "2019-04-06" } ] }
        """;

        (int? year, string? title) = ArchiveFolderResolver.ResolveSeasonNaming(rawJson, parsedSeasonTitle: "锻刀村篇", season: 5);

        year.Should().BeNull();
        title.Should().Be("锻刀村篇");
    }

    [Fact(DisplayName = "⑤ ResolveSeasonNaming：TMDB 季名与解析名俱无 → 标题 null")]
    public void ResolveSeasonNaming_俱无返回Null标题()
    {
        const string rawJson = """
        { "seasons": [ { "season_number": 2, "episode_count": 12, "air_date": "2020-10-01" } ] }
        """;

        (int? year, string? title) = ArchiveFolderResolver.ResolveSeasonNaming(rawJson, parsedSeasonTitle: null, season: 2);

        year.Should().Be(2020);
        title.Should().BeNull();
    }

    // ─────────────── BuildRelativeTargetPath（⑦ 形态 / ⑧ 双集 / 复用联动） ───────────────

    [Fact(DisplayName = "⑦ 电影路径形态：{标题 (年) {tmdb}}\\{标题 (年) {tmdb}}.ext，忽略 season/episode")]
    public void BuildRelativeTargetPath_电影形态()
    {
        // targetRootForReuse 传 null → 仅做标题规范化、不扫描
        string rel = ArchiveFolderResolver.BuildRelativeTargetPath(
            canonicalTitle: "Inception", year: 2010, tmdbId: 27205, isTv: false,
            season: 99, episode: 99, episodeEnd: null, extension: "MKV",
            targetRootForReuse: null);

        // 电影：目录与文件名同带 {tmdb-NNN}；扩展名归一化为小写；season/episode 被忽略
        string expected = Path.Combine(
            "Inception (2010) {tmdb-27205}",
            "Inception (2010) {tmdb-27205}.mkv");
        rel.Should().Be(expected);
    }

    [Fact(DisplayName = "⑦ 剧集路径形态：剧集根 / Season NN / 单集文件三段，标记仅落剧集根")]
    public void BuildRelativeTargetPath_剧集单集形态()
    {
        string rel = ArchiveFolderResolver.BuildRelativeTargetPath(
            canonicalTitle: "Breaking Bad", year: 2008, tmdbId: 1396, isTv: true,
            season: 1, episode: 1, episodeEnd: null, extension: ".mp4",
            targetRootForReuse: null);

        // 剧集根带 {tmdb}；季目录 Season 01 不带标记；单集文件名 SxxEyy 也不带标记
        string expected = Path.Combine(
            "Breaking Bad (2008) {tmdb-1396}",
            "Season 01",
            "Breaking Bad (2008) - S01E01.mp4");
        rel.Should().Be(expected);
    }

    [Fact(DisplayName = "⑧ 双集范围文件名：episodeEnd > episode → SxxEaa-Ebb")]
    public void BuildRelativeTargetPath_双集范围文件名()
    {
        string rel = ArchiveFolderResolver.BuildRelativeTargetPath(
            canonicalTitle: "Show", year: 2020, tmdbId: 555, isTv: true,
            season: 1, episode: 8, episodeEnd: 9, extension: "mkv",
            targetRootForReuse: null);

        string expected = Path.Combine(
            "Show (2020) {tmdb-555}",
            "Season 01",
            "Show (2020) - S01E08-E09.mkv");
        rel.Should().Be(expected);
    }

    [Fact(DisplayName = "⑧ episodeEnd == episode → 退化为单集 SxxEyy（无范围后缀）")]
    public void BuildRelativeTargetPath_双集相等退化单集()
    {
        string rel = ArchiveFolderResolver.BuildRelativeTargetPath(
            canonicalTitle: "Show", year: 2020, tmdbId: 555, isTv: true,
            season: 1, episode: 8, episodeEnd: 8, extension: "mkv",
            targetRootForReuse: null);

        string expected = Path.Combine(
            "Show (2020) {tmdb-555}",
            "Season 01",
            "Show (2020) - S01E08.mkv");
        rel.Should().Be(expected);
    }

    [Fact(DisplayName = "⑤ BuildRelativeTargetPath：多季季年 + 季标题（季目录带篇章名、文件名用该季首播年）")]
    public void BuildRelativeTargetPath_多季季年与季标题()
    {
        string rel = ArchiveFolderResolver.BuildRelativeTargetPath(
            canonicalTitle: "鬼灭之刃", year: 2019, tmdbId: 85937, isTv: true,
            season: 2, episode: 1, episodeEnd: null, extension: "mkv",
            targetRootForReuse: null, logger: null,
            seasonYear: 2021, seasonTitle: "锻刀村篇");

        // 剧集根仍用整剧年 2019；季目录带季标题；单集文件名年份用该季首播年 2021
        string expected = Path.Combine(
            "鬼灭之刃 (2019) {tmdb-85937}",
            "Season 02 锻刀村篇",
            "鬼灭之刃 (2021) - S02E01.mkv");
        rel.Should().Be(expected);
    }

    [Fact(DisplayName = "⑤ BuildRelativeTargetPath：seasonYear 缺失 → 文件名年份回退整剧年")]
    public void BuildRelativeTargetPath_季年缺失回退整剧年()
    {
        string rel = ArchiveFolderResolver.BuildRelativeTargetPath(
            canonicalTitle: "鬼灭之刃", year: 2019, tmdbId: 85937, isTv: true,
            season: 2, episode: 1, episodeEnd: null, extension: "mkv",
            targetRootForReuse: null, logger: null,
            seasonYear: null, seasonTitle: "锻刀村篇");

        // seasonYear 为 null → 单集文件名年份用整剧 year 2019
        string expected = Path.Combine(
            "鬼灭之刃 (2019) {tmdb-85937}",
            "Season 02 锻刀村篇",
            "鬼灭之刃 (2019) - S02E01.mkv");
        rel.Should().Be(expected);
    }

    [Fact(DisplayName = "① BuildRelativeTargetPath 联动复用：传分类根时整段路径落到已存在 {tmdb} 目录")]
    public void BuildRelativeTargetPath_传分类根时复用已存在目录()
    {
        // 历史中文目录已存在；本次规范名是英文，但 {tmdb-1396} 命中 → 整段相对路径前缀复用历史目录
        const string existing = "绝命毒师 (2008) {tmdb-1396}";
        MakeChildDir(existing);

        string rel = ArchiveFolderResolver.BuildRelativeTargetPath(
            canonicalTitle: "Breaking Bad", year: 2008, tmdbId: 1396, isTv: true,
            season: 1, episode: 2, episodeEnd: null, extension: "mkv",
            targetRootForReuse: _tempRoot, logger: NullLogger.Instance);

        // 剧集根段复用已存在中文目录；季 / 单集段仍按规范名生成
        string expected = Path.Combine(
            existing,
            "Season 01",
            "Breaking Bad (2008) - S01E02.mkv");
        rel.Should().Be(expected);
    }

    [Fact(DisplayName = "① 电影 BuildRelativeTargetPath 联动复用：电影目录同样按 {tmdb} 复用")]
    public void BuildRelativeTargetPath_电影传分类根时复用()
    {
        const string existing = "盗梦空间 (2010) {tmdb-27205}";
        MakeChildDir(existing);

        string rel = ArchiveFolderResolver.BuildRelativeTargetPath(
            canonicalTitle: "Inception", year: 2010, tmdbId: 27205, isTv: false,
            season: 0, episode: 0, episodeEnd: null, extension: "mkv",
            targetRootForReuse: _tempRoot, logger: NullLogger.Instance);

        // 电影目录段复用历史目录；文件名段按规范名（仍带 {tmdb} 标记）
        string expected = Path.Combine(
            existing,
            "Inception (2010) {tmdb-27205}.mkv");
        rel.Should().Be(expected);
    }
}
