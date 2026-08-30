using AppRedactor = PersonalMediaManager.Application.Common.Logging.SensitiveDataRedactor;

namespace PersonalMediaManager.Host.Logging;

/// <summary>SensitiveDataRedactor：Host 侧脱敏门面（转发 Application.Common 同名实现）</summary>
/// <remarks>
/// 实现与脱敏名单已下沉到 <see cref="PersonalMediaManager.Application.Common.Logging.SensitiveDataRedactor"/>：
/// 日志回放（Application 层 LogQueryService）读取侧也要走同一套脱敏规则（审计修复项 4），
/// 而 Application 禁止反向引用 Host，故规则唯一源迁至 Application；本类型保留为转发门面，
/// 避免改动 SignalRSink / ExceptionHandlerMiddleware / LogHub / ValidationEnvelopeFactory 等既有调用点。
/// 新增 / 调整脱敏名单一律改 Application 侧实现，勿在此处再写规则（防止两份名单漂移）。
/// </remarks>
internal static class SensitiveDataRedactor
{
    /// <summary>对日志消息文本做脱敏；输入为 null/empty 时原样返回</summary>
    public static string Redact(string? text) => AppRedactor.Redact(text);

    /// <summary>对单字段值做脱敏（用于 Header 值等已拆解的场景），保留长度提示</summary>
    public static string MaskValue(string? value) => AppRedactor.MaskValue(value);
}
