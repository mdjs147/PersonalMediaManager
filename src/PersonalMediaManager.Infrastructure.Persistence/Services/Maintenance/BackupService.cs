using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.System;
using PersonalMediaManager.Application.Services.System;
using PersonalMediaManager.Domain.Aggregates.WebhookSubscriptions;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Maintenance;

/// <summary>IBackupService 实现 — SQLite VACUUM INTO 在线快照 + 密钥环打包 zip + 按份数保留清理</summary>
/// <remarks>
/// 与 SystemService.ExportAsync 同套 VACUUM + zip 逻辑，区别：导出面向 HTTP 流，本服务落盘到 AppPaths.BackupDir。
/// 放 Persistence 而非 Platform 的本因：需读 System_Setting（Backup_Enabled / Backup_RetainCount）—— 那要 DbContext，
/// 而 Platform 不引 Persistence；VACUUM（SqliteConnection）与 zip（BCL）在 Persistence 同样可用。
/// 密钥环 keys/* 必须随 pmm.db 一并打包——恢复到新数据根后加密字段才解得开（DataProtection 密钥环随数据根漂移坑）。
/// VACUUM INTO 走独立 SqliteConnection 得干净紧凑副本，天然不含 -wal / -shm 残留。
/// </remarks>
internal sealed class BackupService : IBackupService
{
    /// <summary>启用开关 key（与 SystemSettingConfig 种子 Backup_Enabled 必须 1:1 对齐）</summary>
    private const string EnabledKey = "Backup_Enabled";
    /// <summary>保留份数 key（与 SystemSettingConfig 种子 Backup_RetainCount 必须 1:1 对齐）</summary>
    private const string RetainCountKey = "Backup_RetainCount";
    private const string DbEntryName = "pmm.db";
    private const string KeysDirPrefix = "keys/";
    private const string BackupFilePrefix = "pmm-backup-";
    private const int DefaultRetainCount = 7;
    private const int MaxRetainCount = 365;
    /// <summary>Webhook 总开关 key（与 ArchiveService 同口径）</summary>
    private const string WebhookEnabledKey = "Webhook_Enabled";
    /// <summary>定时备份失败事件名（单一事实源 WebhookEvents，与前端 Webhooks 事件选项 / API规范 1:1 对齐）</summary>
    private const string BackupFailedEvent = WebhookEvents.BackupFailed;

    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly AppPaths _paths;
    private readonly DatabaseFileLocation _dbLocation;
    private readonly IWebhookOutboxQueue _outboxQueue;
    private readonly IClock _clock;
    private readonly ILogger<BackupService> _logger;

    public BackupService(
        IDbContextFactory<PmmDbContext> dbFactory,
        AppPaths paths,
        DatabaseFileLocation dbLocation,
        IWebhookOutboxQueue outboxQueue,
        IClock clock,
        ILogger<BackupService> logger)
    {
        _dbFactory = dbFactory;
        _paths = paths;
        _dbLocation = dbLocation;
        _outboxQueue = outboxQueue;
        _clock = clock;
        _logger = logger;
    }

    public async Task<BackupResult> RunScheduledAsync(CancellationToken ct = default)
    {
        if (!await IsEnabledAsync(ct))
        {
            _logger.LogDebug("自动备份未启用（Backup_Enabled=false），跳过本轮");
            return new BackupResult(false, null, 0, 0);
        }
        try
        {
            return await CreateAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 定时备份失败盲区：发 backup.failed Webhook 作为独立告警（手动备份失败已直接 9000 反馈用户）。
            // Webhook 仅补充，本身失败不掩盖原异常；继续抛 → BackupJob 的 Recorder 记 Audit_ScheduledTaskRun Failed。
            await TryEnqueueBackupFailedWebhookAsync(ex.Message, ct);
            throw;
        }
    }

    public async Task<BackupResult> CreateAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(_paths.BackupDir);
        string fileName = $"{BackupFilePrefix}{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.zip";
        string targetZip = Path.Combine(_paths.BackupDir, fileName);
        string tempDbCopy = Path.Combine(Path.GetTempPath(), $"pmm-backup-{Guid.NewGuid():N}.db");
        try
        {
            // 1. VACUUM INTO 在线快照（不锁主库，干净紧凑副本）；源库取生效路径（托盘可 override，override 感知）
            await using (SqliteConnection conn = new($"Data Source={_dbLocation.Path}"))
            {
                await conn.OpenAsync(ct);
                await using SqliteCommand cmd = conn.CreateCommand();
                cmd.CommandText = "VACUUM INTO @target";
                cmd.Parameters.AddWithValue("@target", tempDbCopy);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // 2. 打 zip：pmm.db + keys/*（密钥环一并打包，否则恢复到新数据根后加密字段解不开）
            await using (FileStream zipFs = new(targetZip, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (ZipArchive archive = new(zipFs, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(tempDbCopy, DbEntryName, CompressionLevel.Optimal);
                if (Directory.Exists(_paths.KeyRingDir))
                {
                    foreach (string file in Directory.EnumerateFiles(_paths.KeyRingDir, "*", SearchOption.AllDirectories))
                    {
                        string relative = Path.GetRelativePath(_paths.KeyRingDir, file).Replace('\\', '/');
                        archive.CreateEntryFromFile(file, KeysDirPrefix + relative, CompressionLevel.Optimal);
                    }
                }
            }

            long size = new FileInfo(targetZip).Length;

            // 3. 保留清理：超出 Backup_RetainCount 的最旧备份删除
            int pruned = PruneOldBackups(await ReadRetainCountAsync(ct));

            _logger.LogInformation("自动备份完成 file={File} size={Size} 字节，清理旧备份 {Pruned} 份", fileName, size, pruned);
            return new BackupResult(true, fileName, size, pruned);
        }
        catch
        {
            // 失败清理半成品 zip，避免残留损坏备份污染保留计数
            try { if (File.Exists(targetZip)) File.Delete(targetZip); } catch { /* 吞 — 主异常更重要 */ }
            throw;
        }
        finally
        {
            try { if (File.Exists(tempDbCopy)) File.Delete(tempDbCopy); } catch { /* 临时副本清理失败仅泄漏一个临时文件 */ }
        }
    }

    /// <summary>读 Backup_Enabled（缺失 / 非 "true" 一律视为关闭）</summary>
    private async Task<bool> IsEnabledAsync(CancellationToken ct)
    {
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        string? raw = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == EnabledKey)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        return string.Equals(raw?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>读 Backup_RetainCount（缺失 / 非法 / ≤0 回退默认 7；上限 365 夹紧）</summary>
    private async Task<int> ReadRetainCountAsync(CancellationToken ct)
    {
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        string? raw = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == RetainCountKey)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        return int.TryParse(raw, out int n) && n > 0 ? Math.Min(n, MaxRetainCount) : DefaultRetainCount;
    }

    /// <summary>按保留份数清理旧备份：文件名时间戳即字典序，倒序保留最近 retain 份，其余删除；返回删除份数</summary>
    private int PruneOldBackups(int retain)
    {
        if (retain <= 0 || !Directory.Exists(_paths.BackupDir)) return 0;
        List<string> backups = Directory.EnumerateFiles(_paths.BackupDir, $"{BackupFilePrefix}*.zip")
            .OrderByDescending(p => Path.GetFileName(p), StringComparer.Ordinal)
            .ToList();
        int pruned = 0;
        foreach (string old in backups.Skip(retain))
        {
            try { File.Delete(old); pruned++; }
            catch (Exception ex) { _logger.LogWarning(ex, "清理旧备份失败（跳过）：{Path}", old); }
        }
        return pruned;
    }

    /// <summary>定时备份失败 → 给订阅了 backup.failed 的 Webhook 写 Delivery + 入 Outbox（受 Webhook 总开关 gated）</summary>
    /// <remarks>与 ArchiveService 出站同模式；本方法自身异常仅吞咽记 Warning，绝不掩盖触发它的备份原始异常。</remarks>
    private async Task TryEnqueueBackupFailedWebhookAsync(string error, CancellationToken ct)
    {
        try
        {
            await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);

            // Webhook 总开关（默认关）：关则不查订阅、不产生任何投递
            string? whEnabled = await db.SystemSettings.AsNoTracking()
                .Where(s => s.Key == WebhookEnabledKey)
                .Select(s => s.Value)
                .FirstOrDefaultAsync(ct);
            if (!string.Equals(whEnabled?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
                return;

            List<WebhookSubscription> subs = await db.WebhookSubscriptions
                .Where(s => s.Enabled)
                .ToListAsync(ct);
            subs = subs.Where(s => s.Events?.Contains(BackupFailedEvent) ?? false).ToList();
            if (subs.Count == 0) return;

            string requestId = Guid.NewGuid().ToString("N");
            string payload = JsonSerializer.Serialize(new
            {
                @event = BackupFailedEvent,
                requestId,
                occurredAt = _clock.UtcNow,
                data = new { error },
            });

            foreach (WebhookSubscription sub in subs)
            {
                db.WebhookDeliveries.Add(new WebhookDelivery
                {
                    SubscriptionId = sub.Id,
                    Event = BackupFailedEvent,
                    Payload = payload,
                    Status = WebhookDeliveryStatus.Pending,
                    Attempts = 0,
                    RequestId = requestId,
                });
            }
            await db.SaveChangesAsync(ct);

            // SaveChanges 后 Id 已填，从 ChangeTracker.Local 按 (SubscriptionId, RequestId) 重新定位入队
            foreach (WebhookSubscription sub in subs)
            {
                long deliveryId = db.WebhookDeliveries.Local
                    .Where(d => d.SubscriptionId == sub.Id && d.RequestId == requestId)
                    .Select(d => d.Id).Single();
                await _outboxQueue.EnqueueAsync(deliveryId, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "发送 backup.failed Webhook 失败（不影响主流程）");
        }
    }
}
