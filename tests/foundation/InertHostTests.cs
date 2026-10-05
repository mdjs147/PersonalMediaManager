using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PersonalMediaManager.Web;
using Xunit;

// Host configuration tests temporarily alter this process's environment only.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace PersonalMediaManager.Foundation.Tests;

public sealed class InertHostTests
{
    [Fact]
    public async Task CompleteEndpointSetContainsOnlyReadOnlyShellAndHealth()
    {
        await using WebApplication app = FoundationHost.Build([]);
        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints).ToArray();
        Assert.Equal(2, endpoints.Length);
        var routes = endpoints.Select(endpoint => Assert.IsType<RouteEndpoint>(endpoint)).ToArray();
        // MVC represents an absolute root route as the empty template.
        Assert.Equal(new[] { "/", "/health" }, routes.Select(route =>
            route.RoutePattern.RawText == "" ? "/" : route.RoutePattern.RawText).Order());
        foreach (var endpoint in endpoints)
        {
            var metadata = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>();
            Assert.NotNull(metadata);
            Assert.Equal(new[] { "GET", "HEAD" }, metadata.HttpMethods.Order());
        }
    }

    [Fact]
    public async Task HostDoesNotInstallBusinessWorkersOrNetworkClients()
    {
        await using WebApplication app = FoundationHost.Build([]);
        var hostedServices = app.Services.GetServices<IHostedService>().ToArray();
        var webHost = Assert.Single(hostedServices);
        Assert.Equal("Microsoft.AspNetCore.Hosting.GenericWebHostService", webHost.GetType().FullName);
        Assert.Equal("Microsoft.AspNetCore.Hosting", webHost.GetType().Assembly.GetName().Name);
        Assert.Null(app.Services.GetService<IHttpClientFactory>());
    }

    [Fact]
    public async Task DataProtectionCannotCreateOrConsumeKeys()
    {
        await using WebApplication app = FoundationHost.Build([]);
        Assert.Null(app.Services.GetService<IKeyManager>());
        Assert.DoesNotContain(app.Services.GetServices<IHostedService>(),
            service => service.GetType().FullName?.Contains("DataProtection", StringComparison.Ordinal) == true);
        var protector = app.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("foundation-tests");
        Assert.Throws<NotSupportedException>(() => protector.Protect([1, 2, 3]));
        Assert.Throws<NotSupportedException>(() => protector.Unprotect([1, 2, 3]));
    }

    [Fact]
    public async Task EnvironmentCannotEnableDevelopmentOrOverrideBinding()
    {
        var replacements = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["DOTNET_ENVIRONMENT"] = "Development",
            ["ASPNETCORE_URLS"] = "http://0.0.0.0:17432",
            ["ASPNETCORE_HTTP_PORTS"] = "17433",
            ["ASPNETCORE_HTTPS_PORTS"] = "17434",
            ["Kestrel__Endpoints__Injected__Url"] = "http://0.0.0.0:17435",
        };
        var original = replacements.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var (name, value) in replacements)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            await using WebApplication app = FoundationHost.Build([]);
            Assert.Equal(Environments.Production, app.Environment.EnvironmentName);
            Assert.DoesNotContain(app.Configuration.AsEnumerable(),
                item => item.Value is not null && replacements.Values.Contains(item.Value));
        }
        finally
        {
            foreach (var (name, value) in original)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}
