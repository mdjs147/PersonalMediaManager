namespace PersonalMediaManager.Application.Contracts;

/// <summary>AI 提供商 RPM（每分钟请求数）滑动窗口限流闸门</summary>
/// <remarks>
/// 纯内存滑动 60 秒窗口，按 providerId 隔离计数：升级链在调用某 provider 前用 <see cref="IsThrottled"/> 判定，
/// 本窗口请求数已达该 provider 的 RpmLimit 时「跳过」本级、直接升级到下一级（不等待、不写任何 DB 禁用标记）；
/// 窗口随时间滑出后自动恢复。实际发起调用时调 <see cref="Record"/> 登记一次。
/// 与套餐 / 周期配额（按累计用量禁用）正交：RPM 保护「瞬时速率」，防短时间打爆第三方每分钟限额。
/// 单例注册（内存态跨请求共享）；进程重启计数清零（RPM 是瞬时保护，无需持久化）。
/// </remarks>
public interface IAiProviderRpmGate
{
    /// <summary>本 provider 在滑动 60 秒窗口内是否已达 RPM 上限（true=应跳过升级）；rpmLimit 为 null 或 ≤0 恒返 false（不限流）</summary>
    bool IsThrottled(long providerId, int? rpmLimit);

    /// <summary>登记一次实际发起的请求（滑动窗口计数 +1）</summary>
    void Record(long providerId);
}
