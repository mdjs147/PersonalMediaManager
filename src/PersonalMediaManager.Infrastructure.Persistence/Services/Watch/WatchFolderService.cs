using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.Watch;
using PersonalMediaManager.Application.Services.Watch;
using PersonalMediaManager.Domain.Aggregates.WatchDirectories;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Watch;

/// <summary>监控目录服务实现（Watch_Folder CRUD + 连通性测试）</summary>
/// <remarks>
/// 路径归一化：Trim + 末尾分隔符剥离，原样保留大小写 / 反斜杠 / 正斜杠（按输入原生格式存储）。
/// 同路径不可重复（UQ_Watch_Folder_Path 兜底，服务层提前抛 1000 以获得更友好的错误信息）。
/// 删除前检查：SourcePath 以 Path + 分隔符 开头且 Status 处于非终态（Detected/Queued/Parsing/TmdbMatching/AiParsing/
/// TmdbRematching/Classifying/AwaitingReview/Archiving）的 Media_Item 视为 in-flight，禁止删除（→ 1000）。
/// /test：Directory.Exists + Enumerate 第一个条目；任何 IO 异常归集为 ErrorMessage（不抛）。
/// 增/删/改（含启停）落库成功后经 IWatchRebuildSignal 通知 FileWatcherWorker 增量重建：
/// 新增 → 挂载；禁用 / 删除 → 立即卸载；改路径 → 重建，使 CRUD 运行时即刻生效、无需重启进程。
/// </remarks>
internal sealed class WatchFolderService : IWatchFolderService
{
    private static readonly MediaItemStatus[] InFlightStatuses =
    [
        MediaItemStatus.Detected,
        MediaItemStatus.Queued,
        MediaItemStatus.Parsing,
        MediaItemStatus.TmdbMatching,
        MediaItemStatus.AiParsing,
        MediaItemStatus.TmdbRematching,
        MediaItemStatus.Classifying,
        MediaItemStatus.AwaitingReview,
        MediaItemStatus.Archiving,
    ];

    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly IWatchRebuildSignal _rebuildSignal;

    public WatchFolderService(IDbContextFactory<PmmDbContext> dbFactory, IWatchRebuildSignal rebuildSignal)
    {
        _dbFactory = dbFactory;
        _rebuildSignal = rebuildSignal;
    }

    public async Task<IReadOnlyList<WatchFolderResponse>> ListAsync(CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        List<WatchFolder> rows = await ctx.WatchFolders.AsNoTracking()
            .OrderBy(f => f.Priority).ThenBy(f => f.Id)
            .ToListAsync(ct);

        // 单次拉全部 SourcePath 后 in-memory 前缀匹配聚合 MediaCount；
        // 家用规模（几千条媒体 × 数个目录）开销可忽略，避免 N+1 EF 查询。
        List<string> allSourcePaths = await ctx.MediaItems.AsNoTracking()
            .Select(m => m.SourcePath)
            .ToListAsync(ct);

        return rows.Select(f =>
        {
            string sepPrefix = f.Path + GuessSeparator(f.Path);
            int count = allSourcePaths.Count(p =>
                string.Equals(p, f.Path, StringComparison.Ordinal) || p.StartsWith(sepPrefix, StringComparison.Ordinal));
            return ToResponse(f, count);
        }).ToList();
    }

    public async Task<WatchFolderResponse> CreateAsync(CreateWatchFolderRequest req, CancellationToken ct = default)
    {
        string path = NormalizePath(req.Path);

        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        if (await ctx.WatchFolders.AnyAsync(f => f.Path == path, ct))
            throw new BusinessException($"监控目录 {path} 已存在");

        WatchFolder entity = new()
        {
            Path = path,
            Alias = req.Alias?.Trim(),
            IsTransit = req.IsTransit,
            IsNetworkShare = req.IsNetworkShare,
            Enabled = req.Enabled,
            AutoScan = req.AutoScan,
            Priority = req.Priority,
        };
        ctx.WatchFolders.Add(entity);
        await ctx.SaveChangesAsync(ct);
        // 落库成功后通知 FileWatcherWorker 增量挂载（启用与否由消费侧按 DB 当前状态对账）
        _rebuildSignal.Publish(new WatchRebuildItem(WatchChangeKind.FolderCreated, entity.Id, entity.Path));
        return ToResponse(entity);
    }

    public async Task<WatchFolderResponse> UpdateAsync(UpdateWatchFolderRequest req, CancellationToken ct = default)
    {
        string path = NormalizePath(req.Path);

        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        WatchFolder? entity = await ctx.WatchFolders.FirstOrDefaultAsync(f => f.Id == req.Id, ct);
        if (entity is null)
            throw new BusinessException($"监控目录 Id={req.Id} 不存在");

        if (entity.Path != path
            && await ctx.WatchFolders.AnyAsync(f => f.Id != req.Id && f.Path == path, ct))
            throw new BusinessException($"监控目录 {path} 已存在");

        // 乐观并发：客户端提交的 RowVersion 须与 DB 当前值一致；不一致说明客户端持有旧版本，提前抛 1000 避免覆盖更新的数据
        if (entity.RowVersion != req.RowVersion)
            throw new BusinessException($"监控目录 Id={req.Id} 已被其他操作修改，请刷新后重试");

        entity.Path = path;
        entity.Alias = req.Alias?.Trim();
        entity.IsTransit = req.IsTransit;
        entity.IsNetworkShare = req.IsNetworkShare;
        entity.Enabled = req.Enabled;
        entity.AutoScan = req.AutoScan;
        entity.Priority = req.Priority;
        await ctx.SaveChangesAsync(ct);
        // 改路径 / 启停 / 其它字段统一发 Updated：消费侧按 DB 当前状态对账（启用→重建挂载，禁用→卸载）
        _rebuildSignal.Publish(new WatchRebuildItem(WatchChangeKind.FolderUpdated, entity.Id, entity.Path));
        return ToResponse(entity);
    }

    public async Task DeleteAsync(DeleteWatchFolderRequest req, CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        WatchFolder? entity = await ctx.WatchFolders.FirstOrDefaultAsync(f => f.Id == req.Id, ct);
        if (entity is null)
            throw new BusinessException($"监控目录 Id={req.Id} 不存在");

        // in-flight 检测：SourcePath 以 Path + 分隔符 开头且状态非终态
        string sepPrefix = entity.Path + GuessSeparator(entity.Path);
        bool hasInFlight = await ctx.MediaItems.AsNoTracking()
            .AnyAsync(m => InFlightStatuses.Contains(m.Status)
                && (m.SourcePath == entity.Path || m.SourcePath.StartsWith(sepPrefix)), ct);
        if (hasInFlight)
            throw new BusinessException($"监控目录 {entity.Path} 下尚有未完成的媒体处理记录，无法删除");

        ctx.WatchFolders.Remove(entity);
        await ctx.SaveChangesAsync(ct);
        // 删除成功后通知 FileWatcherWorker 立即卸载该目录 watcher（不发则已删目录仍在采集直到重启）
        _rebuildSignal.Publish(new WatchRebuildItem(WatchChangeKind.FolderDeleted, req.Id, entity.Path));
    }

    public async Task<WatchFolderTestResponse> TestAsync(long id, CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        WatchFolder? entity = await ctx.WatchFolders.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == id, ct);
        if (entity is null)
            throw new BusinessException($"监控目录 Id={id} 不存在");

        try
        {
            if (!Directory.Exists(entity.Path))
                return new WatchFolderTestResponse(false, false, null, $"目录不存在：{entity.Path}");

            string? sample = Directory
                .EnumerateFileSystemEntries(entity.Path)
                .FirstOrDefault();
            return new WatchFolderTestResponse(true, true, sample, null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return new WatchFolderTestResponse(false, true, null, ex.Message);
        }
    }

    private static WatchFolderResponse ToResponse(WatchFolder f, int mediaCount = 0) =>
        new(f.Id, f.Path, f.Alias, f.IsTransit, f.IsNetworkShare, f.Enabled, f.AutoScan, f.Priority,
            mediaCount, f.LastScanAt, f.LastReachableAt, f.RowVersion, f.CreatedAt, f.UpdatedAt);

    // 路径归一化：Trim + 末尾分隔符剥离（A 类「非空 / 长度」校验已上移至 DTO DataAnnotations，此处不再抛异常）
    private static string NormalizePath(string raw)
    {
        string trimmed = raw.Trim();
        // 末尾分隔符剥离（保留根路径，如 C:\ 或 / 不剥）
        if (trimmed.Length > 1
            && (trimmed.EndsWith('\\') || trimmed.EndsWith('/'))
            && !(trimmed.Length == 3 && trimmed[1] == ':'))
        {
            trimmed = trimmed.TrimEnd('\\', '/');
        }
        return trimmed;
    }

    private static char GuessSeparator(string path) =>
        path.Contains('\\') ? '\\' : '/';
}
