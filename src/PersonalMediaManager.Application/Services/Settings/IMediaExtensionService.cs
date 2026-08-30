using PersonalMediaManager.Application.Dtos.Settings;

namespace PersonalMediaManager.Application.Services.Settings;

/// <summary>媒体扩展名管理服务（System_MediaExtension CRUD）</summary>
public interface IMediaExtensionService
{
    Task<IReadOnlyList<MediaExtensionResponse>> ListAsync(CancellationToken ct = default);
    Task<MediaExtensionResponse> CreateAsync(CreateMediaExtensionRequest req, CancellationToken ct = default);
    Task<MediaExtensionResponse> UpdateAsync(UpdateMediaExtensionRequest req, CancellationToken ct = default);
    Task DeleteAsync(DeleteMediaExtensionRequest req, CancellationToken ct = default);
}
