using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.Contracts;
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

    /// <summary>原文安全阀：正常解析的请求/响应原文远小于此，仅防个别厂商返回异常巨包撑爆库</summary>
    private const int MaxTextLength = 16_000;

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
            ErrorDetail = Truncate(e.ErrorDetail, 1000),
            Model = e.Model,
            PromptTokens = e.PromptTokens,
            CompletionTokens = e.CompletionTokens,
            Confidence = e.Confidence,
            HttpStatus = e.HttpStatus,
            ChainId = e.ChainId,
            AttemptLevel = e.AttemptLevel,
            IsPrimary = e.IsPrimary,
            RequestText = Truncate(e.RequestText, MaxTextLength),
            ResponseText = Truncate(e.ResponseText, MaxTextLength),
            Timestamp = _clock.UtcNow,
        };
        ctx.AuditAiCalls.Add(row);
        await ctx.SaveChangesAsync(ct);
    }

    /// <summary>按上限截断（null 透传；超长取前 max 字符）</summary>
    private static string? Truncate(string? s, int max) =>
        s is null ? null : (s.Length <= max ? s : s[..max]);
}
