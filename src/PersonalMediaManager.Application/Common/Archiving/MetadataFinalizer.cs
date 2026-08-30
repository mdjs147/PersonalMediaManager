using System.Text;
using System.Xml;

namespace PersonalMediaManager.Application.Common.Archiving;

/// <summary>nfo + 海报 + fanart + season-poster 生成（D4.4，§3.6.4）</summary>
/// <remarks>
/// 责任：
/// - 生成 Kodi/Plex/Emby 通用约定的 .nfo XML（电影 movie / 剧集根 tvshow / 单集 episodedetails）
/// - 拷贝海报文件（poster.jpg / fanart.jpg / seasonNN-poster.jpg）到目标目录
/// - 写出后用 XmlDocument.Load 校验合法（防御 TMDB 注入 / 编码问题）
/// 不负责：
/// - 海报下载（由 IPosterDownloader 缓存到 &lt;AppData&gt;/cache/posters/）
/// - 主视频与字幕的归档（由 IFileMover + SubtitleRenamer + ArchiveService 编排）
///
/// XML 编码：UTF-8 + XmlWriterSettings.Indent=true，便于人工查看。
/// 字符转义：用 XmlWriter API（不手拼字符串），自动转义 &lt; &gt; &amp; ' "。
/// </remarks>
public static class MetadataFinalizer
{
    /// <summary>写电影 .nfo（含 TmdbId / Title / OriginalTitle / Year / Plot / Genres / OriginCountry）</summary>
    public static async Task WriteMovieNfoAsync(string nfoPath, MovieNfoData data, CancellationToken ct = default)
    {
        EnsureDirectory(nfoPath);
        await WriteAndValidateAsync(nfoPath, writer =>
        {
            writer.WriteStartElement("movie");
            WriteSimple(writer, "title", data.Title);
            WriteSimpleIfNotEmpty(writer, "originaltitle", data.OriginalTitle);
            if (data.Year is not null) WriteSimple(writer, "year", data.Year.Value.ToString());
            WriteSimpleIfNotEmpty(writer, "plot", data.Plot);
            WriteSimpleIfNotEmpty(writer, "country", data.OriginCountry);

            // Kodi 约定：TmdbId 同时写 uniqueid + id
            writer.WriteStartElement("uniqueid");
            writer.WriteAttributeString("type", "tmdb");
            writer.WriteAttributeString("default", "true");
            writer.WriteString(data.TmdbId.ToString());
            writer.WriteEndElement();
            WriteSimple(writer, "id", data.TmdbId.ToString());

            if (data.Genres is not null)
            {
                foreach (string g in data.Genres.Where(s => !string.IsNullOrWhiteSpace(s)))
                    WriteSimple(writer, "genre", g);
            }
            writer.WriteEndElement();   // </movie>
        }, ct);
    }

    /// <summary>写剧集根 tvshow.nfo（不含单集信息）</summary>
    public static async Task WriteTvShowNfoAsync(string nfoPath, TvShowNfoData data, CancellationToken ct = default)
    {
        EnsureDirectory(nfoPath);
        await WriteAndValidateAsync(nfoPath, writer =>
        {
            writer.WriteStartElement("tvshow");
            WriteSimple(writer, "title", data.Title);
            WriteSimpleIfNotEmpty(writer, "originaltitle", data.OriginalTitle);
            if (data.Year is not null) WriteSimple(writer, "year", data.Year.Value.ToString());
            WriteSimpleIfNotEmpty(writer, "plot", data.Plot);
            WriteSimpleIfNotEmpty(writer, "country", data.OriginCountry);
            if (data.TotalSeasons is not null) WriteSimple(writer, "season", data.TotalSeasons.Value.ToString());

            writer.WriteStartElement("uniqueid");
            writer.WriteAttributeString("type", "tmdb");
            writer.WriteAttributeString("default", "true");
            writer.WriteString(data.TmdbId.ToString());
            writer.WriteEndElement();
            WriteSimple(writer, "id", data.TmdbId.ToString());

            if (data.Genres is not null)
            {
                foreach (string g in data.Genres.Where(s => !string.IsNullOrWhiteSpace(s)))
                    WriteSimple(writer, "genre", g);
            }
            writer.WriteEndElement();   // </tvshow>
        }, ct);
    }

    /// <summary>写单集 episodedetails.nfo（season / episode / 可选 title / plot）</summary>
    public static async Task WriteEpisodeNfoAsync(string nfoPath, EpisodeNfoData data, CancellationToken ct = default)
    {
        EnsureDirectory(nfoPath);
        await WriteAndValidateAsync(nfoPath, writer =>
        {
            writer.WriteStartElement("episodedetails");
            WriteSimpleIfNotEmpty(writer, "title", data.Title);
            WriteSimple(writer, "season", data.Season.ToString());
            WriteSimple(writer, "episode", data.Episode.ToString());
            WriteSimpleIfNotEmpty(writer, "plot", data.Plot);
            if (data.ShowTmdbId is not null)
            {
                writer.WriteStartElement("uniqueid");
                writer.WriteAttributeString("type", "tmdb");
                writer.WriteAttributeString("default", "true");
                writer.WriteString(data.ShowTmdbId.Value.ToString());
                writer.WriteEndElement();
            }
            writer.WriteEndElement();   // </episodedetails>
        }, ct);
    }

    /// <summary>拷贝海报到目标路径（覆盖已存在的同名文件，因为 nfo/poster 是「生成后可重生成」）</summary>
    /// <remarks>
    /// 与 IFileMover 不同：海报允许覆盖（重跑归档时元数据应被最新 TMDB 数据覆盖）。
    /// 源路径来自 IPosterDownloader 的本地缓存；本方法只做「从缓存复制到目标」。
    /// </remarks>
    public static async Task CopyPosterAsync(string sourceCachedPath, string targetPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceCachedPath))
            throw new ArgumentException("源海报路径不能为空", nameof(sourceCachedPath));
        if (!File.Exists(sourceCachedPath))
            throw new FileNotFoundException("源海报文件不存在（IPosterDownloader 应已缓存）", sourceCachedPath);
        EnsureDirectory(targetPath);

        await using FileStream src = new(sourceCachedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        await using FileStream dst = new(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await src.CopyToAsync(dst, 81920, ct);
        await dst.FlushAsync(ct);
    }

    private static async Task WriteAndValidateAsync(string path, Action<XmlWriter> writeBody, CancellationToken ct)
    {
        // 先到 MemoryStream → 校验 → 写入文件，避免半成品 nfo 落地
        using MemoryStream ms = new();
        XmlWriterSettings settings = new()
        {
            Indent = true,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Async = true,
        };
        await using (XmlWriter writer = XmlWriter.Create(ms, settings))
        {
            writer.WriteStartDocument();
            writeBody(writer);
            writer.WriteEndDocument();
            await writer.FlushAsync();
        }

        // 校验：用 XmlDocument 重新 Load 一次，捕获任何 well-formed 问题
        byte[] bytes = ms.ToArray();
        XmlDocument validator = new();
        using (MemoryStream readBack = new(bytes, writable: false))
            validator.Load(readBack);   // 抛 XmlException → 视为生成失败

        await File.WriteAllBytesAsync(path, bytes, ct);
    }

    private static void WriteSimple(XmlWriter writer, string name, string value)
    {
        writer.WriteStartElement(name);
        writer.WriteString(value);
        writer.WriteEndElement();
    }

    private static void WriteSimpleIfNotEmpty(XmlWriter writer, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        WriteSimple(writer, name, value);
    }

    private static void EnsureDirectory(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }
}

/// <summary>电影 nfo 数据</summary>
public sealed record MovieNfoData(
    int TmdbId,
    string Title,
    string? OriginalTitle,
    int? Year,
    string? Plot,
    string? OriginCountry,
    IReadOnlyList<string>? Genres);

/// <summary>剧集根 tvshow.nfo 数据</summary>
public sealed record TvShowNfoData(
    int TmdbId,
    string Title,
    string? OriginalTitle,
    int? Year,
    int? TotalSeasons,
    string? Plot,
    string? OriginCountry,
    IReadOnlyList<string>? Genres);

/// <summary>单集 episodedetails.nfo 数据</summary>
public sealed record EpisodeNfoData(
    int Season,
    int Episode,
    string? Title,
    string? Plot,
    int? ShowTmdbId);
