using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Services.Setup;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Host.Middleware;

/// <summary>SetupGuardMiddleware：未完成 Setup 时拦截非白名单 API 端点</summary>
/// <remarks>
/// 仅拦截 API 控制器端点：SPA 外壳（MapFallbackToFile 返回 index.html）、静态文件、
/// SignalR Hub / Scalar / OpenAPI 等非控制器端点、未匹配路由的请求一律放行——否则
/// Setup 完成前前端页面（含初始化向导本身）根本无法加载。
/// 放行规则（精确路径，r2 P2-r2.2 收紧为精确匹配防子端点全开）：
/// - 始终放行：GET /api/setup/status、GET /api/setup/checklist、POST /api/auth/login、GET /api/health、GET /api/diag/boom
/// - 仅「初始化未完成」放行：POST /api/setup/admin、POST /api/setup/complete
///   （已完成后这两个写端点会沦为「匿名 → 创建 Admin / 标记完成」的认证绕过后门，故已完成即拦截；
///    SetupService.CreateAdminAsync 另有 hasAdmin 守门做纵深防御）
/// CORS pre-flight（OPTIONS 方法）一律放行：浏览器先发 OPTIONS 试探，被 1000 拦截会导致整个跨域请求失败。
/// 其余 API 控制器端点：未完成时仅放行「已认证 Admin」（向导建账号登录后配置 TMDB/监控/分类/AI），
/// 匿名 / 非 Admin 一律返 { code:1000, message:"请先完成初始化", ... }。
/// 检查走 ISetupService.IsCompletedAsync；首次完成后所有后续请求免检（应用层缓存）。
/// 缓存策略：完成后切到 _completed=true 永久放行；初始化阶段每请求查一次（开发期可接受）。
/// </remarks>
public sealed class SetupGuardMiddleware
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // 始终放行（无论初始化是否完成）：状态查询 / 配置就绪度 / 登录 / 健康检查 / 诊断
    // - checklist：向导续配与 Dashboard 完成度卡片都要读，仅含计数与布尔标志、无密钥明文，匿名可读无敏感泄露；
    // - login：向导建账号后须登录拿 JWT 才能调后续 settings/* 端点（complete 在最后才置完成），故未完成阶段也必须可达。
    private static readonly HashSet<string> AlwaysAllowPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/setup/status",
        "/api/setup/checklist",
        "/api/auth/login",
        "/api/health",
        "/api/diag/boom",
    };

    // 仅「初始化未完成」时可用的写端点：已完成后它们会沦为「匿名 → 创建 Admin / 标记完成」的认证绕过后门，必须拦死
    private static readonly HashSet<string> SetupWritePaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/setup/admin",
        "/api/setup/complete",
    };

    private static bool _completedCache;

    private readonly RequestDelegate _next;

    public SetupGuardMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ISetupService setup)
    {
        // CORS pre-flight：浏览器 OPTIONS 试探请求一律放行，避免预检被 1000 拦截导致整个跨域请求失败
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            await _next(context);
            return;
        }

        string path = context.Request.Path.Value ?? string.Empty;

        // 始终放行：状态查询 / 健康检查 / 诊断（初始化前后都需可达）
        if (AlwaysAllowPaths.Contains(path))
        {
            await _next(context);
            return;
        }

        bool isSetupWrite = SetupWritePaths.Contains(path);

        // 非 setup 写端点中：SPA 外壳（MapFallbackToFile）/ 静态文件 / 未匹配路由一律放行，仅拦 API 控制器端点。
        // setup 写端点本身是控制器端点，需继续走下面的完成态判定（已完成要拦死后门）。
        // WebApplication 自动在管线最前插入 UseRouting，此处 GetEndpoint() 已可用。
        if (!isSetupWrite
            && context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>() is null)
        {
            await _next(context);
            return;
        }

        bool completed = _completedCache;
        if (!completed)
        {
            completed = await setup.IsCompletedAsync(context.RequestAborted);
            if (completed) _completedCache = true;
        }

        if (isSetupWrite)
        {
            // 初始化已完成后，创建 Admin / 标记完成属越权后门 → 拦死（SetupService 另有 hasAdmin 守门做纵深防御）
            if (completed)
            {
                await WriteJsonErrorAsync(context, "初始化向导已完成");
                return;
            }
            await _next(context);
            return;
        }

        // 普通 API 控制器端点：未完成初始化时——已认证 Admin 放行，其余拦截。
        // 向导建账号 → 登录后，第 2~N 步要调 settings/* 等 Admin 端点配置 TMDB / 监控 / 分类 / AI；
        // 能登录成 Admin 者本就拥有全部设置权限（无安全回归），且 complete 前唯一可登录者即刚建的那个 Admin。
        if (!completed)
        {
            bool isAuthedAdmin = context.User.Identity?.IsAuthenticated == true
                && context.User.IsInRole(UserRole.Admin.ToString());
            if (!isAuthedAdmin)
            {
                await WriteJsonErrorAsync(context, "请先完成初始化");
                return;
            }
        }

        await _next(context);
    }

    private static async Task WriteJsonErrorAsync(HttpContext context, string message)
    {
        if (context.Response.HasStarted) return;
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        string requestId = context.Items.TryGetValue(RequestIdMiddleware.ItemsKey, out object? rid)
            ? rid?.ToString() ?? string.Empty : string.Empty;
        var body = new
        {
            code = ApiCode.BusinessError,
            message,
            data = (object?)null,
            requestId,
        };
        await context.Response.WriteAsync(JsonSerializer.Serialize(body, JsonOpts));
    }

    /// <summary>测试夹具用：每个新 fixture 都重置缓存避免互相污染</summary>
    public static void ResetCacheForTest() => _completedCache = false;
}
