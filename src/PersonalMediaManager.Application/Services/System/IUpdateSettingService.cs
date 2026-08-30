using PersonalMediaManager.Application.Dtos.System;

namespace PersonalMediaManager.Application.Services.System;

/// <summary>软件升级设置应用服务（PAT 加密落库 + 检查编排 + 跳过版本）</summary>
/// <remarks>
/// 持久化 7 个 System_Setting key：
///   - Update_GitHubOwner (明文)
///   - Update_GitHubRepo  (明文)
///   - Update_GitHubPat   (IProtectedFieldService 加密)
///   - Update_Enabled     (true / false；自动检查启用开关)
///   - Update_CheckIntervalHours (自动检查周期，[1, 720])
///   - Update_LastCheckJson (上次检查结果 JSON 快照)
///   - Update_SkippedVersion (用户跳过的版本号)
///
/// appsettings.json Update:Enabled / Update:CheckIntervalHours 仅作为新装库首次 seed 默认值；运行时一律以 db 为准。
/// 与 [[IUpdateChecker]] 协作：本服务只做编排 + 落库 + 版本比对；GitHub HTTP 由 IUpdateChecker 包揽。
/// </remarks>
public interface IUpdateSettingService
{
    /// <summary>取设置 + 上次检查结果（GET /system/update-check）</summary>
    Task<UpdateCheckSettingsResponse> GetAsync(CancellationToken ct = default);

    /// <summary>更新 owner/repo/pat（PUT /system/update-check/settings）</summary>
    Task<UpdateCheckSettingsResponse> UpdateAsync(UpdateSettingsRequest req, CancellationToken ct = default);

    /// <summary>立即检查 + 写库（POST /system/update-check/run；Launcher 周期触发 / WebUI 立即检查）</summary>
    Task<RunUpdateCheckResponse> RunCheckAsync(CancellationToken ct = default);

    /// <summary>用临时密钥试一次，不写库（POST /system/update-check/test）</summary>
    Task<TestUpdateCheckResponse> TestAsync(TestUpdateCheckRequest req, CancellationToken ct = default);

    /// <summary>跳过此版本（POST /system/update-check/skip；写 Update_SkippedVersion）</summary>
    Task SkipVersionAsync(string version, CancellationToken ct = default);
}
