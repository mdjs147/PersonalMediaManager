using PersonalMediaManager.Foundation;
using Xunit;

namespace PersonalMediaManager.Foundation.Tests;

public sealed class FoundationOptionsTests
{
    [Fact]
    public void NoArgumentsSelectsAnEphemeralPort()
    {
        Assert.Equal(0, FoundationOptions.Parse([]).Port);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("65535", 65535)]
    [InlineData("00123", 123)]
    public void OnlyAnExplicitNumericPortIsAccepted(string text, int expected)
    {
        Assert.Equal(expected, FoundationOptions.Parse(["--port", text]).Port);
    }

    public static TheoryData<string[]> InvalidArguments => new()
    {
        new[] { "--port" },
        new[] { "--port", "" },
        new[] { "--port", "-1" },
        new[] { "--port", "+1" },
        new[] { "--port", " 1" },
        new[] { "--port", "1 " },
        new[] { "--port", "65536" },
        new[] { "--port", "99999999999999999999" },
        new[] { "--port", "1.5" },
        new[] { "--port", "١" },
        new[] { "--port", "1", "--port", "2" },
        new[] { "--urls", "http://0.0.0.0:12345" },
        new[] { "--environment", "Development" },
        new[] { "--data-path", "/unapproved-data" },
        new[] { "--port=1" },
        new[] { "--PORT", "1" },
    };

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public void AmbiguousOrUnsupportedConfigurationIsRejected(string[] arguments)
    {
        Assert.ThrowsAny<ArgumentException>(() => FoundationOptions.Parse(arguments));
    }
}
