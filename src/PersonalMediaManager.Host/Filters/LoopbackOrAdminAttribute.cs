using System.Net;
using Microsoft.AspNetCore.Mvc.Filters;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Host.Filters;

/// <summary>授权过滤器：Loopback 或 Admin 任一过即放行（CLAUDE.md §9.5 / 升级检查 D9.1）</summary>
/// <remarks>
/// 设计目标：Launcher 启动后 fire-and-forget 调本机 /system/update-check/run 时不需要 JWT，
///   但同样的 endpoint 被 WebUI（已登录 Admin）调用时也要通过。
///
/// 使用方式（必须与 [AllowAnonymous] 一并标，绕过全局 FallbackPolicy(Admin)）：
///   [HttpPost("update-check/run")]
///   [AllowAnonymous]      // 跳过 UseAuthorization 中间件的 Admin 兜底
///   [LoopbackOrAdmin]     // 接管授权：loopback 任意来源 OR 已认证 Admin
///   public Task&lt;IActionResult&gt; Run() ...
///
/// 校验顺序：
///   1. HttpContext.Connection.RemoteIpAddress.IsLoopback → 放行（IPAddress.IsLoopback 同时识别 IPv4 127.0.0.0/8 和 IPv6 ::1）
///   2. User.IsInRole(Admin) 且 IsAuthenticated → 放行
///   3. 其余 → 403 Forbid（不走 401，因为请求要么来自外网要么非 Admin，给登录页跳转无意义）
///
/// 安全性：Loopback 校验等价于「调用方拥有本机 root/admin 权限」—— 攻击者要伪造 loopback IP 必须 own 本机。
///   非本机攻击者无法走 NAT 或 IP 欺骗触发 IPAddress.IsLoopback（路由层已拒绝）。
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class LoopbackOrAdminAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        IPAddress? remote = context.HttpContext.Connection.RemoteIpAddress;
        if (remote is not null && IPAddress.IsLoopback(remote))
        {
            return; // loopback 任意来源放行
        }

        System.Security.Claims.ClaimsPrincipal user = context.HttpContext.User;
        bool isAdmin = user.Identity?.IsAuthenticated == true
            && user.IsInRole(UserRole.Admin.ToString());
        if (isAdmin)
        {
            return;
        }

        // 非 loopback 且非 Admin → 拒绝
        context.Result = new Microsoft.AspNetCore.Mvc.ForbidResult();
    }
}
