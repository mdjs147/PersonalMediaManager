using PersonalMediaManager.Server;

namespace PersonalMediaManager.Host.Tests.Composition;

public sealed class ServerOptionsTests
{
    [Fact]
    public void ExplicitArguments_MapToConfiguration()
    {
        ServerOptions options = ServerOptions.Parse(["--headless", "--port", "12345", "--bind", "127.0.0.1", "--data-dir", Path.GetTempPath()]);
        options.Headless.Should().BeTrue();
        options.ConfigurationArgs.Should().Equal("--Web:Port", "12345", "--Web:BindAddress", "127.0.0.1");
    }

    [Theory]
    [InlineData("--unknown")]
    [InlineData("--port")]
    public void InvalidArguments_Fail(string value)
    {
        Action action = () => ServerOptions.Parse([value]);
        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void RelativeDataDirectory_Fails()
    {
        Action action = () => ServerOptions.Parse(["--data-dir", "relative"]);
        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DataRootLease_ExcludesDuplicateAndReleases()
    {
        string root = Path.Combine(Path.GetTempPath(), "pmm-lease-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (DataRootLease lease = new DataRootLease(root))
            {
                Action action = () => new DataRootLease(root).Dispose();
                action.Should().Throw<IOException>();
            }
            using DataRootLease reopened = new DataRootLease(root);
        }
        finally { Directory.Delete(root, true); }
    }
}
