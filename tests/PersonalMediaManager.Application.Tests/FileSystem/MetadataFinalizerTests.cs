using System.Xml;
using PersonalMediaManager.Application.Common.Archiving;

namespace PersonalMediaManager.Application.Tests.FileSystem;

/// <summary>MetadataFinalizer（D4.4）— movie/tvshow/episode nfo + 海报拷贝 + XML 合法性校验</summary>
public sealed class MetadataFinalizerTests : IDisposable
{
    private readonly string _workDir;

    public MetadataFinalizerTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"pmm-meta-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task MovieNfo_AllFields_WellFormedAndContainsTmdbId()
    {
        string path = Path.Combine(_workDir, "movie.nfo");
        await MetadataFinalizer.WriteMovieNfoAsync(path, new MovieNfoData(
            TmdbId: 27205,
            Title: "盗梦空间",
            OriginalTitle: "Inception",
            Year: 2010,
            Plot: "梦中梦",
            OriginCountry: "US",
            Genres: ["Action", "Sci-Fi"]));

        XmlDocument doc = new();
        doc.Load(path);   // 抛 XmlException 则测试失败
        doc.DocumentElement!.Name.Should().Be("movie");
        doc.SelectSingleNode("/movie/title")!.InnerText.Should().Be("盗梦空间");
        doc.SelectSingleNode("/movie/originaltitle")!.InnerText.Should().Be("Inception");
        doc.SelectSingleNode("/movie/year")!.InnerText.Should().Be("2010");
        doc.SelectSingleNode("/movie/id")!.InnerText.Should().Be("27205");
        doc.SelectSingleNode("/movie/uniqueid[@type='tmdb']")!.InnerText.Should().Be("27205");
        doc.SelectNodes("/movie/genre")!.Count.Should().Be(2);
    }

    [Fact]
    public async Task MovieNfo_NullOptionalFields_OmittedFromOutput()
    {
        string path = Path.Combine(_workDir, "minimal.nfo");
        await MetadataFinalizer.WriteMovieNfoAsync(path, new MovieNfoData(
            TmdbId: 1, Title: "X", OriginalTitle: null, Year: null, Plot: null, OriginCountry: null, Genres: null));

        XmlDocument doc = new();
        doc.Load(path);
        doc.SelectSingleNode("/movie/originaltitle").Should().BeNull("null 字段不应输出元素");
        doc.SelectSingleNode("/movie/year").Should().BeNull();
        doc.SelectSingleNode("/movie/plot").Should().BeNull();
    }

    [Fact]
    public async Task MovieNfo_TitleWithXmlSpecialChars_ProperlyEscaped()
    {
        string path = Path.Combine(_workDir, "esc.nfo");
        await MetadataFinalizer.WriteMovieNfoAsync(path, new MovieNfoData(
            TmdbId: 1, Title: "A & B <C>", OriginalTitle: null, Year: 2020, Plot: "x & y",
            OriginCountry: null, Genres: null));

        XmlDocument doc = new();
        doc.Load(path);   // 文件如果转义错误这里就抛
        doc.SelectSingleNode("/movie/title")!.InnerText.Should().Be("A & B <C>", "XmlWriter API 自动转义");

        string raw = await File.ReadAllTextAsync(path);
        raw.Should().Contain("&amp;").And.Contain("&lt;");
    }

    [Fact]
    public async Task TvShowNfo_HasTotalSeasons_AndCorrectRootName()
    {
        string path = Path.Combine(_workDir, "tvshow.nfo");
        await MetadataFinalizer.WriteTvShowNfoAsync(path, new TvShowNfoData(
            TmdbId: 1396, Title: "绝命毒师", OriginalTitle: "Breaking Bad",
            Year: 2008, TotalSeasons: 5, Plot: null, OriginCountry: "US", Genres: ["Drama"]));

        XmlDocument doc = new();
        doc.Load(path);
        doc.DocumentElement!.Name.Should().Be("tvshow");
        doc.SelectSingleNode("/tvshow/season")!.InnerText.Should().Be("5");
        doc.SelectSingleNode("/tvshow/country")!.InnerText.Should().Be("US");
    }

    [Fact]
    public async Task EpisodeNfo_SeasonEpisodeRequired_OptionalShowTmdbId()
    {
        string path = Path.Combine(_workDir, "ep.nfo");
        await MetadataFinalizer.WriteEpisodeNfoAsync(path, new EpisodeNfoData(
            Season: 1, Episode: 7, Title: "Pilot", Plot: null, ShowTmdbId: 1396));

        XmlDocument doc = new();
        doc.Load(path);
        doc.DocumentElement!.Name.Should().Be("episodedetails");
        doc.SelectSingleNode("/episodedetails/season")!.InnerText.Should().Be("1");
        doc.SelectSingleNode("/episodedetails/episode")!.InnerText.Should().Be("7");
        doc.SelectSingleNode("/episodedetails/title")!.InnerText.Should().Be("Pilot");
        doc.SelectSingleNode("/episodedetails/uniqueid")!.InnerText.Should().Be("1396");
    }

    [Fact]
    public async Task EpisodeNfo_NoTmdbId_NoUniqueidElement()
    {
        string path = Path.Combine(_workDir, "ep2.nfo");
        await MetadataFinalizer.WriteEpisodeNfoAsync(path, new EpisodeNfoData(
            Season: 0, Episode: 5, Title: null, Plot: null, ShowTmdbId: null));

        XmlDocument doc = new();
        doc.Load(path);
        doc.SelectSingleNode("/episodedetails/uniqueid").Should().BeNull();
        doc.SelectSingleNode("/episodedetails/season")!.InnerText.Should().Be("0", "S00 特别篇");
    }

    [Fact]
    public async Task CopyPoster_NestedDir_CreatedAutomatically()
    {
        string src = Path.Combine(_workDir, "src", "27205.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(src)!);
        await File.WriteAllBytesAsync(src, [1, 2, 3, 4, 5]);

        string dst = Path.Combine(_workDir, "out", "deep", "poster.jpg");
        await MetadataFinalizer.CopyPosterAsync(src, dst);

        File.Exists(dst).Should().BeTrue();
        (await File.ReadAllBytesAsync(dst)).Should().Equal([1, 2, 3, 4, 5]);
    }

    [Fact]
    public async Task CopyPoster_TargetExists_Overwrites()
    {
        string src = Path.Combine(_workDir, "src.jpg");
        await File.WriteAllBytesAsync(src, [9, 9, 9]);
        string dst = Path.Combine(_workDir, "dst.jpg");
        await File.WriteAllBytesAsync(dst, [1, 1]);

        await MetadataFinalizer.CopyPosterAsync(src, dst);

        (await File.ReadAllBytesAsync(dst)).Should().Equal([9, 9, 9], "海报允许覆盖（重跑归档应该用最新数据）");
    }

    [Fact]
    public async Task CopyPoster_MissingSource_ThrowsFileNotFound()
    {
        await ((Func<Task>)(() => MetadataFinalizer.CopyPosterAsync("/ghost.jpg", Path.Combine(_workDir, "d.jpg"))))
            .Should().ThrowAsync<FileNotFoundException>();
    }
}
