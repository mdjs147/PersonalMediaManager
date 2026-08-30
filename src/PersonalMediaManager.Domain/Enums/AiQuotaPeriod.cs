namespace PersonalMediaManager.Domain.Enums;

/// <summary>AI 提供商周期额度粒度（Parse_AiProvider.QuotaPeriod）</summary>
/// <remarks>
/// 与终身累计配额（QuotaCallLimit / QuotaTokenLimit）<b>正交</b>的「周期性额度」维度：
/// 到周期自然边界<b>自动重置计数、自动恢复 provider</b>，无需人工 reset-quota。
/// 典型场景：Cloudflare Workers AI「每日 1000 Neurons 免费额度」等周期性免费 / 套餐额度，超出即转按量计费。
/// 周期边界按 <c>QuotaPeriodTimeZone</c> 指定时区计算（默认本机时区，可配 UTC）——详见 <see cref="Aggregates.AiProviders.QuotaPeriodMath"/>。
/// 数据库以字符串存储（"None" / "Daily" / "Weekly" / "Monthly"），EF 配置 HasConversion。
/// <b>None 显式 = 0</b>：作 CLR 默认哨兵——未启用周期额度时属性默认值即 None，HasConversion 序列化为 "None" 而非破坏性的 "0"。
/// </remarks>
public enum AiQuotaPeriod
{
    /// <summary>不启用周期额度（默认）</summary>
    None = 0,

    /// <summary>每日：按指定时区次日 00:00 重置</summary>
    Daily = 1,

    /// <summary>每周：按指定时区下周一 00:00 重置</summary>
    Weekly = 2,

    /// <summary>每月：按指定时区下月 1 号 00:00 重置</summary>
    Monthly = 3,
}
