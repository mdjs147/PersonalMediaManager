using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Dtos.System;
using PersonalMediaManager.Application.Services.System;
using PersonalMediaManager.Infrastructure.Persistence;

namespace PersonalMediaManager.Host.HostedServices;

/// <summary>UpdateCheckWorker（D9.1）— 启动延迟 60s 首检 + 每 db 配置周期触发 GitHub /releases/latest</summary>
/// <remarks>
/// 与 Launcher 启动调 /system/update-check/run（loopback 免鉴权）是**冗余但独立**的检查路径：
///   - Worker 在 Host 进程内直接调 IUpdateSettingService，无 HTTP；
///   - Launcher 触发的 /run 与 Worker 的首检如果并发，两次写 Update_LastCheckJson 由 EF 最后赢策略覆盖，无业务冲突。
///
/// 节流：UpdateSettingService 内部本身不做节流；本 Worker 做时间节流（60s 启动延迟 + 周期间隔）；
///   IUpdateChecker 走 If-None-Match ETag，304 不计 GitHub 速率上限（5000/h 或匿名 60/h 都安全）。
///
/// 关停：BackgroundService 模式，stoppingToken 触发后 Task.Delay 抛 OCE 正常退出。
///
/// 配置来源：System_Setting 表 Update_Enabled / Update_CheckIntervalHours（用户可在 WebUI 实时切换）。
///   - 每轮 tick 前从 db 重新读取；Enabled=false 时跳过本轮（不退出循环），下轮继续判断。
///   - 用 Task.Delay 而非 PeriodicTimer，让每轮周期独立按当前 db 值计算（用户改 24→1 后最多等当前轮跑完）。
///   - appsettings.json Update:Enabled / Update:CheckIntervalHours 仅作为 db 首次 seed 默认，运行期不再读。
/// </remarks>
public sealed class UpdateCheckWorker : BackgroundService
{
    /// <summary>启动延迟（让 Kestrel / Migration 先稳定再去打外网）</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(60);

    /// <summary>Enabled=false 时的轮询间隔（让用户在 UI 重新打开后能较快被 Worker 看到）</summary>
    public static readonly TimeSpan DisabledPollInterval = TimeSpan.FromMinutes(5);

    private const string KeyEnabled = "Update_Enabled";
    private const string KeyInterval = "Update_CheckIntervalHours";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UpdateCheckWorker> _logger;

    public UpdateCheckWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<UpdateCheckWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "UpdateCheckWorker 启动：启动延迟 {Delay}（运行时配置走 System_Setting.Update_Enabled / Update_CheckIntervalHours）",
            StartupDelay);

        try
        {
            // 启动延迟：让首次检查在 60s 后触发；外部停机会立刻取消 Delay
            await Task.Delay(StartupDelay, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                (bool enabled, int hours) = await ReadConfigAsync(stoppingToken);
                if (enabled)
                {
                    await CheckSafelyAsync(stoppingToken);
                    await Task.Delay(TimeSpan.FromHours(hours), stoppingToken);
                }
                else
                {
                    // 关闭状态下不调 GitHub；按短轮询等用户重新打开
                    await Task.Delay(DisabledPollInterval, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停机
        }

        _logger.LogInformation("UpdateCheckWorker 退出");
    }

    /// <summary>每轮从 db 读 Enabled + Hours；解析容错（与 UpdateSettingService.ParseEnabled / ParseInterval 同款逻辑）</summary>
    private async Task<(bool Enabled, int Hours)> ReadConfigAsync(CancellationToken ct)
    {
        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
            IDbContextFactory<PmmDbContext> factory =
                scope.ServiceProvider.GetRequiredService<IDbContextFactory<PmmDbContext>>();
            await using PmmDbContext ctx = await factory.CreateDbContextAsync(ct);
            string[] keys = [KeyEnabled, KeyInterval];
            Dictionary<string, string?> rows = await ctx.SystemSettings
                .AsNoTracking()
                .Where(s => keys.Contains(s.Key))
                .ToDictionaryAsync(s => s.Key, s => s.Value, ct);

            bool enabled = !rows.TryGetValue(KeyEnabled, out string? e) || string.IsNullOrWhiteSpace(e)
                ? true
                : string.Equals(e.Trim(), "true", StringComparison.OrdinalIgnoreCase);
            int hours = rows.TryGetValue(KeyInterval, out string? h) && int.TryParse(h, out int parsed)
                ? Math.Clamp(parsed, 1, 720)
                : 24;
            return (enabled, hours);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // db 不可读时给个安全默认：保持开启 + 24h 周期，避免卡死循环
            _logger.LogWarning(ex, "UpdateCheckWorker 读取 db 配置失败，本轮按 (Enabled=true, Hours=24) 兜底");
            return (true, 24);
        }
    }

    private async Task CheckSafelyAsync(CancellationToken ct)
    {
        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
            IUpdateSettingService svc = scope.ServiceProvider.GetRequiredService<IUpdateSettingService>();
            RunUpdateCheckResponse result = await svc.RunCheckAsync(ct);
            if (result.Success)
            {
                if (result.HasNewVersion)
                {
                    _logger.LogInformation(
                        "升级检查：发现新版本 {Latest}（当前 {Current}），地址 {Url}",
                        result.LatestVersion, result.CurrentVersion, result.LatestUrl);
                }
                else
                {
                    _logger.LogDebug(
                        "升级检查：已是最新（当前 {Current}，latest {Latest}）",
                        result.CurrentVersion, result.LatestVersion);
                }
            }
            else
            {
                _logger.LogWarning(
                    "升级检查失败：category={Category} status={Status} message={Message}",
                    result.ErrorCategory, result.HttpStatus, result.ErrorMessage);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 单次失败不停 Worker；下次周期再试
            _logger.LogError(ex, "升级检查周期任务未预期异常（下次周期继续）");
        }
    }
}
