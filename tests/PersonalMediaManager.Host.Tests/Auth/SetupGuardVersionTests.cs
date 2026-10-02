using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using NSubstitute;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Services.Setup;
using PersonalMediaManager.Host.Middleware;

namespace PersonalMediaManager.Host.Tests.Auth;

/// <summary>初始化守卫仅放行静态版本 GET 的精确路径</summary>
public sealed class SetupGuardVersionTests : IDisposable
{
    public SetupGuardVersionTests() => SetupGuardMiddleware.ResetCacheForTest();

    public void Dispose() => SetupGuardMiddleware.ResetCacheForTest();

    [Fact(DisplayName = "静态版本在初始化前直接放行，不读取数据库完成状态")]
    public async Task StaticVersionGet_DoesNotReadSetupState()
    {
        ISetupService setup = Substitute.For<ISetupService>();
        DefaultHttpContext context = CreateControllerRequest("GET", "/api/system/version");
        bool reachedEndpoint = false;
        SetupGuardMiddleware middleware = new(_ =>
        {
            reachedEndpoint = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, setup);

        reachedEndpoint.Should().BeTrue();
        await setup.DidNotReceive().IsCompletedAsync(Arg.Any<CancellationToken>());
    }

    [Theory(DisplayName = "版本放行不覆盖写方法、子路径或其他系统端点")]
    [InlineData("POST", "/api/system/version")]
    [InlineData("GET", "/api/system/version/internal")]
    [InlineData("GET", "/api/system/info")]
    public async Task OtherControllerRequests_StillRequireSetup(string method, string path)
    {
        ISetupService setup = Substitute.For<ISetupService>();
        setup.IsCompletedAsync(Arg.Any<CancellationToken>()).Returns(false);
        DefaultHttpContext context = CreateControllerRequest(method, path);
        using MemoryStream body = new();
        context.Response.Body = body;
        bool reachedEndpoint = false;
        SetupGuardMiddleware middleware = new(_ =>
        {
            reachedEndpoint = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, setup);

        reachedEndpoint.Should().BeFalse();
        await setup.Received(1).IsCompletedAsync(Arg.Any<CancellationToken>());
        body.Position = 0;
        using JsonDocument response = await JsonDocument.ParseAsync(body);
        response.RootElement.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        response.RootElement.GetProperty("message").GetString().Should().Be("请先完成初始化");
    }

    private static DefaultHttpContext CreateControllerRequest(string method, string path)
    {
        DefaultHttpContext context = new();
        context.Request.Method = method;
        context.Request.Path = path;
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new ControllerActionDescriptor()), "初始化守卫版本路径测试"));
        return context;
    }
}
