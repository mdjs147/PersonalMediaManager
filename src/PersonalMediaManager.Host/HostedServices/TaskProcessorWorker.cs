using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Services.Parse;

namespace PersonalMediaManager.Host.HostedServices;

/// <summary>TaskProcessorWorker（D6.2）— 单消费者从 PendingFileQueue 取文件串行处理</summary>
/// <remarks>
/// **一次只处理一个文件**是 PMM 的核心硬约束：
///   - 避免多线程同时改同名目标路径产生竞争（FileMover + MetadataFinalizer 都假定独占）
///   - AI / TMDB 调用走全局节流，并发会击穿 1 req/s 限制（D.3.1 InProvider 节流是 per-provider）
///   - 单文件平均 5~10s 处理足够追上 5MB/s 下载速率，无需并行
///
/// 实现：用 Channel.SingleReader=true 已经保证只有本 Worker 一个读者；
/// 仍叠加 SemaphoreSlim(1,1) 作为「同一 Worker 内绝不重入」的二次保险，
/// 防止未来误改成 ReadAsync 后 ProcessAsync 不 await 等错误。
///
/// IProcessFileService 走 IServiceScopeFactory 创建独立 scope：
///   ProcessFileService 内部依赖 DbContext + 多个 Scoped 服务，必须每文件独立 scope，
///   不可放在 Worker 构造函数（Singleton 持有 Scoped 是 DI 反模式）。
///
/// 异常吞咽：单文件失败仅写 Error 日志，Worker 继续取下一个；
/// ProcessFileService 自身负责把 MediaItem.Status 改成 Failed 并落库，
/// Worker 不做业务回滚（落库错误本就是业务层职责）。
/// </remarks>
public sealed class TaskProcessorWorker : BackgroundService
{
    private readonly IPendingFileQueue _queue;
    private readonly ITaskCancellationManager _cancellation;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TaskProcessorWorker> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TaskProcessorWorker(
        IPendingFileQueue queue,
        ITaskCancellationManager cancellation,
        IServiceScopeFactory scopeFactory,
        ILogger<TaskProcessorWorker> logger)
    {
        _queue = queue;
        _cancellation = cancellation;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TaskProcessorWorker 启动：单消费 + 全局 Semaphore(1,1)");

        try
        {
            await foreach (PendingFileItem item in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await ProcessOneAsync(item, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常关停
        }

        _logger.LogInformation("TaskProcessorWorker 退出");
    }

    private async Task ProcessOneAsync(PendingFileItem item, CancellationToken stoppingToken)
    {
        bool acquired = false;
        TaskCancellationRegistration? registration = null;
        try
        {
            // WaitAsync 在取消时抛 OperationCanceledException 且不会获得 lease；此时 acquired 保持 false，跳过 Release
            await _gate.WaitAsync(stoppingToken);
            acquired = true;

            registration = _cancellation.Register(item.FullPath, stoppingToken);
            using IServiceScope scope = _scopeFactory.CreateScope();
            IProcessFileService svc = scope.ServiceProvider.GetRequiredService<IProcessFileService>();
            ProcessFileOutcome outcome = await svc.ProcessAsync(item, registration.Token);
            _logger.LogInformation(
                "处理完成：Path={Path}, MediaItemId={MediaItemId}, Outcome={Outcome}",
                item.FullPath, outcome.MediaItemId, outcome.Outcome);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常关停时被 ProcessAsync 内部传播
            _logger.LogWarning("处理被取消：Path={Path}", item.FullPath);
        }
        catch (OperationCanceledException) when (registration?.IsCancellationRequested == true)
        {
            _logger.LogInformation("用户取消处理：Path={Path}", item.FullPath);
        }
        catch (Exception ex)
        {
            // 取消信号不能被吞：让外层 await foreach 走正常停机路径
            if (ex is OperationCanceledException && stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            _logger.LogError(ex, "处理失败（继续下一个）：Path={Path}", item.FullPath);
        }
        finally
        {
            registration?.Dispose();
            // r3 P3-r3.19：仅看 acquired，不再叠加 !stoppingToken.IsCancellationRequested
            //   旧实现「取消时不归还」存在两个问题：
            //     1. 与 SemaphoreSlim 契约不符（WaitAsync/Release 必须一一对应，否则余量永久-1，进程内若 Dispose 重用同实例会异常）
            //     2. 「避免误唤醒等待者」实际靠 await foreach + stoppingToken 已经让任何等待者拿到 OCE，不需要靠不归还来阻塞
            //   归还信号量是无条件的资源释放语义，正确做法是「拿到即归还」，无副作用
            if (acquired)
            {
                _gate.Release();
            }
        }
    }

    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();
    }
}
