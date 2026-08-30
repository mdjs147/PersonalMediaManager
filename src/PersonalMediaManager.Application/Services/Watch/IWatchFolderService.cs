using PersonalMediaManager.Application.Dtos.Watch;

namespace PersonalMediaManager.Application.Services.Watch;

/// <summary>监控目录服务契约（Watch_Folder CRUD + 连通性测试）</summary>
/// <remarks>
/// 删除前必须检查是否有 in-flight Media_Item（SourcePath 以 Watch_Folder.Path 开头且状态非终态）；
/// 有则抛 BusinessException → 1000，不允许悬挂正在处理的文件。
/// </remarks>
public interface IWatchFolderService
{
    Task<IReadOnlyList<WatchFolderResponse>> ListAsync(CancellationToken ct = default);

    Task<WatchFolderResponse> CreateAsync(CreateWatchFolderRequest req, CancellationToken ct = default);

    Task<WatchFolderResponse> UpdateAsync(UpdateWatchFolderRequest req, CancellationToken ct = default);

    Task DeleteAsync(DeleteWatchFolderRequest req, CancellationToken ct = default);

    Task<WatchFolderTestResponse> TestAsync(long id, CancellationToken ct = default);
}
