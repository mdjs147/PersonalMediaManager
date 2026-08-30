using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Common.Logging;
using PersonalMediaManager.Application.Dtos.Logs;

namespace PersonalMediaManager.Application.Services.Logs;

/// <summary>日志查询实现（D7.9）— 解析 Serilog 输出模板做行级过滤 + 分页</summary>
/// <remarks>
/// 模板（PmmHost UseSerilog）：
///   {Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj} {NewLine}{Exception}
///
/// 行解析：
///   ^(?<ts>YYYY-MM-DD HH:MM:SS.fff ±HH:MM) [(?<lvl>VRB|DBG|INF|WRN|ERR|FTL)] (?<src>\S+) (?<msg>.*)$
///
/// Level token 映射：VRB→Verbose / DBG→Debug / INF→Information / WRN→Warning / ERR→Error / FTL→Fatal
///
/// 异常堆栈（多行）在「下一行不是 timestamp 开头」时累加到上一条 entry 的 Message。
///
/// 文件枚举倒序（按 LastWriteTimeUtc desc），每个文件按行倒序处理；匹配行仅对当前页窗口构造结果对象，
/// 其余仅计数，避免把全部匹配日志对象常驻内存（深分页 / 大库下原会膨胀到数百 MB）。
/// 总匹配数（Total）= 全文件扫到的匹配行数（须遍历全部文件以保证精确）。
/// 单文件读异常仅 Warning 跳过该文件，不阻断其他文件。
///
/// 读取侧脱敏（审计修复项 4）：日志文件落盘的是未脱敏原文（SensitiveDataRedactor 原仅在 SignalRSink
/// 写出侧应用），回放给前端前必须再过一遍同一套脱敏规则——在条目定稿（含多行堆栈拼接完成）处逐条 Redact，
/// 且先于关键词过滤执行：除直接展示防泄露外，也封掉「按密钥片段逐字符试探关键词、用命中计数还原密钥」的旁路。
///
/// 放 Application 层而非 Persistence：本服务零 EF 依赖，纯文件 IO（System.IO + System.Text.RegularExpressions）。
/// </remarks>
public sealed class LogQueryService : ILogQueryService
{
    private const int KeywordMaxLength = 200;

    private static readonly Regex LogLinePattern = new(
        @"^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [\+\-]\d{2}:\d{2}) \[(?<lvl>VRB|DBG|INF|WRN|ERR|FTL)\] (?<src>\S+) (?<msg>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    private static readonly IReadOnlyDictionary<string, string> TokenToLevel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["VRB"] = "Verbose",
        ["DBG"] = "Debug",
        ["INF"] = "Information",
        ["WRN"] = "Warning",
        ["ERR"] = "Error",
        ["FTL"] = "Fatal",
    };

    private static readonly HashSet<string> ValidLevelNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Verbose", "Debug", "Information", "Warning", "Error", "Fatal",
    };

    private readonly AppPaths _paths;
    private readonly ILogger<LogQueryService> _logger;

    public LogQueryService(AppPaths paths, ILogger<LogQueryService> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public Task<LogPage> ListAsync(LogQuery query, CancellationToken ct = default)
    {
        // Page / PageSize 的范围校验已上移至 LogQuery DataAnnotations（[Range]），由 [ApiController] 边界拦截。
        // Level 大小写不敏感（ValidLevelNames 用 OrdinalIgnoreCase）、From>To 跨字段比较，二者无法用单字段 DataAnnotations 表达，保留于此。
        if (query.Level is not null && !ValidLevelNames.Contains(query.Level))
            throw new BusinessException("日志级别取值非法");
        if (query.From is not null && query.To is not null && query.From > query.To)
            throw new BusinessException("时间区间格式错误");

        if (!Directory.Exists(_paths.LogDir))
        {
            return Task.FromResult(new LogPage(Array.Empty<LogEntryResponse>(), 0, query.Page, query.PageSize));
        }

        int skip = (query.Page - 1) * query.PageSize;
        (IReadOnlyList<LogEntryResponse> pageItems, int total) = CollectPage(query, skip, query.PageSize, ct);
        return Task.FromResult(new LogPage(pageItems, total, query.Page, query.PageSize));
    }

    /// <summary>按文件时间倒序遍历，匹配行只对「当前页窗口」构造结果对象、其余仅计数——避免全量日志常驻内存</summary>
    /// <remarks>
    /// 内存峰值 = 单文件解析临时 List（≤ 单日 10MB 滚动文件）+ 一页结果，而非全部匹配对象。
    /// Total 仍精确（须遍历全部文件计数），但不再为深分页 / 大库把数百 MB 日志对象全留在内存。
    /// </remarks>
    private (IReadOnlyList<LogEntryResponse> Page, int Total) CollectPage(LogQuery q, int skip, int pageSize, CancellationToken ct)
    {
        // 按 LastWriteTimeUtc desc 遍历文件；同一文件内行序为时间升序，反转后整体得到「时间倒序」效果。
        string[] files;
        try
        {
            files = Directory.EnumerateFiles(_paths.LogDir, "pmm-*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(p => File.GetLastWriteTimeUtc(p))
                .ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "枚举日志目录失败：{LogDir}", _paths.LogDir);
            throw new BusinessException("日志文件读取失败", ex);
        }

        string? kw = string.IsNullOrWhiteSpace(q.Keyword) ? null
            : (q.Keyword.Length > KeywordMaxLength ? q.Keyword[..KeywordMaxLength] : q.Keyword);
        string? src = string.IsNullOrWhiteSpace(q.Source) ? null : q.Source;

        int matched = 0;
        List<LogEntryResponse> page = new(Math.Min(pageSize, 200));
        foreach (string file in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                List<LogEntryResponse> fileEntries = ParseFile(file, ct);
                // 文件内时间升序 → 反向遍历实现总倒序
                for (int i = fileEntries.Count - 1; i >= 0; i--)
                {
                    LogEntryResponse e = fileEntries[i];
                    if (q.Level is not null && !string.Equals(e.Level, q.Level, StringComparison.OrdinalIgnoreCase)) continue;
                    if (q.From is not null && e.Timestamp < q.From.Value) continue;
                    if (q.To is not null && e.Timestamp > q.To.Value) continue;
                    if (kw is not null && e.Message.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (src is not null && e.Source.IndexOf(src, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    // 命中：仅当前页窗口 [skip, skip+pageSize) 构造对象，其余仅累加计数（保证 Total 精确）
                    if (matched >= skip && page.Count < pageSize)
                        page.Add(e);
                    matched++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读日志文件失败（跳过单文件）：{File}", file);
            }
        }
        return (page, matched);
    }

    private static List<LogEntryResponse> ParseFile(string file, CancellationToken ct)
    {
        List<LogEntryResponse> entries = new();
        LogEntryResponse? current = null;
        // shared 写 + 读：用 FileShare.ReadWrite 防 Serilog 持锁
        using FileStream fs = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(fs);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            ct.ThrowIfCancellationRequested();
            Match m = TrySafeMatch(line);
            if (m.Success)
            {
                if (current is not null) entries.Add(Seal(current));
                if (!DateTimeOffset.TryParseExact(m.Groups["ts"].Value,
                    "yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset ts))
                {
                    // 无法解析时间戳的行跳过（避免污染下一条）
                    current = null;
                    continue;
                }
                string lvl = TokenToLevel.TryGetValue(m.Groups["lvl"].Value, out string? name) ? name : m.Groups["lvl"].Value;
                current = new LogEntryResponse(ts, lvl, m.Groups["src"].Value, m.Groups["msg"].Value);
            }
            else if (current is not null)
            {
                // 续行（堆栈跟踪 / Exception 内容）追加到上一条 Message
                current = current with { Message = current.Message + Environment.NewLine + line };
            }
        }
        if (current is not null) entries.Add(Seal(current));
        return entries;
    }

    /// <summary>条目定稿：续行（堆栈/异常）拼接完成后统一做读取侧脱敏（先脱敏后过滤，见类 remarks）</summary>
    /// <remarks>逐条 Redact 为 3 个已编译正则替换，单日 10MB 滚动文件量级可接受；正常无敏感内容的行近似零开销。</remarks>
    private static LogEntryResponse Seal(LogEntryResponse e) =>
        e with { Message = SensitiveDataRedactor.Redact(e.Message) };

    private static Match TrySafeMatch(string line)
    {
        try { return LogLinePattern.Match(line); }
        catch (RegexMatchTimeoutException) { return Match.Empty; }
    }
}
