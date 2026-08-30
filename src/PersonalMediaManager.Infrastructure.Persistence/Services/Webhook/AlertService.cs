using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Webhook;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Webhook;

/// <summary>IAlertService 实现：抑制窗口 + 复用 IWebhookEmitter 发送</summary>
/// <remarks>
/// 抑制窗口读 System_AlertSuppressMinutes（缺失 / 非法回退 60min；0 = 不抑制每次都发；上限 10080min=7 天夹紧，防误填超大值致同类告警进程内永久哑火）。
/// 单例：依赖全 singleton（IWebhookEmitter / IDbContextFactory / IClock / AlertSuppressionState），
/// 供 singleton 的 NetworkShareMonitorWorker 直接注入。自身吞咽异常不阻断主流程。
/// </remarks>
internal sealed class AlertService : IAlertService
{
    /// <summary>抑制窗口配置 key（与 SystemSettingConfig 种子 System_AlertSuppressMinutes 必须 1:1 对齐）</summary>
    private const string SuppressMinutesKey = "System_AlertSuppressMinutes";
    private const int DefaultSuppressMinutes = 60;
    private const int MaxSuppressMinutes = 10080;   // 7 天上限：防误填超大值导致同类告警进程生命周期内永久哑火

    private readonly IWebhookEmitter _emitter;
    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly IClock _clock;
    private readonly AlertSuppressionState _suppression;
    private readonly ILogger<AlertService> _logger;

    public AlertService(
        IWebhookEmitter emitter,
        IDbContextFactory<PmmDbContext> dbFactory,
        IClock clock,
        AlertSuppressionState suppression,
        ILogger<AlertService> logger)
    {
        _emitter = emitter;
        _dbFactory = dbFactory;
        _clock = clock;
        _suppression = suppression;
        _logger = logger;
    }

    public async Task RaiseAsync(string alertKey, string @event, object data, CancellationToken ct = default)
    {
        try
        {
            int minutes = await ReadSuppressMinutesAsync(ct);
            if (minutes > 0 && !_suppression.ShouldFire(alertKey, TimeSpan.FromMinutes(minutes), _clock.UtcNow))
            {
                _logger.LogDebug("告警 {AlertKey} 在抑制窗口内（{Minutes}min），本次跳过", alertKey, minutes);
                return;
            }
            await _emitter.EmitAsync(@event, data, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "触发告警 {AlertKey} 失败（不影响主流程）", alertKey);
        }
    }

    private async Task<int> ReadSuppressMinutesAsync(CancellationToken ct)
    {
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        string? raw = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == SuppressMinutesKey)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        return int.TryParse(raw, out int v) && v >= 0 ? Math.Min(v, MaxSuppressMinutes) : DefaultSuppressMinutes;
    }
}
