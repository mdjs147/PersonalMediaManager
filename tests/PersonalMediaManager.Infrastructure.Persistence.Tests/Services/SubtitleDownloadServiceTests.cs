using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.Subtitles;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Persistence.Interceptors;
using PersonalMediaManager.Infrastructure.Persistence.Services.Subtitles;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests.Services;

/// <summary>SubtitleDownloadService — 守门 / 下载落盘命名 / 语言回退 / 记录落库 / 关键词缺省</summary>
/// <remarks>
/// 真实临时目录承载归档视频与字幕落盘（命名避让按 File.Exists 判定，必须落真实磁盘）；
/// 字幕源走 NSubstitute 桩（ISubtitleProviderRegistry → ISubtitleProvider），不出网。
/// </remarks>
public sealed class SubtitleDownloadServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly IProtectedFieldService _protector = Substitute.For<IProtectedFieldService>();
    private readonly ISubtitleProviderRegistry _registry = Substitute.For<ISubtitleProviderRegistry>();
    private readonly ISubtitleProvider _provider = Substitute.For<ISubtitleProvider>();
    private readonly SubtitleDownloadService _sut;
    private readonly string _tempDir;

    public SubtitleDownloadServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbFactory = new TestDbContextFactory(_connection);
        using (PmmDbContext ctx = _dbFactory.CreateDbContext())
        {
            ctx.Database.EnsureCreated(); // Subtitle_Setting Id=1 种子由 HasData 注入
        }

        SetToken("enc-token");
        _protector.Unprotect("enc-token").Returns("plain-token");
        _registry.Resolve(SubtitleProviderType.Assrt).Returns(_provider);

        _sut = new SubtitleDownloadService(_dbFactory, _protector, _registry,
            NullLogger<SubtitleDownloadService>.Instance);

        _tempDir = Path.Combine(Path.GetTempPath(), $"pmm-subdl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        _connection.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ---------- ① 未归档记录拒绝 ----------

    [Fact(DisplayName = "守门：非 Completed 记录 → 拒绝下载")]
    public async Task Download_NotCompleted_Throws()
    {
        long id = SeedMedia(MediaItemStatus.Failed, targetPath: null);

        Func<Task> act = () => _sut.DownloadAsync(id, new DownloadSubtitleRequest("sub-1", -1, null));

        await act.Should().ThrowAsync<BusinessException>().WithMessage("仅已归档完成的记录可下载字幕");
    }

    [Fact(DisplayName = "守门：媒体记录不存在 → 拒绝")]
    public async Task Download_MediaMissing_Throws()
    {
        Func<Task> act = () => _sut.DownloadAsync(99999, new DownloadSubtitleRequest("sub-1", -1, null));

        await act.Should().ThrowAsync<BusinessException>().WithMessage("媒体记录不存在");
    }

    // ---------- ② TargetPath 文件不存在拒绝 ----------

    [Fact(DisplayName = "守门：TargetPath 指向的归档文件已不存在 → 拒绝")]
    public async Task Download_TargetFileMissing_Throws()
    {
        long id = SeedMedia(MediaItemStatus.Completed, targetPath: Path.Combine(_tempDir, "missing.mkv"));

        Func<Task> act = () => _sut.DownloadAsync(id, new DownloadSubtitleRequest("sub-1", -1, null));

        await act.Should().ThrowAsync<BusinessException>().WithMessage("归档目标文件不存在，无法定位字幕落点");
    }

    // ---------- ③ 正常下载：落盘命名 + 记录落库 ----------

    [Fact(DisplayName = "下载：落地名 = {视频基名}.zh.srt，SubtitleDownload 记录字段齐全")]
    public async Task Download_Success_LandsFileAndPersistsRecord()
    {
        string video = WriteVideoFile("Movie (2010).mkv");
        long id = SeedMedia(MediaItemStatus.Completed, targetPath: video);
        StubFiles("sub-1", languageHint: null,
            new SubtitleFileEntry(-1, "Inception.2010.CHS.srt", "http://dl.example/f1", 3));
        StubDownload("http://dl.example/f1", new byte[] { 1, 2, 3 });

        SubtitleDownloadResponse resp = await _sut.DownloadAsync(id, new DownloadSubtitleRequest("sub-1", -1, "精校简体"));

        string expectedPath = Path.Combine(_tempDir, "Movie (2010).zh.srt");
        resp.SavedFileName.Should().Be("Movie (2010).zh.srt", "落地名完全自构：视频基名 + 语言码 + 白名单扩展名");
        resp.SavedPath.Should().Be(expectedPath);
        resp.Language.Should().Be("zh", "文件名含 CHS token → zh");
        File.Exists(expectedPath).Should().BeTrue();
        (await File.ReadAllBytesAsync(expectedPath)).Should().Equal(1, 2, 3);

        SubtitleDownload record = ReadSingleRecord();
        record.MediaItemId.Should().Be(id);
        record.Provider.Should().Be(SubtitleProviderType.Assrt);
        record.SubtitleName.Should().Be("精校简体");
        record.FileName.Should().Be("Movie (2010).zh.srt");
        record.TargetPath.Should().Be(expectedPath);
        record.Language.Should().Be("zh");
        record.FileSize.Should().Be(3);
    }

    // ---------- ④ 已存在同名 → 序号避让 ----------

    [Fact(DisplayName = "下载：同名已存在 → 序号避让 .2.zh.srt")]
    public async Task Download_NameCollision_AppendsOrdinal()
    {
        string video = WriteVideoFile("Movie (2010).mkv");
        long id = SeedMedia(MediaItemStatus.Completed, targetPath: video);
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "Movie (2010).zh.srt"), "已有字幕");
        StubFiles("sub-1", languageHint: null,
            new SubtitleFileEntry(-1, "Inception.2010.CHS.srt", "http://dl.example/f1", 3));
        StubDownload("http://dl.example/f1", new byte[] { 9 });

        SubtitleDownloadResponse resp = await _sut.DownloadAsync(id, new DownloadSubtitleRequest("sub-1", -1, null));

        resp.SavedFileName.Should().Be("Movie (2010).2.zh.srt", "首选名被占 → .2.{lang} 序号避让（语言段紧贴扩展名，与 SubtitleRenamer.Plan 产物一致）");
        File.Exists(Path.Combine(_tempDir, "Movie (2010).2.zh.srt")).Should().BeTrue();
        (await File.ReadAllTextAsync(Path.Combine(_tempDir, "Movie (2010).zh.srt"))).Should().Be("已有字幕", "既有文件绝不能被覆盖");
    }

    // ---------- ⑤ 扩展名白名单 ----------

    [Fact(DisplayName = "下载：扩展名不在白名单 → 拒绝（外部名只取扩展名）")]
    public async Task Download_UnsupportedExtension_Throws()
    {
        string video = WriteVideoFile("Movie (2010).mkv");
        long id = SeedMedia(MediaItemStatus.Completed, targetPath: video);
        StubFiles("sub-1", languageHint: null,
            new SubtitleFileEntry(-1, "evil.exe", "http://dl.example/f1", 3));

        Func<Task> act = () => _sut.DownloadAsync(id, new DownloadSubtitleRequest("sub-1", -1, null));

        await act.Should().ThrowAsync<BusinessException>().WithMessage("不支持的字幕格式：.exe");
        await _provider.DidNotReceiveWithAnyArgs().DownloadAsync(default!, default!, default);
    }

    // ---------- ⑥ und 回退 LanguageHint 映射 ----------

    [Theory(DisplayName = "下载：文件名识别不出语言（und）→ 回退 LanguageHint 映射")]
    [InlineData("简体", "zh")]
    [InlineData("简繁双语", "zh-TW")] // 含「繁」优先繁体（与 SubtitleRenamer token 表「繁先于简」同序）
    [InlineData("繁體", "zh-TW")]
    [InlineData("CHT", "zh-TW")]
    [InlineData("双语", "zh")]
    [InlineData("英文", "en")]
    [InlineData("日文", "ja")]
    [InlineData(null, "und")]
    public async Task Download_UndFileName_FallsBackToLanguageHint(string? hint, string expectedLang)
    {
        string video = WriteVideoFile($"V-{Guid.NewGuid():N}.mkv");
        long id = SeedMedia(MediaItemStatus.Completed, targetPath: video);
        // 文件名纯数字：DetectLanguage 识别不出 → und → 走 LanguageHint 回退
        StubFiles("sub-1", hint, new SubtitleFileEntry(-1, "754321.srt", "http://dl.example/f1", 3));
        StubDownload("http://dl.example/f1", new byte[] { 1 });

        SubtitleDownloadResponse resp = await _sut.DownloadAsync(id, new DownloadSubtitleRequest("sub-1", -1, null));

        resp.Language.Should().Be(expectedLang);
        string stem = Path.GetFileNameWithoutExtension(video);
        resp.SavedFileName.Should().Be($"{stem}.{expectedLang}.srt");
        File.Exists(Path.Combine(_tempDir, resp.SavedFileName)).Should().BeTrue();
    }

    // ---------- ⑦ keyword 缺省 = 原始文件名 stem ----------

    [Fact(DisplayName = "搜索：keyword 空白 → 缺省用原始文件名去扩展名")]
    public async Task Search_BlankKeyword_DefaultsToFileNameStem()
    {
        string video = WriteVideoFile("Archived (2023) - S01E01.mkv");
        long id = SeedMedia(MediaItemStatus.Completed, targetPath: video, fileName: "Some.Show.S01E01.1080p.mkv");
        string? captured = null;
        _provider.SearchAsync(Arg.Do<string>(k => captured = k), Arg.Any<SubtitleProviderEndpoint>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SubtitleSearchEntry>());

        SubtitleSearchResponse resp = await _sut.SearchAsync(id, "   ");

        captured.Should().Be("Some.Show.S01E01.1080p", "缺省关键词 = 原始文件名 stem（非归档后名）");
        resp.Keyword.Should().Be("Some.Show.S01E01.1080p", "响应回传实际生效关键词");
        resp.Items.Should().BeEmpty();
    }

    [Fact(DisplayName = "搜索：显式 keyword → 原样（trim 后）透传")]
    public async Task Search_ExplicitKeyword_PassesThrough()
    {
        string video = WriteVideoFile("Archived (2023) - S01E01.mkv");
        long id = SeedMedia(MediaItemStatus.Completed, targetPath: video);
        string? captured = null;
        _provider.SearchAsync(Arg.Do<string>(k => captured = k), Arg.Any<SubtitleProviderEndpoint>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new SubtitleSearchEntry("654321", "某剧第1集", "Some.Show.S01E01", "简体", "srt", "2026-01-01", 5.0),
            });

        SubtitleSearchResponse resp = await _sut.SearchAsync(id, " 某剧 第一季 ");

        captured.Should().Be("某剧 第一季");
        resp.Keyword.Should().Be("某剧 第一季");
        resp.Items.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new SubtitleSearchItem("654321", "某剧第1集", "Some.Show.S01E01", "简体", "srt", "2026-01-01", 5.0));
    }

    // ---------- 补充守护：FileIndex 匹配 / 未配 Token / 外部源异常转译 / 记录查询 ----------

    [Fact(DisplayName = "下载：FileIndex 无匹配项 → 拒绝")]
    public async Task Download_FileIndexNotFound_Throws()
    {
        string video = WriteVideoFile("Movie (2010).mkv");
        long id = SeedMedia(MediaItemStatus.Completed, targetPath: video);
        StubFiles("sub-1", languageHint: null,
            new SubtitleFileEntry(0, "a.srt", "http://dl.example/f1", 3));

        Func<Task> act = () => _sut.DownloadAsync(id, new DownloadSubtitleRequest("sub-1", 5, null));

        await act.Should().ThrowAsync<BusinessException>().WithMessage("所选字幕文件不存在，请重新选择");
    }

    [Fact(DisplayName = "守门：未配 Assrt Token → 拒绝并提示前往设置")]
    public async Task Search_WithoutToken_Throws()
    {
        SetToken(null);
        string video = WriteVideoFile("Movie (2010).mkv");
        long id = SeedMedia(MediaItemStatus.Completed, targetPath: video);

        Func<Task> act = () => _sut.SearchAsync(id, null);

        await act.Should().ThrowAsync<BusinessException>().WithMessage("尚未配置 Assrt Token，请前往 设置→字幕 配置");
    }

    [Fact(DisplayName = "外部源失败：SubtitleProviderException 转译 BusinessException（中文消息保留走 1000）")]
    public async Task GetFiles_ProviderException_TranslatedToBusinessException()
    {
        string video = WriteVideoFile("Movie (2010).mkv");
        long id = SeedMedia(MediaItemStatus.Completed, targetPath: video);
        _provider.GetFilesAsync("sub-1", Arg.Any<SubtitleProviderEndpoint>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SubtitleProviderException("字幕源响应异常（HTTP 502）"));

        Func<Task> act = () => _sut.GetFilesAsync(id, "sub-1");

        // 不转译则 ExceptionHandlerMiddleware 按未预期异常归 9000 并吞掉原因
        await act.Should().ThrowAsync<BusinessException>().WithMessage("字幕源响应异常（HTTP 502）");
    }

    [Fact(DisplayName = "记录查询：仅要求媒体存在（非 Completed 也可回看），按 CreatedAt 倒序")]
    public async Task ListDownloads_ReturnsRecords_WithoutCompletedGate()
    {
        long id = SeedMedia(MediaItemStatus.Skipped, targetPath: null);
        SeedDownloadRecord(id, "old.zh.srt", createdAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        SeedDownloadRecord(id, "new.zh.srt", createdAt: DateTimeOffset.UtcNow);

        SubtitleDownloadListResponse resp = await _sut.ListDownloadsAsync(id);

        resp.Items.Should().HaveCount(2);
        resp.Items[0].FileName.Should().Be("new.zh.srt", "按下载时间倒序，最新在前");
        resp.Items[0].Provider.Should().Be("Assrt", "Provider 枚举以字符串回传");
    }

    [Fact(DisplayName = "记录查询：媒体不存在 → 拒绝")]
    public async Task ListDownloads_MediaMissing_Throws()
    {
        Func<Task> act = () => _sut.ListDownloadsAsync(99999);

        await act.Should().ThrowAsync<BusinessException>().WithMessage("媒体记录不存在");
    }

    // ---------- helpers ----------

    /// <summary>写入 / 清空 Subtitle_Setting 单例 Token 密文</summary>
    private void SetToken(string? encrypted)
    {
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        SubtitleSetting setting = ctx.SubtitleSettings.Single(s => s.Id == 1);
        setting.AssrtTokenEncrypted = encrypted;
        ctx.SaveChanges();
    }

    /// <summary>seed 一条媒体记录（CreateFixture 跳状态机，直接给定状态与 TargetPath）</summary>
    private long SeedMedia(MediaItemStatus status, string? targetPath, string fileName = "Some.Show.S01E01.1080p.mkv")
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        MediaItem item = MediaItem.CreateFixture($"/tmp/{Guid.NewGuid():N}.mkv", fileName, 1024,
            status: status, targetPath: targetPath);
        db.MediaItems.Add(item);
        db.SaveChanges();
        return item.Id;
    }

    /// <summary>直插一条字幕下载记录（记录查询排序用，CreatedAt 显式覆盖拦截器值）</summary>
    private void SeedDownloadRecord(long mediaItemId, string fileName, DateTimeOffset createdAt)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        SubtitleDownload row = new()
        {
            MediaItemId = mediaItemId,
            Provider = SubtitleProviderType.Assrt,
            FileName = fileName,
            TargetPath = Path.Combine(_tempDir, fileName),
            Language = "zh",
            FileSize = 10,
        };
        db.SubtitleDownloads.Add(row);
        db.SaveChanges();
        db.SubtitleDownloads.Where(d => d.Id == row.Id)
            .ExecuteUpdate(s => s.SetProperty(d => d.CreatedAt, createdAt));
    }

    private string WriteVideoFile(string name)
    {
        string p = Path.Combine(_tempDir, name);
        File.WriteAllText(p, "video");
        return p;
    }

    private SubtitleDownload ReadSingleRecord()
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        return db.SubtitleDownloads.AsNoTracking().Single();
    }

    private void StubFiles(string subtitleId, string? languageHint, params SubtitleFileEntry[] files)
        => _provider.GetFilesAsync(subtitleId, Arg.Any<SubtitleProviderEndpoint>(), Arg.Any<CancellationToken>())
            .Returns(new SubtitleDetailEntry(subtitleId, languageHint, files));

    private void StubDownload(string url, byte[] bytes)
        => _provider.DownloadAsync(url, Arg.Any<SubtitleProviderEndpoint>(), Arg.Any<CancellationToken>())
            .Returns(bytes);

    private sealed class TestDbContextFactory : IDbContextFactory<PmmDbContext>
    {
        private readonly SqliteConnection _connection;
        public TestDbContextFactory(SqliteConnection c) { _connection = c; }
        public PmmDbContext CreateDbContext()
        {
            DbContextOptionsBuilder<PmmDbContext> opts = new();
            opts.UseSqlite(_connection);
            opts.AddInterceptors(new TimestampInterceptor(), new RowVersionInterceptor());
            return new PmmDbContext(opts.Options);
        }
    }
}
