using PersonalMediaManager.Domain.Common;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Domain.Aggregates.AiProviders;

/// <summary>AI 提供商聚合根（Parse_AiProvider）</summary>
/// <remarks>
/// 阶段 A：字段定义；阶段 D（D3.4）在 AiProviderHealthTracker 内根据 Audit_AiCall 滚动窗口写 DisabledUntil。
/// 全表至多 1 行 IsPrimary=1（应用层校验，无数据库约束）；备用按 (Enabled, Priority) 顺序。
/// ApiKey 走 DataProtection 加密；Ollama 本地部署可为空。
/// </remarks>
public sealed class ParseAiProvider : AggregateRoot
{
    public string Name { get; set; } = default!;

    public AiProviderType Type { get; set; }

    /// <summary>成本档位（独立于协议）：Free 享节流豁免 + 高阈值默认，Paid 走常规节流</summary>
    public AiCostTier CostTier { get; set; } = AiCostTier.Paid;

    /// <summary>该节点是否支持结构化 JSON 输出（response_format=json_object）；false 时仅靠 system prompt 约束输出格式</summary>
    public bool StructuredJson { get; set; } = true;

    /// <summary>完整 URL（Ollama 默认 http://localhost:11434）</summary>
    public string BaseUrl { get; set; } = default!;

    /// <summary>DataProtection 加密；Ollama 可为空</summary>
    public string? ApiKeyEncrypted { get; set; }

    /// <summary>模型名（如 llama3 / qwen-plus）</summary>
    public string Model { get; set; } = default!;

    public bool IsPrimary { get; set; }

    /// <summary>备用顺序，越小越先；同时是「升级阶梯」：Ollama 设最小值排第 1 级，结果不满意或异常逐级升到更高级</summary>
    public int Priority { get; set; } = 100;

    /// <summary>满意度阈值（0~1）：本 provider 返回的置信度低于此值视为「结果不满意」→ 升级到下一级；null = 回退全局 Parse.AiConfidenceThreshold（默认 0.7）</summary>
    /// <remarks>典型用法：本地 Ollama 设较高阈值（如 0.85，逼它把握不足就升级到云端），云端设较低阈值（如 0.7）兜底。</remarks>
    public double? ConfidenceThreshold { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>自动禁用截止时间（UTC）；超过即解禁</summary>
    public DateTimeOffset? DisabledUntil { get; set; }

    /// <summary>单次请求超时秒数（含建立连接 + 首字节 + 收完整 body）；本地模型如 Ollama 首次加载较慢可调大，默认 30</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>JSON 扩展参数（temperature/topP 等）</summary>
    public string? ExtraOptions { get; set; }

    /// <summary>是否通过代理访问（仅在系统代理总开关启用时生效；本地 Ollama 通常保持 false）</summary>
    public bool UseProxy { get; set; } = false;

    /// <summary>套餐调用次数上限；null = 不限</summary>
    public int? QuotaCallLimit { get; set; }

    /// <summary>套餐 token 总量上限（prompt + completion 合计）；null = 不限</summary>
    public long? QuotaTokenLimit { get; set; }

    /// <summary>套餐到期时间（UTC）；到期即从升级链剔除（纯查询过滤，不写标记）；null = 不限</summary>
    public DateTimeOffset? QuotaExpiresAt { get; set; }

    /// <summary>已累计调用次数（独立计数器，不依赖 Audit_AiCall 聚合——该表有保留期清理会截断）</summary>
    public long QuotaUsedCalls { get; set; }

    /// <summary>已累计 token（prompt + completion 合计，成功失败都计）</summary>
    public long QuotaUsedTokens { get; set; }

    /// <summary>用量超限自动禁用时刻；非 null 即配额禁用——与 Enabled（用户开关）、DisabledUntil（健康熔断，自动恢复）三态分离，手动 /enable 不解除</summary>
    public DateTimeOffset? QuotaExceededAt { get; set; }

    // ============ 周期滚动额度（与上方终身累计配额正交）============
    // 到周期自然边界自动重置计数、自动恢复 provider，无需人工 reset-quota；典型：Cloudflare Workers AI「每日 N Neurons 免费额度」等。
    // 计数清零走「惰性重置」：RecordUsage 发现 now≥QuotaPeriodResetAt 时先归零再累加并重算 ResetAt；Resolver 侧仅在窗口内超限才软禁用（跨窗口自动放行）。

    /// <summary>周期额度粒度（None=不启用）；启用后按 QuotaPeriodTimeZone 时区到自然边界重置</summary>
    public AiQuotaPeriod QuotaPeriod { get; set; } = AiQuotaPeriod.None;

    /// <summary>周期边界计算时区 id（null/空=本机时区，"UTC"=UTC，其它=IANA/Windows id）；仅 QuotaPeriod≠None 时有意义</summary>
    public string? QuotaPeriodTimeZone { get; set; }

    /// <summary>周期内调用次数上限；null=不限次</summary>
    public int? QuotaPeriodCallLimit { get; set; }

    /// <summary>周期内 token 上限（prompt+completion 合计）；null=不限 token</summary>
    public long? QuotaPeriodTokenLimit { get; set; }

    /// <summary>当前周期已用调用次数（跨窗口由 RecordUsage 惰性归零）</summary>
    public long QuotaPeriodUsedCalls { get; set; }

    /// <summary>当前周期已用 token（跨窗口由 RecordUsage 惰性归零）</summary>
    public long QuotaPeriodUsedTokens { get; set; }

    /// <summary>当前周期重置时刻（UTC，此刻及之后视为新窗口）；null=尚未开始计量，首次 RecordUsage 时按 QuotaPeriodMath.NextBoundary 落定</summary>
    public DateTimeOffset? QuotaPeriodResetAt { get; set; }

    /// <summary>每分钟请求数上限（RPM，滑动 60 秒窗口）；达到后升级链「跳过」本 provider 直接升级到下一级（不等待），窗口滑出后自动恢复；null=不限流</summary>
    /// <remarks>与套餐/周期配额（按累计用量禁用）正交：RPM 是「瞬时速率」保护，防止短时间打爆第三方接口的每分钟限额；不写任何禁用标记，纯由内存滑动窗口实时判定。</remarks>
    public int? RpmLimit { get; set; }
}
