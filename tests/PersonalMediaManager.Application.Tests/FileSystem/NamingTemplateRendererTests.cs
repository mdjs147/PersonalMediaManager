using PersonalMediaManager.Application.Common.Archiving;

namespace PersonalMediaManager.Application.Tests.FileSystem;

/// <summary>归档命名模板渲染 + 校验（NamingTemplateRenderer）单元测试</summary>
/// <remarks>
/// 覆盖：占位符替换 / 缺值省段（含 edition 自动段）/ 数值裸数字 / 多空格与末尾点清理 /
/// {tmdb 字面量拒绝 / 路径分隔符拒绝 / 未知占位符拒绝 / 剧集单集必须含 {episode}。
/// 渲染值视为「已过 Sanitize」（调用方 PlexNamingConventions 负责清洗），故此处直接传干净值验证拼接逻辑。
/// </remarks>
public sealed class NamingTemplateRendererTests
{
    private static Dictionary<string, string?> Tokens(params (string Key, string? Value)[] pairs)
    {
        Dictionary<string, string?> d = new(StringComparer.Ordinal);
        foreach ((string k, string? v) in pairs) d[k] = v;
        return d;
    }

    // ---------- RenderToken ----------

    [Fact]
    public void RenderToken_DefaultMovieTemplate_SubstitutesTitleAndYear()
    {
        string r = NamingTemplateRenderer.RenderToken(
            NamingTemplateOptions.DefaultMovieNameTemplate,
            Tokens(("title", "盗梦空间"), ("year", "2010")));
        r.Should().Be("盗梦空间 (2010)");
    }

    [Fact]
    public void RenderToken_OriginalTitleToken_Substituted()
    {
        string r = NamingTemplateRenderer.RenderToken(
            "{originaltitle} ({year})",
            Tokens(("originaltitle", "Inception"), ("year", "2010")));
        r.Should().Be("Inception (2010)");
    }

    [Fact]
    public void RenderToken_TmdbIdToken_BareNumber()
    {
        // {tmdbid} 是裸数字（≠ {tmdb-NNN} 锚点）；用户自定义模板可引用
        string r = NamingTemplateRenderer.RenderToken(
            "{title} [{tmdbid}]",
            Tokens(("title", "X"), ("tmdbid", "27205")));
        r.Should().Be("X [27205]");
    }

    [Fact]
    public void RenderToken_MissingEdition_OmitsLeadingSeparatorSegment()
    {
        // " - {edition}" 在 edition 缺值（null）时整段连同 ' - ' 一并省略 → 与 MovieFileBase 无 edition 时逐字节一致
        string r = NamingTemplateRenderer.RenderToken(
            "{title} ({year}) - {edition}",
            Tokens(("title", "盗梦空间"), ("year", "2010"), ("edition", null)));
        r.Should().Be("盗梦空间 (2010)");
    }

    [Fact]
    public void RenderToken_PresentEdition_RendersSegment()
    {
        string r = NamingTemplateRenderer.RenderToken(
            "{title} ({year}) - {edition}",
            Tokens(("title", "盗梦空间"), ("year", "2010"), ("edition", "导演剪辑版")));
        r.Should().Be("盗梦空间 (2010) - 导演剪辑版");
    }

    [Fact]
    public void RenderToken_EpisodeToken_RenderedAsGiven()
    {
        // {episode} 由调用方传入已格式化的 SxxEyy（FormatEpisodeToken 产出），渲染器只做替换
        string r = NamingTemplateRenderer.RenderToken(
            "{title} ({year}) {episode}",
            Tokens(("title", "鬼灭之刃"), ("year", "2019"), ("episode", "S03E01")));
        r.Should().Be("鬼灭之刃 (2019) S03E01");
    }

    [Fact]
    public void RenderToken_CollapsesMultiSpace_AndTrimsTrailingDotSpace()
    {
        string r = NamingTemplateRenderer.RenderToken(
            "{title}   ({year}) .",
            Tokens(("title", "A"), ("year", "2020")));
        r.Should().Be("A (2020)");
    }

    [Fact]
    public void RenderToken_AllTokensMissing_NoLiteral_Throws()
    {
        Action act = () => NamingTemplateRenderer.RenderToken(
            "{edition}",
            Tokens(("edition", null)));
        act.Should().Throw<ArgumentException>();
    }

    // ---------- ValidateTemplate ----------

    [Theory]
    [InlineData("{title} ({year})", NamingTemplateRenderer.TemplateKind.Movie)]
    [InlineData("{title} ({year}) - {edition}", NamingTemplateRenderer.TemplateKind.Movie)]
    [InlineData("{originaltitle} ({year})", NamingTemplateRenderer.TemplateKind.TvShowFolder)]
    [InlineData("{title} ({year}) - {episode}", NamingTemplateRenderer.TemplateKind.TvEpisode)]
    [InlineData("{title} {episode}", NamingTemplateRenderer.TemplateKind.TvEpisode)]
    public void ValidateTemplate_ValidTemplates_Pass(string template, NamingTemplateRenderer.TemplateKind kind)
    {
        NamingTemplateRenderer.ValidateTemplate(template, kind).IsValid.Should().BeTrue();
    }

    [Fact]
    public void ValidateTemplate_ContainsTmdbLiteral_Rejected()
    {
        NamingTemplateRenderer.ValidationOutcome r = NamingTemplateRenderer.ValidateTemplate("{title} ({year}) {tmdb-1}", NamingTemplateRenderer.TemplateKind.Movie);
        r.IsValid.Should().BeFalse();
        r.Errors.Should().Contain(e => e.Contains("{tmdb"));
    }

    [Theory]
    [InlineData("{title}/{year}")]
    [InlineData("{title}\\{year}")]
    public void ValidateTemplate_ContainsPathSeparator_Rejected(string template)
    {
        NamingTemplateRenderer.ValidationOutcome r = NamingTemplateRenderer.ValidateTemplate(template, NamingTemplateRenderer.TemplateKind.Movie);
        r.IsValid.Should().BeFalse();
        r.Errors.Should().Contain(e => e.Contains("路径分隔符"));
    }

    [Fact]
    public void ValidateTemplate_UnknownPlaceholder_Rejected()
    {
        NamingTemplateRenderer.ValidationOutcome r = NamingTemplateRenderer.ValidateTemplate("{title} ({year}) {foobar}", NamingTemplateRenderer.TemplateKind.Movie);
        r.IsValid.Should().BeFalse();
        r.Errors.Should().Contain(e => e.Contains("foobar"));
    }

    [Fact]
    public void ValidateTemplate_EpisodeKind_MissingEpisodeToken_Rejected()
    {
        NamingTemplateRenderer.ValidationOutcome r = NamingTemplateRenderer.ValidateTemplate("{title} ({year})", NamingTemplateRenderer.TemplateKind.TvEpisode);
        r.IsValid.Should().BeFalse();
        r.Errors.Should().Contain(e => e.Contains("{episode}"));
    }

    [Fact]
    public void ValidateTemplate_EditionInTvShowFolder_Rejected()
    {
        // {edition} 仅电影适用；剧集根目录模板用它属未知占位符
        NamingTemplateRenderer.ValidationOutcome r = NamingTemplateRenderer.ValidateTemplate("{title} ({year}) - {edition}", NamingTemplateRenderer.TemplateKind.TvShowFolder);
        r.IsValid.Should().BeFalse();
        r.Errors.Should().Contain(e => e.Contains("edition"));
    }

    [Fact]
    public void ValidateTemplate_EpisodeTokenInMovie_Rejected()
    {
        NamingTemplateRenderer.ValidationOutcome r = NamingTemplateRenderer.ValidateTemplate("{title} ({year}) {episode}", NamingTemplateRenderer.TemplateKind.Movie);
        r.IsValid.Should().BeFalse();
        r.Errors.Should().Contain(e => e.Contains("episode"));
    }

    [Fact]
    public void ValidateTemplate_Blank_Rejected()
    {
        NamingTemplateRenderer.ValidateTemplate("   ", NamingTemplateRenderer.TemplateKind.Movie).IsValid.Should().BeFalse();
        NamingTemplateRenderer.ValidateTemplate(null, NamingTemplateRenderer.TemplateKind.Movie).IsValid.Should().BeFalse();
    }
}
