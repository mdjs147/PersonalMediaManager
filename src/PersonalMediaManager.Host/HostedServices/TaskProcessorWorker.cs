using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Services.Parse;

namespace PersonalMediaManager.Host.HostedServices;

/// <summary>有界准备、批量推理、逐项续行的单消费者</summary>
/// <remarks>每项独立 scope；仅 AI 请求可暂停，状态写入和归档续行始终串行。</remarks>
public sealed class TaskProcessorWorker : BackgroundService
{
    private readonly IPendingFileQueue _queue;
    private readonly ITaskCancellationManager _cancellation;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TaskProcessorWorker> _logger;
    private readonly AiBatchOptions _batch;
    private readonly IAiBatchSettingsService? _batchSettings;

    public TaskProcessorWorker(
        IPendingFileQueue queue,
        ITaskCancellationManager cancellation,
        IServiceScopeFactory scopeFactory,
        ILogger<TaskProcessorWorker> logger, AiBatchOptions? batch = null, IAiBatchSettingsService? batchSettings = null)
    {
        _queue = queue;
        _cancellation = cancellation;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _batch = batch ?? new();
        _batch.Validate();
        _batchSettings = batchSettings;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TaskProcessorWorker 启动：有界 AI 批处理 + 串行状态写入及归档");

        try
        {
            await foreach (PendingFileItem item in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                AiBatchOptions options;
                try
                {
                    options = _batchSettings is null ? _batch : (await _batchSettings.GetAsync(stoppingToken)).ToOptions();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    // 首项已出队：配置故障不得丢项或终止消费者，保守退回默认单条。
                    _logger.LogWarning("读取 AI 批量设置失败（{ExceptionType}），当前项改用单条处理", ex.GetType().Name);
                    options = new() { MaxItems = 1, MaxWaitMilliseconds = 0 };
                }
                List<PendingFileItem> items = [item];
                using CancellationTokenSource window = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                window.CancelAfter(TimeSpan.FromMilliseconds(options.MaxWaitMilliseconds));
                while (items.Count < options.MaxItems)
                {
                    if (_queue.Reader.TryRead(out PendingFileItem? next)) { items.Add(next); continue; }
                    try { if (!await _queue.Reader.WaitToReadAsync(window.Token)) break; }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { break; }
                }
                await AiBatchPipeline.RunAsync(items.Select<PendingFileItem, Func<CancellationToken, Task>>(pending =>
                    token => ProcessOneAsync(pending, token)).ToArray(), options, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常关停
        }

        _logger.LogInformation("TaskProcessorWorker 退出");
    }

    private async Task ProcessOneAsync(PendingFileItem item, CancellationToken stoppingToken)
    {
        TaskCancellationRegistration? registration = null;
        try
        {
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

        }
    }

}
