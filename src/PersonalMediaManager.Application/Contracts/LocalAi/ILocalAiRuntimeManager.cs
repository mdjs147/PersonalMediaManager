using PersonalMediaManager.Application.Dtos.LocalAi;

namespace PersonalMediaManager.Application.Contracts.LocalAi;

/// <summary>仅管理本程序启动的本地运行时</summary>
public interface ILocalAiRuntimeManager
{
    Task<IReadOnlyList<LocalAiModelDto>> GetModelsAsync(CancellationToken ct = default);
    Task<LocalAiStatusDto> GetStatusAsync(CancellationToken ct = default);
    Task DownloadAsync(string modelId, CancellationToken ct = default);
    Task CancelDownloadAsync(CancellationToken ct = default);
    Task UpdateSettingsAsync(LocalAiSettingsDto settings, CancellationToken ct = default);
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}
