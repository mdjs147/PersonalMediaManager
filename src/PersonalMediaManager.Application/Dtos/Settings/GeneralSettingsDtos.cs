using System.ComponentModel.DataAnnotations;
using PersonalMediaManager.Application.Common.Validation;

namespace PersonalMediaManager.Application.Dtos.Settings;

/// <summary>单个设置项；ValueType / Options 供前端选择合适控件（int→数字框、bool→开关、enum→下拉，缺省→文本）</summary>
public sealed record GeneralSettingItem(
    string Key,
    string? Value,
    string Category,
    string? Description,
    string? ValueType = null,
    IReadOnlyList<string>? Options = null);

public sealed record GroupedSettingsResponse(IReadOnlyDictionary<string, IReadOnlyList<GeneralSettingItem>> Groups);

/// <summary>单条 KV 更新项；受范围约束的 key 在绑定阶段声明式校验值区间</summary>
/// <remarks>
/// 通用 KV 端点的 Value 是字符串、合法区间随 Key 而定，无法用 [Range] 直接约束某一 Key；
/// 故实现 <see cref="IValidatableObject"/>：仅对已知受限 key（归档磁盘告警阈值 / 告警抑制窗口）校验整数区间，
/// 失败经 [ApiController] → ValidationEnvelopeFactory 归一为 {code:1000} 信封（不抛 BusinessException）。
/// 读侧另有 Math.Clamp 兜底（HealthCheckService / AlertService），双重防越界值把告警配成失效。
/// 空值放行（= 用默认/不变，读侧回退默认）；非受限 key 不约束。
/// </remarks>
public sealed record UpdateGeneralItem(
    [RequiredNotBlank(ErrorMessage = "配置项 Key 不能为空")] string Key,
    string? Value) : IValidatableObject
{
    /// <summary>受范围约束的配置 key → 合法整数闭区间（与读侧钳位上下限保持一致）</summary>
    private static readonly IReadOnlyDictionary<string, (int Min, int Max)> RangedKeys =
        new Dictionary<string, (int, int)>(StringComparer.Ordinal)
        {
            ["Archive_DiskWarnPercent"] = (0, 100),
            ["Archive_DiskCriticalPercent"] = (0, 100),
            ["System_AlertSuppressMinutes"] = (0, 10080),   // 7 天上限
            ["Archive_MinFreeSpaceMB"] = (0, 1048576),      // 1 TB（MB 计）上限：防误填超大余量把所有归档卡成「磁盘不足」
        };

    /// <summary>受限 key：值须为区间内整数；空值放行，非整数 / 越界拦截</summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrEmpty(Key) || !RangedKeys.TryGetValue(Key, out (int Min, int Max) range))
            yield break;                                    // 非受限 key 不约束（Key 为空另由 [RequiredNotBlank] 拦）
        if (string.IsNullOrWhiteSpace(Value))
            yield break;                                    // 空 = 用默认/不变（读侧回退），不在写侧拦
        if (!int.TryParse(Value.Trim(), out int n))
            yield return new ValidationResult($"配置项 {Key} 必须为整数", new[] { nameof(Value) });
        else if (n < range.Min || n > range.Max)
            yield return new ValidationResult($"配置项 {Key} 必须在 {range.Min}~{range.Max} 之间", new[] { nameof(Value) });
    }
}

/// <summary>通用设置批量更新请求；跨项关系校验见 Validate</summary>
/// <remarks>
/// 磁盘双阈值（warn / critical）语义均为「剩余空间低于 X% 触发」：剩余空间下行先穿过警告值、再穿过严重值，
/// 故数值上须 critical ≤ warn（如 warn=10、critical=5）；倒置（critical &gt; warn）会让 warn 档永远被 critical 抢先（过度告警）。
/// 仅当两 key 同次提交且均为正整数时校验关系：
///   · 单 key 提交不拦——通用 KV 端点逐项提交的现实约束，写侧无法可靠取得另一侧现值（关系提示已写入两项的运行时描述）；
///   · 任一为 0 = 该档检查关闭，不存在先后关系；
///   · 非整数 / 越界已由 UpdateGeneralItem 项级校验拦截，此处跳过避免重复报错。
/// </remarks>
public sealed record UpdateGeneralRequest(
    [Required(ErrorMessage = "没有需要更新的配置项")]
    [MinLength(1, ErrorMessage = "没有需要更新的配置项")]
    IReadOnlyList<UpdateGeneralItem> Items) : IValidatableObject
{
    /// <summary>跨项关系校验：warn / critical 同次提交时，严重阈值不得大于警告阈值</summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Items is null)
            yield break;                                    // null 已由 [Required] 拦
        int? warn = LastIntValue("Archive_DiskWarnPercent");
        int? crit = LastIntValue("Archive_DiskCriticalPercent");
        if (warn is int w && crit is int c && w > 0 && c > 0 && c > w)
            yield return new ValidationResult(
                $"磁盘严重阈值（{c}）不能大于警告阈值（{w}）：两者均为「剩余空间低于 X% 触发」，剩余空间先低于警告值再低于严重值",
                new[] { nameof(Items) });
    }

    /// <summary>取该 key 最后一次出现的整数值（与服务端逐项 upsert「后写覆盖」语义一致）；缺失 / 空 / 非整数返 null</summary>
    private int? LastIntValue(string key)
    {
        UpdateGeneralItem? item = Items.LastOrDefault(i => string.Equals(i.Key, key, StringComparison.Ordinal));
        if (item is null || string.IsNullOrWhiteSpace(item.Value))
            return null;
        return int.TryParse(item.Value.Trim(), out int n) ? n : null;
    }
}

/// <summary>ffmpeg 路径自检请求</summary>
/// <remarks>
/// PathOverride 非空 → 测「当前输入框里尚未保存的值」（边配边测，免去先保存再测）；
/// 为空 → 读 DB 已存的 Audio_FfmpegPath 测「已保存的值」。
/// </remarks>
public sealed record TestFfmpegRequest(string? PathOverride);

/// <summary>单个 ffmpeg 系工具（ffprobe / ffmpeg）的自检状态</summary>
/// <param name="Runnable">能成功启动并以退出码 0 返回</param>
/// <param name="Path">解析出的可执行文件绝对路径（未找到为 null）</param>
/// <param name="Version">版本首行（不可用为 null）</param>
/// <param name="Error">不可用原因（Runnable=true 时为 null）</param>
public sealed record FfmpegToolStatus(bool Runnable, string? Path, string? Version, string? Error);

/// <summary>ffmpeg 路径自检响应</summary>
/// <remarks>Success = ffprobe 与 ffmpeg 均可运行；Message 为可直接展示的中文结论（成功含版本号，失败列出各工具问题）。</remarks>
public sealed record TestFfmpegResponse(
    bool Success,
    FfmpegToolStatus Ffprobe,
    FfmpegToolStatus Ffmpeg,
    string Message);
