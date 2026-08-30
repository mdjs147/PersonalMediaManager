namespace PersonalMediaManager.Application.Common;

/// <summary>appsettings.json Update 配置区块绑定 POCO</summary>
/// <remarks>
/// **仅作为 System_Setting 首次 seed 默认值**：新装库 LoadUpdateRowsAsync 兜底 EnsureRow 时读取一次；
/// 运行时 Enabled / CheckIntervalHours 全部以 System_Setting 表为准（UpdateSettingService 与 UpdateCheckWorker 都从 db 读）。
/// **不放任何密钥**（PAT 走 System_Setting + IProtectedFieldService）。
/// </remarks>
public sealed class UpdateOptions
{
    public const string SectionName = "Update";

    /// <summary>首次 seed 默认值：是否启用自动检查（System_Setting.Update_Enabled 兜底创建时使用）</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>首次 seed 默认值：检查周期小时数（System_Setting.Update_CheckIntervalHours 兜底创建时使用）</summary>
    public int CheckIntervalHours { get; set; } = 24;
}
