using PersonalMediaManager.Application.Dtos.LocalAi;

namespace PersonalMediaManager.Application.Services.LocalAi;

/// <summary>本地模型专用设置存储</summary>
public interface ILocalAiSettingsService
{
    Task<LocalAiSettingsDto> GetAsync(CancellationToken ct = default);
    Task UpdateAsync(LocalAiSettingsDto settings, CancellationToken ct = default);
}
