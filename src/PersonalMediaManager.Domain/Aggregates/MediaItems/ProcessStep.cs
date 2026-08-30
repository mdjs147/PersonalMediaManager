using PersonalMediaManager.Domain.Common;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Domain.Aggregates.MediaItems;

/// <summary>媒体处理时间线单步（Process_Step）— MediaItem 聚合内子实体</summary>
/// <remarks>
/// 由 ProcessFileService 在每次 MediaItem.Transition 之后通过 MediaItem.AppendStep 追加，
/// 用于 MediaDetail 时间线展示。Stage 复用 MediaItemStatus 枚举（语义一致：进入该状态时的快照）。
/// Detail 是结构化 JSON（按 Stage 不同字段集不同：Parsing 含 cleaned/rules/extracted/confidence/decision；
/// AiParsing 含 attempts；Archiving 含 source/target/subSteps 等），前端按 Stage 渲染 + 缺字段降级到 "—"。
///
/// 持久化设计：Cascade Delete（MediaItem 删除则其 Steps 全删）；索引 (MediaItemId, StartedAt) 支撑详情按时间拉取。
/// </remarks>
public sealed class ProcessStep : Entity
{
    /// <summary>EF Core 反射构造</summary>
    private ProcessStep() { }

    /// <summary>仅由 MediaItem.AppendStep 内部调用，避免外部绕过聚合根插入</summary>
    internal ProcessStep(long mediaItemId, MediaItemStatus stage, DateTimeOffset startedAt, long durMs, string? detail)
    {
        MediaItemId = mediaItemId;
        Stage = stage;
        StartedAt = startedAt;
        DurMs = durMs;
        Detail = detail;
    }

    /// <summary>所属 MediaItem 主键（FK CASCADE）</summary>
    public long MediaItemId { get; private set; }

    /// <summary>进入该状态时的快照（复用 MediaItemStatus 枚举）</summary>
    public MediaItemStatus Stage { get; private set; }

    /// <summary>进入该 Stage 的时刻（UTC）</summary>
    public DateTimeOffset StartedAt { get; private set; }

    /// <summary>该 Stage 在管线中实际耗时（毫秒，0 = 瞬时/未度量）</summary>
    public long DurMs { get; private set; }

    /// <summary>结构化 JSON（Stage-specific），可为 null（如 Detected / Queued 等无需 detail 的步骤）</summary>
    public string? Detail { get; private set; }

    /// <summary>测试 fixture 工厂（InternalsVisibleTo 限定 Tests + Persistence）</summary>
    internal static ProcessStep CreateFixture(long mediaItemId, MediaItemStatus stage, DateTimeOffset startedAt, long durMs = 0, string? detail = null)
        => new(mediaItemId, stage, startedAt, durMs, detail);
}
