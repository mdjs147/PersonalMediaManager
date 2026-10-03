using PersonalMediaManager.Application.Common.Diagnostics;

namespace PersonalMediaManager.Infrastructure.External.Ai;

/// <summary>正文准备必须在配额预占之前；dispatch 仅在 SendAsync 已启动后记录。</summary>
internal static class AiDiagnosticHttp
{
    public static async Task RecordPreparedRequestAsync(HttpRequestMessage request, string? credential = null)
    {
        if (!ParseDiagnostics.IsFull) return;
        try
        {
            string? content = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            ParseDiagnostics.Emit("ai.http_request_prepared", new
            {
                stage = "serialized_prepared_not_sent", method = request.Method.Method,
                // 不读请求头、认证、URL；只观察本次实际序列化后的 content。
                body = ParseDiagnostics.CaptureText(content, credential: credential) with { Boundary = "redacted_serialized_http_request_utf8" },
            });
        }
        catch (Exception)
        {
            ParseDiagnostics.Emit("ai.http_request_prepared", new { stage = "serialized_prepared_not_sent", bodyState = "not_recorded", reason = "request_capture_failed" });
        }
    }
    public static void RecordDispatch()
    {
        if (ParseDiagnostics.IsFull)
            ParseDiagnostics.Emit("ai.http_request_dispatch", new { stage = "send_invoked_delivery_not_confirmed" });
    }
}
