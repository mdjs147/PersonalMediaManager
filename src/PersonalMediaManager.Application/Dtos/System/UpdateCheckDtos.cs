using System.ComponentModel.DataAnnotations;
using PersonalMediaManager.Application.Common.Validation;

namespace PersonalMediaManager.Application.Dtos.System;

/// <summary>GitHub /releases/latest 关键字段（HttpClient 反序列化目标）</summary>
/// <remarks>
/// 仅取本期会用到的字段；其它 GitHub 字段（assets / author / draft 等）按需追加。
/// TagName 形如 "v0.2.0"；Version 是 TagName 剥 v 前缀（解析失败时为空字符串）。
/// </remarks>
public sealed record GitHubLatestRelease(
    string TagName,
    string Version,
    string Name,
    string Body,
    string HtmlUrl,
    bool Prerelease,
    bool Draft,
    DateTimeOffset PublishedAt);

/// <summary>IUpdateChecker.CheckAsync 单次调用结果</summary>
/// <remarks>
/// 设计成「成功 / 304 / 业务失败」三态共享一个对象，调用方按 Success + NotModified 判别。
/// ErrorCategory 归一化字符串便于前端做不同提示：
///   - "auth"        → PAT 无效或已过期（HTTP 401）
///   - "not_found"   → 仓库不存在或 PAT 无权访问（HTTP 404）
///   - "rate_limit"  → GitHub 速率限制（HTTP 403 + X-RateLimit-Remaining=0）
///   - "network"     → 网络异常 / 超时
///   - "parse"       → 响应 JSON 解析失败
///   - "no_release"  → 仓库还没有任何 Release（HTTP 404 但仓库存在，仅检查更新场景遇到）
/// </remarks>
public sealed record UpdateCheckOutcome(
    bool Success,
    bool NotModified,
    bool HasNewVersion,
    string CurrentVersion,
    GitHubLatestRelease? Latest,
    string? Etag,
    int? HttpStatus,
    string? ErrorCategory,
    string? ErrorMessage,
    DateTimeOffset CheckedAt,
    long ElapsedMilliseconds);

/// <summary>持久化到 System_Setting.Update_LastCheckJson 的快照</summary>
/// <remarks>
/// 字段比 UpdateCheckOutcome 精简：去掉 Body（长度不可控），只留前端展示所需的轻量字段。
/// Etag 用于下次调用 If-None-Match 缓存命中。
/// </remarks>
public sealed record LastUpdateCheckSnapshot(
    DateTimeOffset CheckedAt,
    bool Success,
    bool HasNewVersion,
    string CurrentVersion,
    string? LatestVersion,
    string? LatestTagName,
    string? LatestName,
    string? LatestUrl,
    DateTimeOffset? PublishedAt,
    string? Etag,
    int? HttpStatus,
    string? ErrorCategory,
    string? ErrorMessage);

/// <summary>GET /system/update-check 响应（设置页 + 关于对话框共用）</summary>
public sealed record UpdateCheckSettingsResponse(
    string GitHubOwner,
    string GitHubRepo,
    bool HasPat,
    bool Enabled,
    int CheckIntervalHours,
    string? SkippedVersion,
    LastUpdateCheckSnapshot? LastCheck);

/// <summary>POST /system/update-check/settings 请求（更新 owner/repo/pat/enabled/interval）</summary>
/// <remarks>
/// GitHubPat 三态：null=不变（前端默认 PUT 留空让后端保持原值）/ ""=清空 / 非空=Protect 后落库。
/// IsPrivateRepo 仅 UI 层切换 PAT 字段必填校验，**不持久化**。
/// Enabled / CheckIntervalHours 走「null=不变」语义，单字段更新友好；
/// CheckIntervalHours 合法区间 [1, 720]（最长 30 天，避免误填导致 worker 长期不工作）。
/// </remarks>
public sealed record UpdateSettingsRequest(
    [RequiredNotBlank(ErrorMessage = "GitHub Owner 不能为空")]
    string GitHubOwner,
    [RequiredNotBlank(ErrorMessage = "GitHub Repo 不能为空")]
    string GitHubRepo,
    string? GitHubPat,
    bool IsPrivateRepo,
    bool? Enabled = null,
    [Range(1, 720, ErrorMessage = "检查周期必须在 1~720 小时之间")]
    int? CheckIntervalHours = null);

/// <summary>POST /system/update-check/test 请求（临时密钥试一次，不写库）</summary>
/// <remarks>
/// GitHubPat 不走加密落库；仅本次调用现传现用，请求结束即丢。
/// </remarks>
public sealed record TestUpdateCheckRequest(
    [RequiredNotBlank(ErrorMessage = "GitHub Owner 不能为空")]
    string GitHubOwner,
    [RequiredNotBlank(ErrorMessage = "GitHub Repo 不能为空")]
    string GitHubRepo,
    string? GitHubPat);

/// <summary>POST /system/update-check/test 响应</summary>
public sealed record TestUpdateCheckResponse(
    bool Success,
    bool HasNewVersion,
    string CurrentVersion,
    string? LatestVersion,
    int? HttpStatus,
    long ElapsedMilliseconds,
    string? ErrorCategory,
    string? ErrorMessage);

/// <summary>POST /system/update-check/run 响应（手动 / Loopback 周期调用）</summary>
/// <remarks>会同步把结果写入 Update_LastCheckJson + 返回当前快照。</remarks>
public sealed record RunUpdateCheckResponse(
    DateTimeOffset CheckedAt,
    bool Success,
    bool HasNewVersion,
    string CurrentVersion,
    string? LatestVersion,
    string? LatestUrl,
    int? HttpStatus,
    string? ErrorCategory,
    string? ErrorMessage);

/// <summary>POST /system/update-check/skip 请求（用户跳过此版本）</summary>
public sealed record SkipUpdateVersionRequest(
    [RequiredNotBlank(ErrorMessage = "版本号不能为空")]
    string Version);
