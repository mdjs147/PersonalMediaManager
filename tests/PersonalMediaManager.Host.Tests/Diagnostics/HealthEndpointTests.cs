using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PersonalMediaManager.Host.Tests.Diagnostics;

/// <summary>GET /health：200 + JSON {code:0, data:{status:"ok"}}</summary>
public sealed class HealthEndpointTests : IClassFixture<PmmHostFactory>
{
    private readonly PmmHostFactory _factory;

    public HealthEndpointTests(PmmHostFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Get_Health_Returns200WithCode0()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetInt32().Should().Be(0);
        body.GetProperty("message").GetString().Should().Be("ok");
        body.GetProperty("data").GetProperty("status").GetString().Should().Be("ok");
        body.TryGetProperty("requestId", out JsonElement _).Should().BeTrue();
    }
}
