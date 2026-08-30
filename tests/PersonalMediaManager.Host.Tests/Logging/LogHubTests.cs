using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Common;

namespace PersonalMediaManager.Host.Tests.Logging;

/// <summary>Serilog 装配 + LogHub 路由：验证日志文件落地与 hub 端点存在</summary>
/// <remarks>
/// SignalR 客户端端到端推送测试在 TestServer 下走 LongPolling 协议握手存在已知问题；
/// 真实端到端推送由前端 E3.7 日志页联调时验证（手动 + Playwright）。本测试仅守护配置正确性。
/// </remarks>
public sealed class LogHubTests : IClassFixture<PmmHostFactory>
{
    private readonly PmmHostFactory _factory;

    public LogHubTests(PmmHostFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task LogHub_Endpoint_IsReachable()
    {
        HttpClient client = _factory.CreateClient();
        HttpResponseMessage resp = await client.GetAsync("/hubs/logs");
        // SignalR negotiate 端点对 GET 返 405 (Method Not Allowed) 或 200（视协议）；总之不应是 404
        resp.StatusCode.Should().NotBe(HttpStatusCode.NotFound, "LogHub 应已映射到 /hubs/logs");
    }

    [Fact]
    public void Serilog_Writing_Information_Does_Not_Throw()
    {
        ILoggerFactory loggerFactory = _factory.Services.GetRequiredService<ILoggerFactory>();
        ILogger logger = loggerFactory.CreateLogger("LogHubSmokeTest");
        Action act = () => logger.LogInformation("smoke-{Id}", Guid.NewGuid());
        act.Should().NotThrow("Serilog 三 sink（Console + File + SignalRSink）任一失败都不应抛回业务调用方");
    }

    [Fact]
    public void AppPaths_LogDir_Exists_AfterHostBuild()
    {
        AppPaths paths = _factory.Services.GetRequiredService<AppPaths>();
        Directory.Exists(paths.LogDir).Should().BeTrue("Serilog File sink 写入前 AppPaths.Resolve 已 mkdir LogDir");
    }
}
