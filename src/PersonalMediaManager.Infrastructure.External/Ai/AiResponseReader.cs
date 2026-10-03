using System.Text;
using PersonalMediaManager.Application.Common.Diagnostics;
using PersonalMediaManager.Application.Contracts;
namespace PersonalMediaManager.Infrastructure.External.Ai;

/// <summary>有界读取实际 HTTP 正文；完整诊断保存传输原文而非重建 completion。</summary>
internal static class AiResponseReader
{
    public static async Task<string> ReadAsync(HttpContent content, int maximumBytes, CancellationToken ct,
        string? credential = null, int? httpStatus = null)
    {
        using MemoryStream output = new();
        int limit = Math.Clamp(maximumBytes, 1024, 8388608);
        if (content.Headers.ContentLength > limit)
        {
            Record(null, "declared_body_size_limit", false, content.Headers.ContentLength, 0);
            throw new AiProviderLogicalException("AI 响应超过字节上限");
        }
        long observed = 0;
        try
        {
            await using Stream stream = await content.ReadAsStreamAsync(ct);
            byte[] buffer = new byte[4096];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                observed += read;
                int kept = (int)Math.Min(read, limit - output.Length);
                if (kept > 0) output.Write(buffer, 0, kept);
                if (observed > limit)
                {
                    RecordPrefix("transport_byte_limit");
                    throw new AiProviderLogicalException("AI 响应超过字节上限");
                }
            }
            string text = Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
            if (ParseDiagnostics.IsFull)
            {
                try { Record(new UTF8Encoding(false, true).GetString(output.GetBuffer(), 0, (int)output.Length), null, true, content.Headers.ContentLength, observed); }
                catch (DecoderFallbackException) { Record(null, "invalid_utf8_body", false, content.Headers.ContentLength, observed); }
            }
            return text;
        }
        catch (AiProviderLogicalException) { throw; }
        catch (Exception ex)
        {
            RecordPrefix(ex is OperationCanceledException ? "transport_cancelled" : "transport_read_failed");
            throw;
        }

        void RecordPrefix(string reason)
        {
            if (!ParseDiagnostics.IsFull) return;
            byte[] bytes = output.GetBuffer();
            int count = (int)output.Length;
            // 部分流可能恰好截在多字节标量中间；不能伪造替换字符。
            for (int omitted = 0; omitted <= Math.Min(3, count); omitted++)
            {
                try { Record(new UTF8Encoding(false, true).GetString(bytes, 0, count - omitted), reason, false, content.Headers.ContentLength, observed); return; }
                catch (DecoderFallbackException) { }
            }
            Record(null, "invalid_utf8_partial_body", false, content.Headers.ContentLength, observed);
        }
        void Record(string? body, string? reason, bool complete, long? declared, long bytesObserved)
        {
            if (!ParseDiagnostics.IsFull) return;
            // 诊断异常不能改变媒体解析、取消或配额边界。
            try
            {
                DiagnosticText captured = body is null
                    ? ParseDiagnostics.UnknownText() with { State = "not_recorded", Reason = reason, Truncated = !complete }
                    : ParseDiagnostics.CaptureText(body, credential: credential);
                if (body is not null) captured = captured with
                {
                    Truncated = captured.Truncated || !complete,
                    Reason = captured.Reason ?? reason,
                    Boundary = complete ? "redacted_http_body_utf8" : "redacted_received_prefix_utf8",
                };
                ParseDiagnostics.Emit("ai.http_response", new { httpStatus, bodyComplete = complete,
                    reason, declaredUtf8Bytes = declared, observedBytes = bytesObserved,
                    capturedPrefixBytes = output.Length, maxResponseBytes = limit, protocolStream = false,
                    content = captured });
            }
            catch (Exception) { /* 非关键诊断永远不能改变主请求结果。 */ }
        }
    }
}
