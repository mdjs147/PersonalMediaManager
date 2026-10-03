using PersonalMediaManager.Application.Dtos.Ai;
namespace PersonalMediaManager.Application.Services.Parse;

/// <summary>独立批量参数持久化</summary>
public interface IAiBatchSettingsService
{
    Task<AiBatchSettingsDto> GetAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AiBatchProviderInfoDto>> GetProvidersAsync(CancellationToken ct = default);
    Task UpdateAsync(AiBatchSettingsDto settings, CancellationToken ct = default);
}
