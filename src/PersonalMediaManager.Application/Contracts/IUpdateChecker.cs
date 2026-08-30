using PersonalMediaManager.Application.Dtos.System;

namespace PersonalMediaManager.Application.Contracts;

/// <summary>软件升级检查契约（GitHub Releases 数据源）</summary>
/// <remarks>
/// 实现位置：Infrastructure.External/Update/GitHubUpdateChecker.cs。
/// 端点：GET https://api.github.com/repos/{owner}/{repo}/releases/latest
/// 鉴权：
///   - 有 PAT → Authorization: Bearer {pat}，速率 5000/h
///   - 无 PAT → 匿名访问，速率 60/h
/// 缓存：If-None-Match 头携带上次返回的 ETag；304 不计入速率上限。
/// User-Agent：PersonalMediaManager/{ProductVersion}（GitHub 强制，缺会 403）。
/// 所有异常归集为 <see cref="UpdateCheckOutcome.Success"/>=false + ErrorCategory，不抛。
/// </remarks>
public interface IUpdateChecker
{
    /// <summary>检查 GitHub 最新 Release 是否比 currentVersion 更新</summary>
    /// <param name="owner">仓库所有者，如 "mdjs147"</param>
    /// <param name="repo">仓库名，如 "PersonalMediaManager"</param>
    /// <param name="pat">PAT；null/empty 走匿名（公开仓库 60 req/h）</param>
    /// <param name="currentVersion">当前主版本号（PmmProductVersion，如 "0.1.0"，不含 v 前缀）</param>
    /// <param name="etag">上次缓存的 ETag；非空时走 If-None-Match</param>
    /// <param name="ct">取消令牌</param>
    Task<UpdateCheckOutcome> CheckAsync(
        string owner,
        string repo,
        string? pat,
        string currentVersion,
        string? etag,
        CancellationToken ct = default);
}
