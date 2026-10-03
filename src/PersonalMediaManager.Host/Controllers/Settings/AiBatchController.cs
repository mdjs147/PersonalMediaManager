using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.Ai;
using PersonalMediaManager.Application.Services.Parse;
namespace PersonalMediaManager.Host.Controllers.Settings;

/// <summary>独立 AI 批量参数</summary>
[ApiController]
[Authorize(Roles = "Admin")]
[Route("settings/ai-batch")]
public sealed class AiBatchController(IAiBatchSettingsService settings) : ApiControllerBase
{
    /// <summary>读取批量参数</summary>
    /// <remarks>
    /// 成功：{"code":0,"message":"ok","data":{"externalBatchSize":1,"localBatchSize":1,"maxWaitMilliseconds":50,"contextTokenBudget":8192,"maxOutputTokens":2048},"requestId":"..."}
    /// 错误码：9000 服务错误。
    /// 错误：{"code":9000,"message":"读取失败","data":null,"requestId":"..."}
    /// </remarks>
    /// <response code="200">当前参数</response>
    [HttpGet]
    [ProducesResponseType<ApiResponse<AiBatchSettingsDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken ct) => Ok(Wrap(await settings.GetAsync(ct)));
    /// <summary>读取提供商批量推荐</summary>
    /// <remarks>
    /// 成功：{"code":0,"message":"ok","data":[],"requestId":"..."}
    /// 错误码：9000 服务错误。
    /// 错误：{"code":9000,"message":"读取失败","data":null,"requestId":"..."}
    /// 仅返回安全显示信息及已核实的官方能力预设，不读取或返回密钥；预设不会自动保存。
    /// </remarks>
    /// <response code="200">提供商显示信息</response>
    [HttpGet("providers")]
    [ProducesResponseType<ApiResponse<IReadOnlyList<AiBatchProviderInfoDto>>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Providers(CancellationToken ct) => Ok(Wrap(await settings.GetProvidersAsync(ct)));
    /// <summary>保存批量参数</summary>
    /// <remarks>
    /// 请求：{"externalBatchSize":1,"localBatchSize":1,"maxWaitMilliseconds":50,"contextTokenBudget":8192,"maxOutputTokens":2048}
    /// 成功：{"code":0,"message":"ok","data":null,"requestId":"..."}
    /// 错误码：1000 参数超限；9000 服务错误。
    /// 错误：{"code":1000,"message":"AI 批处理资源参数超出允许范围","data":null,"requestId":"..."}
    /// 大于1是实验性显式启用；不保证更快或更准确，不更改本地模式及提供商启用状态。
    /// </remarks>
    /// <response code="200">保存成功</response>
    [HttpPut]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update([FromBody] AiBatchSettingsDto request, CancellationToken ct)
    {
        request.Validate();
        await settings.UpdateAsync(request, ct);
        return Ok(Wrap<object?>(null));
    }
}
