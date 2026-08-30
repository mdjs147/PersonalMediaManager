using System.ComponentModel.DataAnnotations;
using PersonalMediaManager.Application.Common.Validation;

namespace PersonalMediaManager.Application.Dtos.Setup;

public sealed record SetupStatusResponse(bool IsCompleted, bool HasAdmin);

/// <summary>初始化配置就绪度</summary>
/// <remarks>
/// 反映「跑通媒体处理链」的必需配置当前状态，供向导断点续配与 Dashboard「配置完成度」卡片使用：
/// 监控目录数、TMDB 是否已配 ApiKey、分类总数 / 落点仍为占位 &lt;UNSET&gt; 的数量（未配置落点）、AI 提供商数。
/// </remarks>
public sealed record SetupChecklistResponse(
    bool IsCompleted,
    bool HasAdmin,
    int WatchFolderCount,
    bool TmdbHasApiKey,
    int CategoryTotalCount,
    int CategoryUnsetCount,
    int AiProviderCount);

public sealed record CreateAdminRequest(
    [RequiredNotBlank(ErrorMessage = "用户名不能为空")]
    string Username,
    [RequiredNotBlank(ErrorMessage = "密码不能为空")]
    [MinLength(6, ErrorMessage = "密码长度至少 6 位")]
    string Password);

public sealed record CreateAdminResponse(long UserId, string Username);
