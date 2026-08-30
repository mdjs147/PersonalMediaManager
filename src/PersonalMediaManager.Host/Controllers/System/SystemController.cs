using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.System;
using PersonalMediaManager.Application.Services.System;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Host.Filters;

namespace PersonalMediaManager.Host.Controllers.System;

/// <summary>System / 系统模块</summary>
/// <remarks>
/// 十四端点：
/// - GET /system/version 匿名 → 4 套版本号 + commit + buildTime，登录前显示版本号 / 巡检脚本拉取用
/// - GET /system/info  → 仪表盘 + 设置「关于」展示（含完整版本号 + 数据库 target/applied 对比 + 最近备份时间）
/// - POST /system/export → 直接返 zip 流（Content-Disposition: attachment）
/// - POST /system/import → multipart/form-data 上传 zip；成功返 RequiresRestart=true 让前端弹窗
/// - POST /system/clear-history → 清空 Media_Item / Process_Step / Audit_* / Webhook_Delivery 历史
/// - POST /system/reset-config → 重置 Setting_* / Watch_* / Parse_* / Category_* / Tmdb_Setting / Webhook_Subscription 后重种默认值
/// - POST /system/backup → 立即 VACUUM INTO 在线快照 + 密钥环打包到 backups/，按保留份数清理
/// - GET  /system/backups → 列出 backups/ 内备份 zip（时间倒序），供「从备份恢复」选择
/// - POST /system/restore → 从指定备份恢复（复用导入暂存，下次启动换库）
/// - GET  /system/update-check → 升级检查设置 + 上次结果
/// - PUT  /system/update-check/settings → 更新 owner/repo/pat
/// - POST /system/update-check/run → 立即检查 + 写库（Loopback 或 Admin 任一过）
/// - POST /system/update-check/test → 临时密钥试一次（不写库，Admin）
/// - POST /system/update-check/skip → 跳过此版本（Admin）
/// 除 /version 与 /update-check/run（Loopback||Admin）外其它要求 Admin 角色。
/// </remarks>
[ApiController]
[Route("system")]
[Authorize(Roles = nameof(UserRole.Admin))]
public sealed class SystemController : ApiControllerBase
{
    private readonly ISystemService _service;
    private readonly ISystemMaintenanceService _maintenance;
    private readonly IVersionInfoProvider _versionInfo;
    private readonly IUpdateSettingService _updateSetting;
    private readonly IBackupService _backup;

    public SystemController(
        ISystemService service,
        ISystemMaintenanceService maintenance,
        IVersionInfoProvider versionInfo,
        IUpdateSettingService updateSetting,
        IBackupService backup)
    {
        _service = service;
        _maintenance = maintenance;
        _versionInfo = versionInfo;
        _updateSetting = updateSetting;
        _backup = backup;
    }

    /// <summary>Version / 版本号</summary>
    /// <remarks>
    /// 匿名访问：登录页 footer / 巡检脚本 / 前后端对齐校验用，不暴露任何敏感字段（路径 / 磁盘大小不返）。
    /// 返完整 VersionInfoResponse：4 套版本号 + commit + dirty + buildTime + 数据库 target/applied/needsMigration。
    ///
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "product":"0.1.0", "backend":"0.1.0+a1b2c3d4", "frontend":"1.0.0", "database":{"target":"0.1.0","applied":"0.1.0","appliedMigrationId":"20260526...","needsMigration":false}, "commit":"a1b2c3d4", "dirty":false, "buildTime":"2026-05-27T01:33:32Z", "framework":".NET 10.0" }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 9000 ServerError — 反射 / db 查询失败（兜底返 unknown 而非抛错；仅严重场景命中）
    /// </remarks>
    /// <response code="200">版本号</response>
    [HttpGet("version")]
    [AllowAnonymous]
    [ProducesResponseType<ApiResponse<VersionInfoResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Version(CancellationToken ct)
    {
        VersionInfoResponse data = await _versionInfo.GetFullAsync(ct);
        return Ok(Wrap(data));
    }

    /// <summary>Info / 系统信息</summary>
    /// <remarks>
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "version":"1.0.0", "os":"Windows", "osVersion":"...", "architecture":"X64", "runtimeVersion":".NET 10.0", "startedAt":"...", "uptime":"01:23:45", "dataRoot":"C:\\Users\\...", "dbSizeBytes":12345, "logDirSizeBytes":2345, "postersCacheSizeBytes":1234 }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 9000 ServerError — 数据目录访问失败
    ///
    /// 错误响应：
    /// ```json
    /// { "code":9000, "message":"...", "data":null, "requestId":"..." }
    /// ```
    /// </remarks>
    /// <response code="200">系统信息</response>
    [HttpGet("info")]
    [ProducesResponseType<ApiResponse<SystemInfoResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Info(CancellationToken ct)
    {
        SystemInfoResponse data = await _service.GetInfoAsync(ct);
        return Ok(Wrap(data));
    }

    /// <summary>Export / 导出快照</summary>
    /// <remarks>
    /// 直接返回 zip 二进制流（Content-Disposition: attachment; filename=pmm-export-yyyyMMddHHmmss.zip）。
    ///
    /// 流式响应的两条错误路径（r3 P3-r3.11 显式记录）：
    /// 1. 响应头未写出（VACUUM INTO 失败 / ZipArchive 初始化失败）→ ExceptionHandlerMiddleware
    ///    检测 HttpResponse.HasStarted=false 后回滚为结构化 JSON 错误体（含 requestId）。
    /// 2. 响应头已写出 + 流式写入过程中失败（磁盘满 / 客户端断开 / 网络抖动）→ 无法回滚 JSON，
    ///    只能终止连接；客户端表现为「下载中断」，没有结构化错误体可读。
    ///    服务端侧仍会通过 ExceptionHandlerMiddleware 落 Error 日志 + 标记 requestId，
    ///    OPS 可凭客户端报告的下载中断时间在日志中关联到具体 RequestId。
    ///
    /// 错误码：
    /// - 9000 ServerError — VACUUM INTO 失败 / 文件 IO 失败 / 流式中断
    ///
    /// 错误响应（仅响应头未写出时）：
    /// ```json
    /// { "code":9000, "message":"...", "data":null, "requestId":"..." }
    /// ```
    /// </remarks>
    /// <response code="200">zip 二进制流</response>
    /// <response code="500">导出失败（响应头未写出时返结构化 JSON；已流式中途失败仅终止连接，无 JSON 体）</response>
    [HttpPost("export")]
    [Produces("application/zip")]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status500InternalServerError)]
    public async Task Export(CancellationToken ct)
    {
        string fileName = $"pmm-export-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.zip";
        Response.ContentType = "application/zip";
        Response.Headers.ContentDisposition = $"attachment; filename={fileName}";
        await _service.ExportAsync(Response.Body, ct);
    }

    /// <summary>Import / 导入快照</summary>
    /// <remarks>
    /// 请求：multipart/form-data，file 字段为 zip 文件。
    /// 导入采用「暂存 + 启动期换库」：本端点仅把新库写到 &lt;db&gt;.import-pending 并合并密钥环，
    /// 不在运行中热替换主库（WAL 模式下热替换会被旧 WAL 回写覆盖 → 导入不生效）。换库在下次启动开库前自动完成。
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "success":true, "stagedPath":"...\\pmm.db.import-pending", "requiresRestart":true, "message":"导入包已暂存，请完整退出并重启应用…" }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 文件缺失 / zip 格式错误 / Zip Slip 守卫触发 / 缺 pmm.db / pmm.db 非合法 SQLite
    /// - 9000 ServerError — 文件 IO 失败
    ///
    /// 错误响应：
    /// ```json
    /// { "code":1000, "message":"...", "data":null, "requestId":"..." }
    /// ```
    /// </remarks>
    /// <response code="200">导入结果（已暂存，需重启换库）</response>
    [HttpPost("import")]
    [ProducesResponseType<ApiResponse<ImportResult>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Import(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            throw new BusinessException("未上传文件");
        }
        await using Stream input = file.OpenReadStream();
        ImportResult data = await _service.ImportAsync(input, ct);
        return Ok(Wrap(data));
    }

    /// <summary>ClearHistory / 清空历史</summary>
    /// <remarks>
    /// 高危：删除 Media_Item + Process_Step + Audit_AiCall + Audit_Operation + Audit_ScheduledTaskRun + Webhook_Delivery 全部行；
    /// 不动配置 / 用户 / TMDB 缓存。前端必须二次确认。
    /// 请求体：空。
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "mediaItems":1058, "processSteps":4231, "aiCalls":120, "operations":342, "scheduledTaskRuns":56, "webhookDeliveries":89 }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 9000 ServerError — 数据库 IO 失败
    /// </remarks>
    /// <response code="200">已清空，返回各表删除条数</response>
    [HttpPost("clear-history")]
    [ProducesResponseType<ApiResponse<ClearHistoryResult>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ClearHistory(CancellationToken ct)
    {
        ClearHistoryResult data = await _maintenance.ClearHistoryAsync(ct);
        return Ok(Wrap(data));
    }

    /// <summary>ResetConfig / 重置配置</summary>
    /// <remarks>
    /// 高危：删除 System_Setting / Watch_* / Parse_* / Tmdb_Setting / Category_* / Webhook_Subscription 全部行，然后调 DataSeeder 重新种子默认分类与解析规则；
    /// 不动 Media_Item 历史与 User_Account，但删除 Parse_AiProvider 会经 FK CASCADE 连带清除其历史调用统计 Audit_AiCall（r4 P3-r4.4 澄清：providers 重置后统计失去归属，属预期副作用）。
    /// 前端必须二次确认；建议用户先调 /system/export 备份。
    /// 请求体：空。
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "systemSettings":3, "watchFolders":2, "watchIgnoreRules":5, "parseRules":12, "parseAiProviders":1, "tmdbSettings":1, "categories":5, "categoryMatchRules":5, "webhookSubscriptions":0, "reseededDefaults":true }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 9000 ServerError — 数据库 IO 失败 / 种子补齐失败
    /// </remarks>
    /// <response code="200">已重置，返回各表删除条数 + 是否重新种子</response>
    [HttpPost("reset-config")]
    [ProducesResponseType<ApiResponse<ResetConfigResult>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ResetConfig(CancellationToken ct)
    {
        ResetConfigResult data = await _maintenance.ResetConfigAsync(ct);
        return Ok(Wrap(data));
    }

    /// <summary>Backup / 立即备份</summary>
    /// <remarks>
    /// 立即对数据库做一次 VACUUM INTO 在线快照 + 密钥环打包，落 backups/pmm-backup-yyyyMMddHHmmss.zip，并按 Backup_RetainCount 清理旧备份。
    /// 手动触发忽略 Backup_Enabled 开关（定时备份才看开关）。请求体：空。
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "performed":true, "fileName":"pmm-backup-20260606040000.zip", "sizeBytes":1234567, "prunedCount":1 }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 9000 ServerError — VACUUM INTO 失败 / 文件 IO 失败
    /// 通用错误响应：
    /// ```json
    /// { "code":9000, "message":"...", "data":null, "requestId":"..." }
    /// ```
    /// </remarks>
    /// <response code="200">备份完成，返回文件名 / 大小 / 清理份数</response>
    [HttpPost("backup")]
    [ProducesResponseType<ApiResponse<BackupResult>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Backup(CancellationToken ct)
    {
        BackupResult data = await _backup.CreateAsync(ct);
        return Ok(Wrap(data));
    }

    /// <summary>Backups / 备份列表</summary>
    /// <remarks>
    /// 列出数据目录 backups/ 下全部备份 zip（自动 + 手动），按时间倒序，供「从备份恢复」选择恢复点。
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":[ { "fileName":"pmm-backup-20260606040000.zip", "sizeBytes":1234567, "createdAt":"2026-06-06T04:00:00Z" } ], "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 9000 ServerError — 备份目录访问失败
    /// 通用错误响应：
    /// ```json
    /// { "code":9000, "message":"...", "data":null, "requestId":"..." }
    /// ```
    /// </remarks>
    /// <response code="200">备份文件列表（时间倒序）</response>
    [HttpGet("backups")]
    [ProducesResponseType<ApiResponse<IReadOnlyList<BackupFileInfo>>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Backups(CancellationToken ct)
    {
        IReadOnlyList<BackupFileInfo> data = await _service.ListBackupsAsync(ct);
        return Ok(Wrap(data));
    }

    /// <summary>Restore / 从备份恢复</summary>
    /// <remarks>
    /// 从 backups/ 内指定备份恢复，复用「导入暂存 + 启动期换库」：把所选备份的 pmm.db 写到 .import-pending + 合并密钥环，
    /// 下次完整退出重启时原子换库（旧库自动备份为 pmm.db.preimport-*）。fileName 严格校验防路径穿越。
    /// 请求体：
    /// ```json
    /// { "fileName":"pmm-backup-20260606040000.zip" }
    /// ```
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "success":true, "stagedPath":"...\\pmm.db.import-pending", "requiresRestart":true, "message":"导入包已暂存，请完整退出并重启应用…" }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 文件名非法（路径穿越守卫）/ 备份文件不存在 / 备份内 pmm.db 非合法 SQLite
    /// - 9000 ServerError — 文件 IO 失败
    /// 通用错误响应：
    /// ```json
    /// { "code":1000, "message":"...", "data":null, "requestId":"..." }
    /// ```
    /// </remarks>
    /// <response code="200">恢复包已暂存（需重启换库）</response>
    [HttpPost("restore")]
    [ProducesResponseType<ApiResponse<ImportResult>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Restore([FromBody] RestoreBackupRequest req, CancellationToken ct)
    {
        ImportResult data = await _service.RestoreBackupAsync(req.FileName, ct);
        return Ok(Wrap(data));
    }

    /// <summary>UpdateCheck / 升级检查设置</summary>
    /// <remarks>
    /// 取 5 个 System_Setting Update_* key 当前值 + 上次检查结果 + appsettings Update 行为开关。
    /// PAT 只返 HasPat 布尔，**不返明文密文**。
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "gitHubOwner":"mdjs147", "gitHubRepo":"PersonalMediaManager", "hasPat":true, "enabled":true, "checkIntervalHours":24, "skippedVersion":null, "lastCheck":{ "checkedAt":"...", "success":true, "hasNewVersion":false, "currentVersion":"0.1.0", "latestVersion":"0.1.0", "latestTagName":"v0.1.0", "latestName":"v0.1.0", "latestUrl":"https://github.com/...", "publishedAt":"...", "etag":"\"abc...\"", "httpStatus":200, "errorCategory":null, "errorMessage":null } }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 9000 ServerError — 数据库 IO 失败
    /// </remarks>
    /// <response code="200">设置 + 上次结果</response>
    [HttpGet("update-check")]
    [AllowAnonymous]
    [LoopbackOrAdmin]
    [ProducesResponseType<ApiResponse<UpdateCheckSettingsResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetUpdateCheck(CancellationToken ct)
    {
        UpdateCheckSettingsResponse data = await _updateSetting.GetAsync(ct);
        return Ok(Wrap(data));
    }

    /// <summary>UpdateSettings / 更新设置</summary>
    /// <remarks>
    /// 请求体（gitHubPat 三态：null=不变 / ""=清空 / 非空=Protect 后落库）：
    /// ```json
    /// { "gitHubOwner":"mdjs147", "gitHubRepo":"PersonalMediaManager", "gitHubPat":null, "isPrivateRepo":true }
    /// ```
    /// 成功响应：同 GET /update-check。
    /// 错误码：
    /// - 1000 BusinessError — owner/repo 为空 / 私有仓但 PAT 缺失
    /// </remarks>
    /// <response code="200">更新后的设置 + 上次结果</response>
    [HttpPut("update-check/settings")]
    [ProducesResponseType<ApiResponse<UpdateCheckSettingsResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateUpdateCheckSettings([FromBody] UpdateSettingsRequest req, CancellationToken ct)
    {
        UpdateCheckSettingsResponse data = await _updateSetting.UpdateAsync(req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>RunCheck / 立即检查</summary>
    /// <remarks>
    /// **授权**：Loopback（127.0.0.1 / ::1）任意来源 OR 已认证 Admin — 任一过即放行。
    /// 用途：
    ///   - Launcher 启动后 fire-and-forget 调本机 /system/update-check/run（无 JWT，loopback 放行）
    ///   - WebUI 用户点「立即检查」按钮（JWT + Admin 放行）
    /// 副作用：写 System_Setting.Update_LastCheckJson 快照（覆盖式）。
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "checkedAt":"...", "success":true, "hasNewVersion":false, "currentVersion":"0.1.0", "latestVersion":"0.1.0", "latestUrl":"https://github.com/...", "httpStatus":200, "errorCategory":null, "errorMessage":null }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 9000 ServerError — 数据库 IO 失败 / 上游 GitHub 不可达时仍返 200（失败信息写入 errorCategory）
    ///
    /// 失败响应（success=false 时仍是 HTTP 200）：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "success":false, "errorCategory":"auth", "errorMessage":"GitHub PAT 无效或已过期", "httpStatus":401, "checkedAt":"...", "currentVersion":"0.1.0", "hasNewVersion":false, "latestVersion":null, "latestUrl":null }, "requestId":"..." }
    /// ```
    /// </remarks>
    /// <response code="200">本次检查结果（成功 / 失败均返 HTTP 200，按 data.success 判别）</response>
    [HttpPost("update-check/run")]
    [AllowAnonymous]
    [LoopbackOrAdmin]
    [ProducesResponseType<ApiResponse<RunUpdateCheckResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RunUpdateCheck(CancellationToken ct)
    {
        RunUpdateCheckResponse data = await _updateSetting.RunCheckAsync(ct);
        return Ok(Wrap(data));
    }

    /// <summary>TestCheck / 测试检查</summary>
    /// <remarks>
    /// 用临时 PAT 试一次 GitHub /releases/latest 调用，**不写库**；用于 WebUI 设置页输入 PAT 后点「测试连接」。
    /// 请求体：
    /// ```json
    /// { "gitHubOwner":"mdjs147", "gitHubRepo":"PersonalMediaManager", "gitHubPat":"ghp_xxx" }
    /// ```
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "success":true, "hasNewVersion":false, "currentVersion":"0.1.0", "latestVersion":"0.1.0", "httpStatus":200, "elapsedMilliseconds":234, "errorCategory":null, "errorMessage":null }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — owner/repo 为空
    /// </remarks>
    /// <response code="200">测试结果（成功 / 失败均返 HTTP 200，按 data.success 判别）</response>
    [HttpPost("update-check/test")]
    [ProducesResponseType<ApiResponse<TestUpdateCheckResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> TestUpdateCheck([FromBody] TestUpdateCheckRequest req, CancellationToken ct)
    {
        TestUpdateCheckResponse data = await _updateSetting.TestAsync(req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>SkipVersion / 跳过版本</summary>
    /// <remarks>
    /// 写 System_Setting.Update_SkippedVersion；下次气泡 / WebUI 提示命中此版本时跳过。
    /// 请求体：
    /// ```json
    /// { "version":"0.2.0" }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — version 为空
    /// </remarks>
    /// <response code="200">已跳过</response>
    [HttpPost("update-check/skip")]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SkipUpdateVersion([FromBody] SkipUpdateVersionRequest req, CancellationToken ct)
    {
        await _updateSetting.SkipVersionAsync(req.Version, ct);
        return Ok(Wrap<object?>(null));
    }
}
