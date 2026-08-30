namespace PersonalMediaManager.Application.Services.Scan;

/// <summary>全量扫描协调器契约（被 FullScanJob 周期触发）</summary>
/// <remarks>
/// 行为：枚举所有 Enabled=true 的 WatchFolder，按 Priority 升序逐个扫描，把发现的待处理文件入主处理 channel。
/// 实现：D7.6 ScanService 提供。Platform 层 FullScanJob 只通过本接口触达 Application，避免反向引用。
/// 异常策略：实现侧应「按文件夹兜底」，单个文件夹失败不阻断其他文件夹；总入口若整体失败由 Job 记错误日志。
/// </remarks>
public interface IFullScanCoordinator
{
    /// <summary>对所有启用的监控目录执行一次全量扫描</summary>
    Task RunAsync(CancellationToken cancellationToken);
}
