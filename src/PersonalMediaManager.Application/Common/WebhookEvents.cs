namespace PersonalMediaManager.Application.Common;

/// <summary>Webhook 事件名常量（单一事实源）</summary>
/// <remarks>
/// 全栈唯一事件名定义处，须与以下三处严格 1:1 对齐（新增事件时同步改）：
///   1. 前端 src/views/settings/Webhooks.vue 的 EVENT_OPTIONS 下拉
///   2. docs/API规范.md 的 events 可选值白名单
///   3. 本类 All 集合（WebhookSubscriptionService.NormalizeEvents 据此拒绝未知事件名）
/// 生产者侧（ArchiveService / BackupService / ProcessFileService）一律引用本类常量，禁止再散写字符串字面量。
/// </remarks>
public static class WebhookEvents
{
    /// <summary>媒体归档成功</summary>
    public const string MediaArchived = "media.archived";

    /// <summary>媒体已跳过（内容去重命中 / 归档同名冲突）</summary>
    public const string MediaSkipped = "media.skipped";

    /// <summary>媒体处理失败</summary>
    public const string MediaFailed = "media.failed";

    /// <summary>进入人工确认队列</summary>
    public const string ReviewCreated = "review.created";

    /// <summary>定时备份失败</summary>
    public const string BackupFailed = "backup.failed";

    /// <summary>归档盘剩余空间不足</summary>
    public const string DiskLow = "disk.low";

    /// <summary>网络共享监控目录不可达</summary>
    public const string ShareUnreachable = "share.unreachable";

    /// <summary>已启用的 AI 提供商全部不可用</summary>
    public const string AiAllUnavailable = "ai.all_unavailable";

    /// <summary>AI 提供商套餐用量超限自动禁用</summary>
    public const string AiProviderQuotaExceeded = "ai.provider_quota_exceeded";

    /// <summary>全部已知事件名（NormalizeEvents 白名单校验用）</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        MediaArchived,
        MediaSkipped,
        MediaFailed,
        ReviewCreated,
        BackupFailed,
        DiskLow,
        ShareUnreachable,
        AiAllUnavailable,
        AiProviderQuotaExceeded,
    };
}
