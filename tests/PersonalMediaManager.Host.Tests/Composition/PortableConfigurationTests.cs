using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Host.Composition;

namespace PersonalMediaManager.Host.Tests.Composition;

public sealed class PortableConfigurationTests
{
    [Fact]
    public async Task CommandLine_OverridesLocalConfiguration()
    {
        string root = Path.Combine(Path.GetTempPath(), "pmm-config-" + Guid.NewGuid().ToString("N"));
        try
        {
            AppPaths paths = AppPaths.ForRoot(root);
            await File.WriteAllTextAsync(Path.Combine(root, "local.json"), "{\"Web\":{\"Port\":12340}}");
            await using WebApplication app = PmmHost.CreateApp(["--Web:Port", "12341"], paths,
                webHostOverride: builder => builder.UseTestServer(), portableServer: true);
            app.Configuration["Web:Port"].Should().Be("12341");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void PortableServer_RejectsExternalDatabaseOverride()
    {
        string root = Path.Combine(Path.GetTempPath(), "pmm-config-" + Guid.NewGuid().ToString("N"));
        try
        {
            AppPaths paths = AppPaths.ForRoot(root);
            File.WriteAllText(Path.Combine(root, "local.json"), "{\"ConnectionStrings\":{\"Default\":\"Data Source=other.db\"}}");
            Action action = () => PmmHost.CreateApp([], paths, portableServer: true);
            action.Should().Throw<InvalidOperationException>().WithMessage("*--data-dir*");
        }
        finally { Directory.Delete(root, true); }
    }
}
