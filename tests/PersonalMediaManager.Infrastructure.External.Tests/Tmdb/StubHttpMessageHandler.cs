using System.Net;
using System.Net.Http.Headers;

namespace PersonalMediaManager.Infrastructure.External.Tests.Tmdb;

/// <summary>测试用 HttpMessageHandler：按队列依次返回预定义响应；可观察调用次数与每次的请求 URL</summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responders = new();

    public List<HttpRequestMessage> Requests { get; } = new();

    public StubHttpMessageHandler EnqueueResponse(HttpStatusCode status, string body, Action<HttpResponseHeaders>? configure = null)
    {
        _responders.Enqueue(_ =>
        {
            HttpResponseMessage resp = new(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
            configure?.Invoke(resp.Headers);
            return resp;
        });
        return this;
    }

    public StubHttpMessageHandler EnqueueResponse(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responders.Enqueue(responder);
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (_responders.Count == 0)
            throw new InvalidOperationException($"未预设响应：{request.Method} {request.RequestUri}");
        Func<HttpRequestMessage, HttpResponseMessage> next = _responders.Dequeue();
        return Task.FromResult(next(request));
    }
}

/// <summary>把 StubHttpMessageHandler 包成 IHttpClientFactory 供 TmdbClient 注入</summary>
internal sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly StubHttpMessageHandler _handler;

    public StubHttpClientFactory(StubHttpMessageHandler handler) => _handler = handler;

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}
