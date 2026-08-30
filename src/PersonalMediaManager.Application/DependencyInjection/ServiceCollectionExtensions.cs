using Microsoft.Extensions.DependencyInjection;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Logs;
using PersonalMediaManager.Application.Services.Parse;

namespace PersonalMediaManager.Application.DependencyInjection;

/// <summary>Application 层 DI 扩展（仅注册纯 .NET 实现，外部契约由调用方注入）</summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<IAiCallOrchestrator, AiCallOrchestrator>();
        // 文件夹级 series 复用缓存：同目录首个文件锁定 TMDB 后，后续文件跳过 AI 兜底（进程级单例，重启清空）
        services.AddSingleton<IFolderSeriesCache, FolderSeriesCache>();
        // 测试集 AI 顾问（Triage / 后续 SuggestRule）：依赖 IAiChatClient（由 External 注入）
        services.AddScoped<IAiAdvisor, AiAdvisor>();
        // 主处理管线队列：单实例进程内共享，Watcher / FullScan / Manual 三种生产者写入，TaskProcessorWorker 唯一消费
        services.AddSingleton<IPendingFileQueue, PendingFileQueue>();
        // 单任务取消协调：排队项以数据库终态跳过；处理中项以独立 linked token 停止
        services.AddSingleton<ITaskCancellationManager, TaskCancellationManager>();
        // 监控重建信号总线：NetworkShareMonitor（可达性翻转）/ WatchFolderService（CRUD）/ WatcherAdapter（FSW 故障）写入，FileWatcherWorker 唯一消费
        services.AddSingleton<IWatchRebuildSignal, WatchRebuildSignal>();
        // Webhook 出站队列：ArchiveService / WebhookRetryJob 写入，WebhookOutboxWorker 唯一消费
        services.AddSingleton<IWebhookOutboxQueue, WebhookOutboxQueue>();
        // D7.9 日志查询：纯文件 IO，无 EF 依赖，放 Application 层
        services.AddScoped<ILogQueryService, LogQueryService>();
        // D8.3 ITaskNotifier 默认 NullObject；Host 用 Replace 覆盖为 SignalRTaskNotifier
        services.AddSingleton<ITaskNotifier, NullTaskNotifier>();
        return services;
    }
}
