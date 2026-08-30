using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.Settings;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Infrastructure.Persistence.Services.Settings;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>GeneralSettingsService — 归档配置合并默认 + 枚举元数据填充</summary>
public sealed class GeneralSettingsServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly IFfmpegToolTester _ffmpegTester;
    private readonly List<string> _tempDirs = new();
    private readonly GeneralSettingsService _sut;

    public GeneralSettingsServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbFactory = new TestDbContextFactory(_connection);
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ctx.Database.EnsureCreated();
        _ffmpegTester = Substitute.For<IFfmpegToolTester>();
        _sut = new GeneralSettingsService(_dbFactory, Substitute.For<IProxyResolver>(), _ffmpegTester);
    }

    public void Dispose()
    {
        _connection.Dispose();
        foreach (string d in _tempDirs)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { /* 临时目录清理失败忽略 */ }
        }
    }

    [Fact(DisplayName = "DB 无归档配置时合并默认项，冲突策略带 enum 元数据")]
    public async Task ListAsync_Merges_Archive_Defaults_When_Db_Empty()
    {
        GroupedSettingsResponse r = await _sut.ListAsync();

        r.Groups.Should().ContainKey("Archive");
        GeneralSettingItem policy = r.Groups["Archive"].Single(i => i.Key == "Archive_ConflictPolicy");
        policy.Value.Should().Be("Skip", "默认值");
        policy.ValueType.Should().Be("enum");
        policy.Options.Should().BeEquivalentTo(new[] { "Skip", "Overwrite", "KeepBoth", "Ask" });

        GeneralSettingItem minFree = r.Groups["Archive"].Single(i => i.Key == "Archive_MinFreeSpaceMB");
        minFree.Value.Should().Be("0");
        minFree.ValueType.Should().Be("int");
    }

    [Fact(DisplayName = "DB 已有归档配置时用 DB 值并补 enum 元数据")]
    public async Task ListAsync_Uses_Db_Value_With_Meta()
    {
        Seed("Archive_ConflictPolicy", "Overwrite", "Archive");

        GroupedSettingsResponse r = await _sut.ListAsync();

        GeneralSettingItem policy = r.Groups["Archive"].Single(i => i.Key == "Archive_ConflictPolicy");
        policy.Value.Should().Be("Overwrite", "DB 值优先于默认");
        policy.ValueType.Should().Be("enum");
        policy.Options.Should().Contain("KeepBoth");
    }

    [Fact(DisplayName = "待确认网页提醒默认开启，保存关闭后返回 bool 元数据与 DB 值")]
    public async Task Review_Notification_Setting_Defaults_On_And_Persists_Off()
    {
        GroupedSettingsResponse defaults = await _sut.ListAsync();

        GeneralSettingItem initial = defaults.Groups["General"]
            .Single(i => i.Key == "Notification_ReviewRequiredEnabled");
        initial.Value.Should().Be("true");
        initial.ValueType.Should().Be("bool");

        await _sut.UpdateAsync(new UpdateGeneralRequest(new[]
        {
            new UpdateGeneralItem("Notification_ReviewRequiredEnabled", "false"),
        }));

        GroupedSettingsResponse saved = await _sut.ListAsync();
        saved.Groups["General"].Single(i => i.Key == "Notification_ReviewRequiredEnabled")
            .Value.Should().Be("false");
    }

    [Fact(DisplayName = "未知 key 不带 valueType（前端按 string 渲染）")]
    public async Task ListAsync_Unknown_Key_Has_Null_ValueType()
    {
        Seed("Some_Custom_Key", "abc", "General");

        GroupedSettingsResponse r = await _sut.ListAsync();

        GeneralSettingItem item = r.Groups["General"].Single(i => i.Key == "Some_Custom_Key");
        item.ValueType.Should().BeNull();
        item.Options.Should().BeNull();
    }

    [Fact(DisplayName = "已知 key 描述以代码元数据为准（DB 种子描述会随版本过时）")]
    public async Task ListAsync_Known_Key_Description_Comes_From_Code_Meta()
    {
        // EnsureCreated 已按 HasData 落种子行；把其描述改成旧文案模拟「种子随版本过时」（HasData 不可改的现实约束）
        using (PmmDbContext db = _dbFactory.CreateDbContext())
        {
            SystemSetting row = db.SystemSettings.Single(s => s.Key == "Archive_DiskWarnPercent");
            row.Description = "归档盘剩余空间低于此百分比 → 健康检查 warn + disk.low 通知（0=不检查）";
            row.Value = "10";
            db.SaveChanges();
        }

        GroupedSettingsResponse r = await _sut.ListAsync();

        GeneralSettingItem item = r.Groups["Archive"].Single(i => i.Key == "Archive_DiskWarnPercent");
        item.Description.Should().Contain("周期巡检", "描述=真实行为：代码元数据覆盖过时的种子文案");
        item.Value.Should().Be("10", "Value 仍来自 DB");
    }

    [Fact(DisplayName = "未知 key 描述仍用 DB 行自带值")]
    public async Task ListAsync_Unknown_Key_Description_Comes_From_Db()
    {
        Seed("Some_Custom_Key", "abc", "General", description: "自定义说明");

        GroupedSettingsResponse r = await _sut.ListAsync();

        r.Groups["General"].Single(i => i.Key == "Some_Custom_Key")
            .Description.Should().Be("自定义说明");
    }

    [Fact(DisplayName = "未配置 ffmpeg 路径时自检失败并提示去填写，不启进程")]
    public async Task TestFfmpeg_NotConfigured_Fails()
    {
        TestFfmpegResponse r = await _sut.TestFfmpegAsync(new TestFfmpegRequest(null));

        r.Success.Should().BeFalse();
        r.Message.Should().Contain("尚未配置");
        await _ffmpegTester.DidNotReceive().ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "路径不存在时两工具均报路径不存在，不启进程")]
    public async Task TestFfmpeg_PathMissing_Fails()
    {
        string missing = Path.Combine(Path.GetTempPath(), "pmm-no-such-" + Guid.NewGuid().ToString("N"));

        TestFfmpegResponse r = await _sut.TestFfmpegAsync(new TestFfmpegRequest(missing));

        r.Success.Should().BeFalse();
        r.Ffprobe.Error.Should().Contain("路径不存在");
        r.Ffmpeg.Error.Should().Contain("路径不存在");
        await _ffmpegTester.DidNotReceive().ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "目录存在但缺 exe 时提示未找到可执行文件")]
    public async Task TestFfmpeg_DirWithoutExe_Fails()
    {
        string dir = NewTempDir();

        TestFfmpegResponse r = await _sut.TestFfmpegAsync(new TestFfmpegRequest(dir));

        r.Success.Should().BeFalse();
        r.Ffprobe.Error.Should().Contain("未在该路径下找到");
        await _ffmpegTester.DidNotReceive().ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "PathOverride 指向含 ffprobe/ffmpeg 的目录且工具可运行时自检通过")]
    public async Task TestFfmpeg_PathOverride_AllRunnable_Succeeds()
    {
        string dir = NewFakeFfmpegDir();
        _ffmpegTester.ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FfmpegToolProbe(true, "ffmpeg version 6.1.1", null));

        TestFfmpegResponse r = await _sut.TestFfmpegAsync(new TestFfmpegRequest(dir));

        r.Success.Should().BeTrue();
        r.Ffprobe.Runnable.Should().BeTrue();
        r.Ffmpeg.Runnable.Should().BeTrue();
        r.Message.Should().Contain("6.1.1");
        await _ffmpegTester.Received(2).ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "PathOverride 省略时读 DB 已存 Audio_FfmpegPath 自检")]
    public async Task TestFfmpeg_NoOverride_UsesDbValue()
    {
        string dir = NewFakeFfmpegDir();
        SetSetting("Audio_FfmpegPath", dir);
        _ffmpegTester.ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FfmpegToolProbe(true, "ffmpeg version 7.0", null));

        TestFfmpegResponse r = await _sut.TestFfmpegAsync(new TestFfmpegRequest(null));

        r.Success.Should().BeTrue();
        r.Message.Should().Contain("7.0");
    }

    [Fact(DisplayName = "工具存在但 -version 失败时映射为不可用并带原因")]
    public async Task TestFfmpeg_ToolNotRunnable_FailsWithReason()
    {
        string dir = NewFakeFfmpegDir();
        _ffmpegTester.ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FfmpegToolProbe(false, null, "退出码 1：libxxx.so 缺失"));

        TestFfmpegResponse r = await _sut.TestFfmpegAsync(new TestFfmpegRequest(dir));

        r.Success.Should().BeFalse();
        r.Message.Should().Contain("ffprobe").And.Contain("退出码");
    }

    private void Seed(string key, string value, string category, string? description = null)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.SystemSettings.Add(new SystemSetting { Key = key, Value = value, Category = category, Description = description });
        db.SaveChanges();
    }

    /// <summary>upsert 单个设置（种子可能已含该 key，故先查再决定 add / update，避免主键冲突）</summary>
    private void SetSetting(string key, string value)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        SystemSetting? row = db.SystemSettings.FirstOrDefault(s => s.Key == key);
        if (row is null)
            db.SystemSettings.Add(new SystemSetting { Key = key, Value = value, Category = "Audio" });
        else
            row.Value = value;
        db.SaveChanges();
    }

    /// <summary>建一个会在 Dispose 清理的临时空目录</summary>
    private string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pmm-ffmpeg-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    /// <summary>建临时目录并放占位 ffprobe/ffmpeg 可执行文件（仅供 ResolveFfmpegTools 的 File.Exists 解析，不会真执行）</summary>
    private string NewFakeFfmpegDir()
    {
        string dir = NewTempDir();
        bool win = OperatingSystem.IsWindows();
        File.WriteAllText(Path.Combine(dir, win ? "ffprobe.exe" : "ffprobe"), string.Empty);
        File.WriteAllText(Path.Combine(dir, win ? "ffmpeg.exe" : "ffmpeg"), string.Empty);
        return dir;
    }

    private sealed class TestDbContextFactory : IDbContextFactory<PmmDbContext>
    {
        private readonly SqliteConnection _connection;
        public TestDbContextFactory(SqliteConnection c) { _connection = c; }
        public PmmDbContext CreateDbContext()
        {
            DbContextOptionsBuilder<PmmDbContext> opts = new();
            opts.UseSqlite(_connection);
            return new PmmDbContext(opts.Options);
        }
    }
}
