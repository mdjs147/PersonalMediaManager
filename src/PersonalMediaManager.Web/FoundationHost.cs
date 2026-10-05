using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using PersonalMediaManager.Foundation;

namespace PersonalMediaManager.Web;

/// <summary>Builds, but does not start, the explicitly inert foundation host.</summary>
public static class FoundationHost
{
    public static WebApplication Build(string[] args)
    {
        var options = FoundationOptions.Parse(args);
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(FoundationHost).Assembly.GetName().Name,
            EnvironmentName = Environments.Production,
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.Configuration.Sources.Clear();
        builder.Host.UseConsoleLifetime();
        builder.Logging.AddSimpleConsole(settings => settings.SingleLine = true);
        builder.WebHost.UseKestrelCore().ConfigureKestrel(server =>
        {
            server.AddServerHeader = false;
            server.Listen(IPAddress.Loopback, options.Port, listener => listener.Protocols = HttpProtocols.Http1);
        });
        builder.Services.AddMvcCore().AddViews().AddRazorViewEngine()
            .AddApplicationPart(typeof(FoundationHost).Assembly);

        // MVC's view support registers Data Protection and its key-prewarming
        // hosted service. This inert shell must never create or read a key ring.
        for (var index = builder.Services.Count - 1; index >= 0; index--)
        {
            var descriptor = builder.Services[index];
            var implementationType = descriptor.IsKeyedService
                ? descriptor.KeyedImplementationType
                : descriptor.ImplementationType;
            if (IsDataProtectionType(descriptor.ServiceType) || IsDataProtectionType(implementationType))
            {
                builder.Services.RemoveAt(index);
            }
        }
        builder.Services.AddSingleton<IDataProtectionProvider, DisabledDataProtectionProvider>();

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.ContentSecurityPolicy =
                "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'";
            await next(context);
        });
        app.MapControllers();
        app.MapMethods("/health", ["GET", "HEAD"], () => Results.Json(new
        {
            status = "ok",
            mode = "foundation",
            businessOperationsEnabled = false
        }));
        return app;
    }

    private static bool IsDataProtectionType(Type? type) =>
        type?.Namespace?.StartsWith("Microsoft.AspNetCore.DataProtection", StringComparison.Ordinal) is true;

    private sealed class DisabledDataProtectionProvider : IDataProtectionProvider, IDataProtector
    {
        public IDataProtector CreateProtector(string purpose)
        {
            ArgumentNullException.ThrowIfNull(purpose);
            return this;
        }

        public byte[] Protect(byte[] plaintext) =>
            throw new NotSupportedException("Data protection is disabled in the inert foundation.");

        public byte[] Unprotect(byte[] protectedData) =>
            throw new NotSupportedException("Data protection is disabled in the inert foundation.");
    }
}
