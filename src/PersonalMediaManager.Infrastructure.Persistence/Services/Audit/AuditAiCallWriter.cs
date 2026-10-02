using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Common.Diagnostics;
using PersonalMediaManager.Application.Services.Audit;
using PersonalMediaManager.Domain.Entities;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Audit;

/// <summary>Audit_AiCall 写入器实现（D3.3 调用结果落库 → D3.4 健康追踪读出）</summary>
internal sealed class AuditAiCallWriter : IAuditAiCallWriter
{
    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly IClock _clock;

    public AuditAiCallWriter(IDbContextFactory<PmmDbContext> dbFactory, IClock clock)
    {
        _dbFactory = dbFactory;
        _clock = clock;
    }

    public async Task WriteAsync(AuditAiCallEntry e, CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        AuditAiCall row = new()
        {
            ProviderId = e.ProviderId,
            MediaItemId = e.MediaItemId,
            Success = e.Success,
            LatencyMs = e.LatencyMs,
            ErrorType = e.ErrorType,
            ErrorDetail = FormatText(e.ErrorDetail, maxUtf8Bytes: 1000),
            Model = FormatText(e.Model, includeAtStandard: true, maxUtf8Bytes: 256),
            PromptTokens = e.PromptTokens,
            CompletionTokens = e.CompletionTokens,
            Confidence = e.Confidence,
            HttpStatus = e.HttpStatus,
            ChainId = e.ChainId,
            AttemptLevel = e.AttemptLevel,
            IsPrimary = e.IsPrimary,
            RequestText = FormatText(e.RequestText),
            ResponseText = FormatText(e.ResponseText),
            Timestamp = _clock.UtcNow,
        };
        ctx.AuditAiCalls.Add(row);
        await ctx.SaveChangesAsync(ct);
        ParseDiagnostics.Emit("ai.audit_written", new
        {
            AuditId = row.Id, e.ProviderId, e.ChainId, e.AttemptLevel,
            e.Success, e.ErrorType, e.LatencyMs,
        });
    }

    /// <summary>复用诊断脱敏、正文开关及字节上限，缺失和截断均明确呈现</summary>
    internal static string? FormatText(string? text, bool includeAtStandard = false, int? maxUtf8Bytes = null)
    {
        if (text is null) return null;
        DiagnosticText captured = ParseDiagnostics.CaptureText(text, includeAtStandard, maxUtf8Bytes);
        if (captured.Text is null)
            return $"[正文未记录；状态={captured.State}；原始UTF-8={captured.OriginalUtf8Bytes}字节；SHA256={captured.Sha256}]";
        if (captured.Truncated)
            return captured.Text + $"\n[正文已截断；原始UTF-8={captured.OriginalUtf8Bytes}字节；已捕获={captured.CapturedUtf8Bytes}字节；SHA256={captured.Sha256}；已脱敏={captured.Redacted}]";
        return captured.Redacted ? captured.Text + "\n[正文已脱敏]" : captured.Text;
    }
}
