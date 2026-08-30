using Microsoft.Extensions.Logging.Abstractions;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.Logs;
using PersonalMediaManager.Application.Services.Logs;

namespace PersonalMediaManager.Application.Tests.Logs;

/// <summary>D7.9 LogQueryService — 解析 Serilog 模板 + 过滤 + 分页</summary>
public sealed class LogQueryServiceTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly LogQueryService _sut;

    public LogQueryServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"pmm-logs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _paths = AppPaths.ForRoot(_root);
        _sut = new LogQueryService(_paths, NullLogger<LogQueryService>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Empty_LogDir_Returns_Empty()
    {
        Directory.Delete(_paths.LogDir);
        LogPage p = await _sut.ListAsync(new LogQuery());
        p.Items.Should().BeEmpty();
        p.Total.Should().Be(0);
    }

    [Fact]
    public async Task Parse_Standard_Lines_From_Single_File()
    {
        WriteLog("pmm-20260517.log",
            "2026-05-17 10:00:01.123 +00:00 [INF] Foo.Bar 第一条信息",
            "2026-05-17 10:00:02.456 +00:00 [WRN] Other.Class 警告内容");

        LogPage p = await _sut.ListAsync(new LogQuery());

        p.Total.Should().Be(2);
        p.Items.Should().HaveCount(2);
        p.Items[0].Level.Should().Be("Warning", "时间倒序：第二条 WRN 在前");
        p.Items[0].Source.Should().Be("Other.Class");
        p.Items[1].Level.Should().Be("Information");
    }

    [Fact]
    public async Task Continuation_Line_Appends_To_Previous_Message()
    {
        WriteLog("pmm-20260517.log",
            "2026-05-17 10:00:01.123 +00:00 [ERR] Pmm.X 出错了",
            "   at System.Foo.Bar()",
            "   at System.Baz.Qux()");

        LogPage p = await _sut.ListAsync(new LogQuery());

        p.Items.Should().ContainSingle();
        p.Items[0].Message.Should().Contain("出错了").And.Contain("System.Foo.Bar()").And.Contain("System.Baz.Qux()");
    }

    [Fact]
    public async Task Filter_By_Level()
    {
        WriteLog("pmm-20260517.log",
            "2026-05-17 10:00:01.123 +00:00 [INF] A.B info",
            "2026-05-17 10:00:02.456 +00:00 [WRN] A.B warn",
            "2026-05-17 10:00:03.789 +00:00 [ERR] A.B err");

        LogPage p = await _sut.ListAsync(new LogQuery(Level: "Warning"));
        p.Items.Should().ContainSingle().Which.Level.Should().Be("Warning");
    }

    [Fact]
    public async Task Filter_By_Keyword_Substring_In_Message()
    {
        WriteLog("pmm-20260517.log",
            "2026-05-17 10:00:01.123 +00:00 [INF] A.B 已归档：盗梦空间",
            "2026-05-17 10:00:02.456 +00:00 [INF] A.B 已归档：星际穿越");

        LogPage p = await _sut.ListAsync(new LogQuery(Keyword: "盗梦"));
        p.Items.Should().ContainSingle().Which.Message.Should().Contain("盗梦空间");
    }

    [Fact]
    public async Task Filter_By_Source_Substring()
    {
        WriteLog("pmm-20260517.log",
            "2026-05-17 10:00:01.123 +00:00 [INF] PMM.Archive.X 已归档",
            "2026-05-17 10:00:02.456 +00:00 [INF] PMM.Watch.Y 监控事件");

        LogPage p = await _sut.ListAsync(new LogQuery(Source: "Archive"));
        p.Items.Should().ContainSingle().Which.Source.Should().Be("PMM.Archive.X");
    }

    [Fact]
    public async Task Filter_By_TimeRange()
    {
        WriteLog("pmm-20260517.log",
            "2026-05-17 10:00:01.000 +00:00 [INF] A.B early",
            "2026-05-17 12:00:00.000 +00:00 [INF] A.B mid",
            "2026-05-17 14:00:00.000 +00:00 [INF] A.B late");

        LogPage p = await _sut.ListAsync(new LogQuery(
            From: new DateTimeOffset(2026, 5, 17, 11, 0, 0, TimeSpan.Zero),
            To: new DateTimeOffset(2026, 5, 17, 13, 0, 0, TimeSpan.Zero)));

        p.Items.Should().ContainSingle().Which.Message.Should().Be("mid");
    }

    [Fact]
    public async Task Pagination_Skip_And_Take()
    {
        // 写 5 条不同时间
        string[] lines = Enumerable.Range(1, 5)
            .Select(i => $"2026-05-17 10:00:{i:D2}.000 +00:00 [INF] A.B 消息 {i}")
            .ToArray();
        WriteLog("pmm-20260517.log", lines);

        LogPage p1 = await _sut.ListAsync(new LogQuery(Page: 1, PageSize: 2));
        LogPage p2 = await _sut.ListAsync(new LogQuery(Page: 2, PageSize: 2));
        LogPage p3 = await _sut.ListAsync(new LogQuery(Page: 3, PageSize: 2));

        p1.Total.Should().Be(5);
        p1.Items.Should().HaveCount(2);
        p2.Items.Should().HaveCount(2);
        p3.Items.Should().HaveCount(1);
        p1.Items.Should().NotIntersectWith(p2.Items);
    }

    [Fact]
    public async Task Multiple_Files_Merged_DESC_By_LastWriteTime()
    {
        // 2 文件，时间不同；让 17 号文件比 16 号文件新
        WriteLog("pmm-20260516.log", "2026-05-16 10:00:00.000 +00:00 [INF] A.B old");
        string newer = WriteLog("pmm-20260517.log", "2026-05-17 10:00:00.000 +00:00 [INF] A.B new");
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);
        File.SetLastWriteTimeUtc(Path.Combine(_paths.LogDir, "pmm-20260516.log"), DateTime.UtcNow.AddDays(-1));

        LogPage p = await _sut.ListAsync(new LogQuery());
        p.Items.Should().HaveCount(2);
        p.Items[0].Message.Should().Be("new", "更新的文件优先");
    }

    [Fact]
    public async Task Invalid_Level_Throws()
    {
        Func<Task> act = async () => await _sut.ListAsync(new LogQuery(Level: "Crazy"));
        await act.Should().ThrowAsync<BusinessException>().WithMessage("日志级别取值非法");
    }

    // 注：Page<1 / PageSize 越界 的范围校验已迁至 LogQuery DataAnnotations（[Range]），
    // 由边界声明式校验单测覆盖（见 Validation/LogQueryRequestValidationTests）；此处不再在 service 层断言。

    [Fact]
    public async Task From_GreaterThan_To_Throws()
    {
        Func<Task> act = async () => await _sut.ListAsync(new LogQuery(
            From: DateTimeOffset.UtcNow, To: DateTimeOffset.UtcNow.AddDays(-1)));
        await act.Should().ThrowAsync<BusinessException>().WithMessage("时间区间格式错误");
    }

    [Fact]
    public async Task Malformed_Line_Skipped_Not_Crash()
    {
        WriteLog("pmm-20260517.log",
            "不是日志格式的随机一行",
            "2026-05-17 10:00:00.000 +00:00 [INF] A.B 正常");

        LogPage p = await _sut.ListAsync(new LogQuery());
        p.Items.Should().ContainSingle().Which.Message.Should().Be("正常");
    }

    // ── 读取侧脱敏（审计修复项 4）：日志文件落盘是未脱敏原文（Redact 原仅在 SignalRSink 写出侧），回放前必须再脱一次 ──

    [Fact]
    public async Task Secret_In_Message_Is_Redacted_On_Read()
    {
        WriteLog("pmm-20260517.log",
            "2026-05-17 10:00:01.123 +00:00 [INF] Pmm.Tmdb 调用 https://api.example.com/v3?apikey=sk-123456&page=2 完成");

        LogPage p = await _sut.ListAsync(new LogQuery());

        p.Items.Should().ContainSingle();
        p.Items[0].Message.Should().NotContain("sk-123456").And.Contain("apikey=***(");
        p.Items[0].Message.Should().Contain("page=2", "非敏感参数保持原样");
    }

    [Fact]
    public async Task BareKey_QueryParam_Redacted_But_Keyword_Param_Kept()
    {
        // Gemini 形态 ?key=AIza...（脱敏名单裸 key 项，审计修复项 3）；keyword= 是普通参数不得误伤
        WriteLog("pmm-20260517.log",
            "2026-05-17 10:00:01.123 +00:00 [INF] Pmm.Ai POST https://g.example.com/v1beta/models/x:generateContent?key=AIza12345",
            "2026-05-17 10:00:02.456 +00:00 [INF] Pmm.Search 搜索 ?keyword=变形金刚 完成");

        LogPage p = await _sut.ListAsync(new LogQuery());

        p.Items.Should().HaveCount(2);
        p.Items[1].Message.Should().NotContain("AIza12345").And.Contain("key=***(");
        p.Items[0].Message.Should().Contain("keyword=变形金刚");
    }

    [Fact]
    public async Task Secret_In_Continuation_Line_Also_Redacted()
    {
        // 多行堆栈在条目定稿（拼接完成）后统一脱敏，续行里的敏感串同样覆盖
        WriteLog("pmm-20260517.log",
            "2026-05-17 10:00:01.123 +00:00 [ERR] Pmm.X 出错了",
            "   at Send(https://x/api?token=eyJ-abc-123)");

        LogPage p = await _sut.ListAsync(new LogQuery());

        p.Items.Should().ContainSingle();
        p.Items[0].Message.Should().NotContain("eyJ-abc-123").And.Contain("token=***(");
    }

    [Fact]
    public async Task Keyword_Filter_Runs_After_Redaction_CannotProbeRawSecret()
    {
        // 先脱敏后过滤：关闭「按密钥片段逐字符试探关键词、用命中计数还原密钥」的旁路
        WriteLog("pmm-20260517.log",
            "2026-05-17 10:00:01.123 +00:00 [INF] Pmm.Tmdb 调用 ?apikey=sk-topsecret-99 完成");

        LogPage probe = await _sut.ListAsync(new LogQuery(Keyword: "sk-topsecret-99"));
        probe.Total.Should().Be(0, "原始密钥串不可被关键词命中计数试探");

        LogPage masked = await _sut.ListAsync(new LogQuery(Keyword: "apikey=***"));
        masked.Total.Should().Be(1, "脱敏后的占位文本可正常检索");
    }

    private string WriteLog(string fileName, params string[] lines)
    {
        string path = Path.Combine(_paths.LogDir, fileName);
        File.WriteAllLines(path, lines);
        return path;
    }
}
