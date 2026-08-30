using PersonalMediaManager.Application.Dtos.System;

namespace PersonalMediaManager.Application.Services.System;

/// <summary>系统维护应用服务（清空处理历史 / 重置所有配置）</summary>
/// <remarks>
/// 与 [[ISystemService]] 区别：本接口聚焦 DB 删表 + 重新种子；前者聚焦 zip 流式导入导出 + 版本元数据。
/// 实现位于 Infrastructure.Persistence（DB 操作 + DataSeeder 协作）。
/// 高危操作，由 Controller 强制 Admin + 前端二次确认双重护栏。
/// </remarks>
public interface ISystemMaintenanceService
{
    /// <summary>清空处理历史（Media_Item / Process_Step / Audit_AiCall / Audit_Operation / Audit_ScheduledTaskRun / Webhook_Delivery）</summary>
    /// <remarks>不动 Setting_*、Webhook_Subscription、User_Account、Tmdb_*Cache</remarks>
    Task<ClearHistoryResult> ClearHistoryAsync(CancellationToken ct = default);

    /// <summary>重置所有配置（System_Setting / Watch_* / Parse_* / Tmdb_Setting / Category_* / Webhook_Subscription）后重新调用 DataSeeder.SeedAsync 补默认值</summary>
    /// <remarks>不动 Media_Item 历史与 User_Account；建议用户提前 Export 备份</remarks>
    Task<ResetConfigResult> ResetConfigAsync(CancellationToken ct = default);
}
