using PersonalMediaManager.Domain.Common;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Domain.Aggregates.ParseTestCases;

/// <summary>解析测试用例聚合根（Parse_TestCase）</summary>
/// <remarks>
/// PR1 阶段仅承载字段；PR2 在 Application 层加 CRUD + 批量回归服务。
///
/// 用例生命周期：
///   1. <see cref="ParseTestCaseSource.Manual"/> 新建 → Status=Active（直接进入正式样本）
///   2. <see cref="ParseTestCaseSource.FromFailed"/> 灌入 → Status=PendingTriage（暂存区）
///      → 用户/AI 判定后 → 升级为 Active；或继续保留暂存；或 Disabled
///
/// 期望基线（Expected*）采用「半自动确认」模式：用户先跑一次实测解析 → 在 UI 上点
/// 「批准为期望」→ 把 LastRunResult 中的 Title/Year/MediaType/Season/Episode 拷贝到
/// Expected* 字段固化。Application 层后续提供 ApproveAsExpected 服务。
///
/// LastRunResult / AiVerdict 两个 JSON 列 EF 配置端为 ValueComparer 字符串比较（不解析），
/// 序列化/反序列化逻辑由 Application 层 Dto 处理，保持 Domain 零依赖。
/// </remarks>
public sealed class ParseTestCase : AggregateRoot
{
    /// <summary>来源（手动新增 / 从失败历史灌入）</summary>
    public ParseTestCaseSource Source { get; set; } = ParseTestCaseSource.Manual;

    /// <summary>用例状态</summary>
    public ParseTestCaseStatus Status { get; set; } = ParseTestCaseStatus.PendingTriage;

    /// <summary>样本文件完整路径（绝对路径；用于驱动解析的输入）</summary>
    public string SamplePath { get; set; } = default!;

    /// <summary>样本所在监控根目录路径（可空；决定 AllAncestors / RelativePath 作用域起点）</summary>
    public string? WatchRootPath { get; set; }

    /// <summary>期望标题（基线确认后写入）</summary>
    public string? ExpectedTitle { get; set; }

    /// <summary>期望年份</summary>
    public int? ExpectedYear { get; set; }

    /// <summary>期望媒体类型（Movie / Tv；Both 不允许）</summary>
    public MediaType? ExpectedMediaType { get; set; }

    /// <summary>期望季编号（仅 Tv）</summary>
    public int? ExpectedSeason { get; set; }

    /// <summary>期望集编号（仅 Tv；区间集取起始集）</summary>
    public int? ExpectedEpisode { get; set; }

    /// <summary>最近一次回归运行时间（UTC）</summary>
    public DateTimeOffset? LastRunAt { get; set; }

    /// <summary>最近一次回归运行结果状态</summary>
    public ParseTestCaseRunStatus LastRunStatus { get; set; } = ParseTestCaseRunStatus.NotRun;

    /// <summary>最近一次回归运行的实测结果 JSON（含 Title/Year/MediaType/Season/Episode/Confidence）</summary>
    public string? LastRunResult { get; set; }

    /// <summary>最近一次回归命中的规则 Id（命中内置规则时为 null，仅当命中 Parse_Rule 时有值）</summary>
    public long? LastMatchedRuleId { get; set; }

    /// <summary>AI 判定结果 JSON（含 worthAdding/reason/similarityToExisting；暂存区 Triage 流程产生）</summary>
    public string? AiVerdict { get; set; }

    /// <summary>AI 建议的规则正则模式（SuggestRule 流程产生，用户审核后可一键新建 Parse_Rule）</summary>
    public string? AiSuggestedRulePattern { get; set; }

    /// <summary>用户备注</summary>
    public string? Notes { get; set; }
}
