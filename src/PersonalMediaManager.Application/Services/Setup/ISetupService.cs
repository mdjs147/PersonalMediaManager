using PersonalMediaManager.Application.Dtos.Setup;

namespace PersonalMediaManager.Application.Services.Setup;

/// <summary>初始化向导服务契约（需求文档 §3.12 首次向导）</summary>
/// <remarks>
/// 实现放 Infrastructure.Persistence/Services/Setup/SetupService（§0.5 Application 仅 Microsoft.Extensions.*）。
/// 状态保存在 System_Setting Key="System.SetupCompleted"。
/// </remarks>
public interface ISetupService
{
    Task<SetupStatusResponse> GetStatusAsync(CancellationToken ct = default);

    Task<CreateAdminResponse> CreateAdminAsync(CreateAdminRequest req, CancellationToken ct = default);

    /// <summary>标记 Setup 完成；要求已存在至少 1 个 Admin，否则抛 BusinessException</summary>
    Task CompleteAsync(CancellationToken ct = default);

    /// <summary>未完成时其它 Admin 端点访问需返 1000「请先完成初始化」</summary>
    Task<bool> IsCompletedAsync(CancellationToken ct = default);

    /// <summary>配置就绪度（向导断点续配 + Dashboard 完成度卡片）</summary>
    Task<SetupChecklistResponse> GetChecklistAsync(CancellationToken ct = default);
}
