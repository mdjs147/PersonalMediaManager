using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Domain.Aggregates.AiProviders;

/// <summary>周期额度边界计算（纯函数，无状态）</summary>
/// <remarks>
/// 把 UTC 当前时刻按指定时区折算到<b>下一个自然周期边界</b>（日 = 次日 00:00 / 周 = 下周一 00:00 / 月 = 下月 1 号 00:00），再转回 UTC 返回。
/// <para><b>时区语义（可配）</b>：</para>
/// <list type="bullet">
/// <item>tzId 为 null / 空 → <see cref="TimeZoneInfo.Local"/>（Windows 单机部署即用户本地时区，故「每日额度」= 本地自然日 00:00 重置，贴合直觉）；</item>
/// <item>tzId = "UTC" → <see cref="TimeZoneInfo.Utc"/>（对齐 Cloudflare Neurons 等按 UTC 计费的厂商）；</item>
/// <item>其它 → <see cref="TimeZoneInfo.FindSystemTimeZoneById"/>（.NET 10 在 Windows 经 ICU 亦可识别 IANA id 如 "Asia/Shanghai"）；解析失败<b>回退本机时区</b>兜底不抛。</item>
/// </list>
/// <para><b>存储 / 显示分层</b>：入参与返回一律 UTC（与全项目 DateTimeOffset 存储口径一致）；边界按上述时区计算但物理时刻存 UTC，前端读到后自行按本地时区显示（倒计时优先、绝对时刻本地化）。</para>
/// <para><b>边界从 now 现算</b>（非 ResetAt += 1 周期）：闲置跨多个周期后单次调用一次跳到 now 之后的边界，结果恒<b>严格大于 now</b>。</para>
/// <para><b>DST 防御</b>：目标时区本地 00:00 若落在夏令时 spring-forward 空档（该墙钟不存在）→ 顺延到最近有效时刻；fall-back 重叠（ambiguous）→ ConvertTimeToUtc 默认取标准偏移，不抛。中国无 DST，此防御主要为可配其它时区兜底。</para>
/// </remarks>
public static class QuotaPeriodMath
{
    /// <summary>计算给定 UTC 时刻在指定时区、指定周期粒度下的下一个自然边界（返回 UTC，恒严格大于 nowUtc）</summary>
    public static DateTimeOffset NextBoundary(DateTimeOffset nowUtc, AiQuotaPeriod period, string? tzId)
    {
        TimeZoneInfo tz = ResolveTimeZone(tzId);
        // 折算到目标时区墙钟（Kind=Unspecified）
        DateTime localNow = TimeZoneInfo.ConvertTime(nowUtc, tz).DateTime;
        DateTime localBoundary = period switch
        {
            AiQuotaPeriod.Daily => localNow.Date.AddDays(1),
            AiQuotaPeriod.Weekly => NextMonday(localNow),
            AiQuotaPeriod.Monthly => new DateTime(localNow.Year, localNow.Month, 1).AddMonths(1),
            // None 不应进入（调用方以 Period != None 为前置守卫）；防御性取次日边界，避免返回过去时刻
            _ => localNow.Date.AddDays(1),
        };
        return ToUtcSafely(localBoundary, tz);
    }

    /// <summary>下一个周一 00:00（ISO 周首）；今天恰为周一则取 7 天后的下周一</summary>
    private static DateTime NextMonday(DateTime local)
    {
        DateTime day = local.Date;
        int daysUntilMonday = ((int)DayOfWeek.Monday - (int)day.DayOfWeek + 7) % 7;
        if (daysUntilMonday == 0) daysUntilMonday = 7;
        return day.AddDays(daysUntilMonday);
    }

    /// <summary>解析时区 id：null/空→本机，"UTC"→UTC，其它→系统查找（失败回退本机）</summary>
    private static TimeZoneInfo ResolveTimeZone(string? tzId)
    {
        if (string.IsNullOrWhiteSpace(tzId)) return TimeZoneInfo.Local;
        if (string.Equals(tzId, "UTC", StringComparison.OrdinalIgnoreCase)) return TimeZoneInfo.Utc;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(tzId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Local;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }

    /// <summary>目标时区墙钟（Unspecified）安全转 UTC：spring-forward 无效时刻顺延到有效，ambiguous 由 BCL 取标准偏移</summary>
    private static DateTimeOffset ToUtcSafely(DateTime unspecifiedLocal, TimeZoneInfo tz)
    {
        DateTime local = DateTime.SpecifyKind(unspecifiedLocal, DateTimeKind.Unspecified);
        // spring-forward 空档：该墙钟不存在，逐小时顺延到有效（DST 通常跳 1 小时，上限 24 次纯防御）
        for (int i = 1; i <= 24 && tz.IsInvalidTime(local); i++)
            local = DateTime.SpecifyKind(unspecifiedLocal.AddHours(i), DateTimeKind.Unspecified);
        DateTime utc = TimeZoneInfo.ConvertTimeToUtc(local, tz);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }
}
