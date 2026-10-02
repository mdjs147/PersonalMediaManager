using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts.LocalAi;
using PersonalMediaManager.Application.Dtos.LocalAi;
using PersonalMediaManager.Application.Services.LocalAi;

namespace PersonalMediaManager.Host.Controllers.Settings;

/// <summary>本地模型设置与运行管理</summary>
[ApiController]
[Authorize(Roles = "Admin")]
[Route("settings/local-ai")]
public sealed class LocalAiController(ILocalAiSettingsService settings, ILocalAiRuntimeManager runtime) : ApiControllerBase
{
    /// <summary>读取本地模型设置</summary>
    /// <remarks>
    /// 成功：{"code":0,"message":"ok","data":{"mode":"Disabled","modelId":"qwen2.5-0.5b-instruct-q8_0"},"requestId":"..."}
    /// 错误码：1000 参数错误；9000 服务错误。
    /// 错误：{"code":9000,"message":"读取失败","data":null,"requestId":"..."}
    /// </remarks>
    /// <response code="200">当前设置</response>
    [HttpGet]
    [ProducesResponseType<ApiResponse<LocalAiSettingsDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken ct) => Ok(Wrap(await settings.GetAsync(ct)));

    /// <summary>保存本地模型设置</summary>
    /// <remarks>
    /// 请求：{"mode":"Disabled","modelId":"qwen2.5-0.5b-instruct-q8_0","runtimeExecutablePath":"","port":18081,"threads":2,"contextTokens":2048,"maxOutputTokens":512,"timeoutSeconds":30,"startupTimeoutSeconds":90,"memoryLimitMb":2048}
    /// 成功：{"code":0,"message":"ok","data":null,"requestId":"..."}
    /// 错误码：1000 配置超限或路径无效；9000 服务错误。
    /// 错误：{"code":1000,"message":"资源参数超出允许范围","data":null,"requestId":"..."}
    /// 保存前停止旧子进程；不会自动启动或下载模型。
    /// </remarks>
    /// <response code="200">保存成功</response>
    [HttpPost("update")]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update([FromBody] LocalAiSettingsDto request, CancellationToken ct)
    {
        request.Validate();
        await runtime.UpdateSettingsAsync(request, ct);
        return Ok(Wrap<object?>(null));
    }

    /// <summary>列出白名单模型</summary>
    /// <remarks>
    /// 成功：{"code":0,"message":"ok","data":[],"requestId":"..."}
    /// 错误码：9000 文件状态读取失败。
    /// 错误：{"code":9000,"message":"读取失败","data":null,"requestId":"..."}
    /// installed 仅表示本地文件大小匹配；启动时仍会重新校验 SHA256。
    /// </remarks>
    /// <response code="200">模型列表</response>
    [HttpGet("models")]
    [ProducesResponseType<ApiResponse<IReadOnlyList<LocalAiModelDto>>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Models(CancellationToken ct) => Ok(Wrap(await runtime.GetModelsAsync(ct)));

    /// <summary>读取运行和下载状态</summary>
    /// <remarks>
    /// 成功：{"code":0,"message":"ok","data":{"state":"Stopped","downloadState":"Idle"},"requestId":"..."}
    /// 错误码：9000 服务错误。
    /// 错误：{"code":9000,"message":"读取失败","data":null,"requestId":"..."}
    /// </remarks>
    /// <response code="200">运行状态</response>
    [HttpGet("status")]
    [ProducesResponseType<ApiResponse<LocalAiStatusDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Status(CancellationToken ct) => Ok(Wrap(await runtime.GetStatusAsync(ct)));

    /// <summary>开始下载固定模型</summary>
    /// <remarks>
    /// 请求：{"modelId":"qwen2.5-0.5b-instruct-q8_0"}
    /// 成功：{"code":0,"message":"ok","data":null,"requestId":"..."}
    /// 错误码：1000 非白名单、无固定制品或已有操作；9000 服务错误。
    /// 错误：{"code":1000,"message":"已有模型正在下载","data":null,"requestId":"..."}
    /// 成功仅表示开始下载；通过 status 读取校验结果。
    /// </remarks>
    /// <response code="200">下载已开始</response>
    [HttpPost("download")]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Download([FromBody] LocalAiDownloadRequest request, CancellationToken ct)
    {
        await runtime.DownloadAsync(request.ModelId, ct);
        return Ok(Wrap<object?>(null));
    }

    /// <summary>取消下载并清理临时文件</summary>
    /// <remarks>
    /// 请求：空请求体。
    /// 成功：{"code":0,"message":"ok","data":null,"requestId":"..."}
    /// 错误码：9000 清理失败。
    /// 错误：{"code":9000,"message":"清理失败","data":null,"requestId":"..."}
    /// </remarks>
    /// <response code="200">下载已取消</response>
    [HttpPost("download/cancel")]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> CancelDownload(CancellationToken ct)
    {
        await runtime.CancelDownloadAsync(ct);
        return Ok(Wrap<object?>(null));
    }

    /// <summary>启动并检查本地运行时</summary>
    /// <remarks>
    /// 请求：空请求体。
    /// 成功：{"code":0,"message":"ok","data":null,"requestId":"..."}
    /// 错误码：1000 未安装、模型校验失败、端口占用或启动超时；9000 服务错误。
    /// 错误：{"code":1000,"message":"本地端口已被占用","data":null,"requestId":"..."}
    /// 不改变模式；Disabled 时也可检查运行时，但不会参与媒体识别。
    /// </remarks>
    /// <response code="200">运行时健康</response>
    [HttpPost("start")]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Start(CancellationToken ct)
    {
        await runtime.StartAsync(ct);
        return Ok(Wrap<object?>(null));
    }

    /// <summary>停止自行启动的子进程</summary>
    /// <remarks>
    /// 请求：空请求体。
    /// 成功：{"code":0,"message":"ok","data":null,"requestId":"..."}
    /// 错误码：1000 子进程尚未退出；9000 服务错误。
    /// 错误：{"code":1000,"message":"请重试停止","data":null,"requestId":"..."}
    /// 不终止其他程序启动的服务。
    /// </remarks>
    /// <response code="200">已停止</response>
    [HttpPost("stop")]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Stop(CancellationToken ct)
    {
        await runtime.StopAsync(ct);
        return Ok(Wrap<object?>(null));
    }
}
