using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Host.Hubs;
using PersonalMediaManager.Host.Middleware;

namespace PersonalMediaManager.Host.Tests.Hubs;

/// <summary>D8.2/D8.3 TaskHub + ITaskNotifier — 客户端订阅 + SignalRTaskNotifier 推送</summary>
/// <remarks>
/// r3 第一波撤销 r2 误判：Hub 改回 [AllowAnonymous]（需求文档 §3.12 明确「SignalR Hub」属匿名访问范围）；
/// 测试仍签发 admin JWT 模拟登录态客户端连接（前端 axios 拦截器把 token 注入 accessTokenProvider 仍可生效），
/// 旨在覆盖「携带 token 时连接同样通过」这条管线；匿名连接路径由 Anonymous_Connect_To_TasksHub_Succeeds 单测覆盖。
/// </remarks>
public sealed class TaskHubTests : IDisposable
{
    private readonly PmmHostFactory _factory;
    private readonly HttpClient _client;

    public TaskHubTests()
    {
        SetupGuardMiddleware.ResetCacheForTest();
        _factory = new PmmHostFactory();
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        SetupGuardMiddleware.ResetCacheForTest();
    }

    [Fact]
    public async Task Connect_To_TasksHub_Returns_Connected_State()
    {
        string token = await IssueAdminTokenAsync();
        await using HubConnection conn = BuildConnection(token);
        await conn.StartAsync();
        conn.State.Should().Be(HubConnectionState.Connected);
    }

    /// <summary>r3 第一波：Hub 已恢复 [AllowAnonymous]，无 JWT 客户端也能连接</summary>
    [Fact]
    public async Task Anonymous_Connect_To_TasksHub_Succeeds()
    {
        await _client.PostAsJsonAsync("/api/setup/admin", new { username = "admin", password = "secret123" });
        await _client.PostAsJsonAsync("/api/setup/complete", new { });
        await using HubConnection conn = BuildConnection(accessToken: null);
        await conn.StartAsync();
        conn.State.Should().Be(HubConnectionState.Connected);
    }

    [Fact]
    public async Task TaskNotifier_Broadcasts_Status_Change_To_Subscribers()
    {
        string token = await IssueAdminTokenAsync();
        await using HubConnection conn = BuildConnection(token);
        TaskCompletionSource<TaskStatusChangedEvent> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.On<TaskStatusChangedEvent>(SignalRTaskNotifier.StatusChangedMethod, evt => tcs.TrySetResult(evt));
        await conn.StartAsync();

        ITaskNotifier notifier = _factory.Services.GetRequiredService<ITaskNotifier>();
        TaskStatusChangedEvent payload = new(
            MediaItemId: 42, FileName: "x.mkv",
            OldStatus: MediaItemStatus.Queued, NewStatus: MediaItemStatus.Parsing,
            OccurredAt: DateTimeOffset.UtcNow);
        await notifier.NotifyStatusChangedAsync(payload);

        Task winner = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        winner.Should().BeSameAs(tcs.Task, "客户端必须在 3 秒内收到推送");
        TaskStatusChangedEvent received = await tcs.Task;
        received.MediaItemId.Should().Be(42);
        received.NewStatus.Should().Be(MediaItemStatus.Parsing);
    }

    [Fact]
    public async Task TaskNotifier_Broadcasts_QueueChanged()
    {
        string token = await IssueAdminTokenAsync();
        await using HubConnection conn = BuildConnection(token);
        TaskCompletionSource<QueueChangedEvent> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.On<QueueChangedEvent>(SignalRTaskNotifier.QueueChangedMethod, evt => tcs.TrySetResult(evt));
        await conn.StartAsync();

        ITaskNotifier notifier = _factory.Services.GetRequiredService<ITaskNotifier>();
        await notifier.NotifyQueueChangedAsync(new QueueChangedEvent(Pending: 5, Running: 1, AwaitingReview: 2, DateTimeOffset.UtcNow));

        Task winner = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        winner.Should().BeSameAs(tcs.Task);
        QueueChangedEvent received = await tcs.Task;
        received.Pending.Should().Be(5);
        received.Running.Should().Be(1);
        received.AwaitingReview.Should().Be(2);
    }

    [Fact]
    public void ITaskNotifier_DefaultResolves_To_SignalRTaskNotifier_NotNullObject()
    {
        ITaskNotifier notifier = _factory.Services.GetRequiredService<ITaskNotifier>();
        notifier.Should().BeOfType<SignalRTaskNotifier>("Host PmmHost 用 Replace 覆盖了 Application 的 NullTaskNotifier");
    }

    /// <summary>完成 Setup → 用 admin 登录拿 JWT；供 Hub 鉴权使用</summary>
    private async Task<string> IssueAdminTokenAsync()
    {
        await _client.PostAsJsonAsync("/api/setup/admin", new { username = "admin", password = "secret123" });
        await _client.PostAsJsonAsync("/api/setup/complete", new { });
        HttpResponseMessage loginResp = await _client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "secret123" });
        loginResp.EnsureSuccessStatusCode();
        string body = await loginResp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("data").GetProperty("token").GetString()!;
    }

    private HubConnection BuildConnection(string? accessToken)
    {
        return new HubConnectionBuilder()
            .WithUrl(_factory.Server.BaseAddress + TaskHub.Path.TrimStart('/'), opts =>
            {
                opts.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                if (accessToken is not null)
                {
                    opts.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
                }
            })
            .AddJsonProtocol(opts =>
            {
                // 服务端 PmmHost AddSignalR 注册了 JsonStringEnumConverter；客户端必须对齐才能反序列化 MediaItemStatus
                opts.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            })
            .Build();
    }
}
