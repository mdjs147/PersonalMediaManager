using Microsoft.Extensions.DependencyInjection;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.System;
using PersonalMediaManager.Infrastructure.Platform.FileSystem;
using PersonalMediaManager.Infrastructure.Platform.Scheduling;
using PersonalMediaManager.Infrastructure.Platform.Security;
using PersonalMediaManager.Infrastructure.Platform.System;

namespace PersonalMediaManager.Infrastructure.Platform.DependencyInjection;

/// <summary>Infrastructure.Platform 注入扩展（安全 / 文件系统 / 调度）</summary>
/// <remarks>
/// 调用方需提前注册 IDataProtectionProvider（Host 通过 AddDataProtection().PersistKeysToFileSystem(...) 装配）。
/// 调度走 Quartz In-Memory store，由 AddQuartzHostedService 启动；
/// FullScan / LogRetention / WebhookRetry 三个 Job 与触发器在 AddQuartzScheduling 内一并注册（详见 QuartzModule）。
/// </remarks>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructurePlatform(this IServiceCollection services)
    {
        services.AddSingleton<IJwtSigningKeyProvider, SigningKeyProvider>();
        services.AddSingleton<IJwtTokenService, JwtTokenService>();
        services.AddSingleton<IPasswordHasher, PasswordHasher>();
        services.AddSingleton<IProtectedFieldService, DataProtectionFieldService>();
        services.AddSingleton<IFileMover, FileMover>();
        // 归档后源端空目录回收（File.CleanEmptyDir 开关由 ArchiveService 读取后调用）
        services.AddSingleton<IEmptyDirectoryCleaner, EmptyDirectoryCleaner>();
        services.AddSingleton<IFileProbe, FileProbe>();
        // 音频探测 / 重混（外部 ffprobe / ffmpeg；exe 路径由编排层从 Audio_FfmpegPath 解析后传入，本层不读 DB / 不读设置）
        services.AddSingleton<IMediaAudioProbe, FfprobeAudioProbe>();
        services.AddSingleton<IAudioRemuxer, FfmpegAudioRemuxer>();
        // ffmpeg / ffprobe 可用性自检（设置页「测试」按钮：跑 -version 验证可启动；同样只按给定 exe 路径执行，不读 DB）
        services.AddSingleton<IFfmpegToolTester, FfmpegToolTester>();
        // 强制匹配标识读取（pmm.txt / .url）：被 ProcessFileService 在解析后、决策前调用，命中即锚定指定 TMDB 条目
        services.AddSingleton<IForcedMatchMarkerStore, ForcedMatchMarkerStore>();
        // 内容去重采样哈希（头部 + 尾部 + 文件大小 SHA256）：被 ProcessFileService 在落库前调用
        services.AddSingleton<IFileHasher, FileHasher>();
        services.AddSingleton<IWriteCompletionDetector, WriteCompletionDetector>();
        services.AddSingleton<IFileWatcherFactory, WatcherAdapterFactory>();
        // AI 提供商 RPM 滑动窗口限流闸门（内存单例，跨请求共享；升级链调用前判定是否跳过本级到下一级）
        services.AddSingleton<IAiProviderRpmGate, AiProviderRpmGate>();
        // E.2 System：导入/导出 + 系统信息（涉及文件 IO 与 SQLite VACUUM INTO，属 Platform 层）
        services.AddScoped<ISystemService, SystemService>();
        services.AddQuartzScheduling();
        return services;
    }
}
