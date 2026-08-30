using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Audit;
using PersonalMediaManager.Domain.Entities;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Audit;

/// <summary>Audit_Operation 写入器实现（B6.4）</summary>
internal sealed class AuditOperationWriter : IAuditOperationWriter
{
    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly IClock _clock;

    public AuditOperationWriter(IDbContextFactory<PmmDbContext> dbFactory, IClock clock)
    {
        _dbFactory = dbFactory;
        _clock = clock;
    }

    public async Task WriteAsync(
        long? userId,
        string? username,
        string action,
        string? target = null,
        string? detail = null,
        string? ip = null,
        string? userAgent = null,
        CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        AuditOperation entry = new()
        {
            UserId = userId,
            Username = username,
            Action = action,
            Target = target,
            Detail = detail,
            Ip = ip,
            UserAgent = userAgent,
            Timestamp = _clock.UtcNow,
        };
        ctx.AuditOperations.Add(entry);
        await ctx.SaveChangesAsync(ct);
    }
}
