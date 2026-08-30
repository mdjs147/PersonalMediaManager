using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace PersonalMediaManager.Host.Composition;

/// <summary>给所有 API 控制器统一加 api/ 路由前缀</summary>
/// <remarks>
/// 隔离 API 与 SPA 的 URL 命名空间：前端 Vue Router 用 history 模式，页面路由是
/// /review、/history、/settings/* 等裸路径；控制器若也挂裸路径，SPA 深链硬刷新会
/// 命中控制器返回 JSON 而非页面。统一加 api/ 前缀后，非 /api/* 的导航请求一律落到
/// MapFallbackToFile 返回 index.html，由 Vue Router 接管。
/// 在 AddControllers 的 MvcOptions.Conventions 注册一次即可，新增控制器自动套用。
/// </remarks>
public sealed class ApiRoutePrefixConvention : IApplicationModelConvention
{
    private readonly AttributeRouteModel _prefix = new(new RouteAttribute("api"));

    public void Apply(ApplicationModel application)
    {
        foreach (ControllerModel controller in application.Controllers)
        {
            foreach (SelectorModel selector in controller.Selectors)
            {
                selector.AttributeRouteModel = selector.AttributeRouteModel is null
                    ? _prefix
                    : AttributeRouteModel.CombineAttributeRouteModel(_prefix, selector.AttributeRouteModel);
            }
        }
    }
}
