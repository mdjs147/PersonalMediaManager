using System.Globalization;

namespace PersonalMediaManager.Foundation;

/// <summary>Only an IPv4 loopback port is configurable in the inert foundation.</summary>
public sealed record FoundationOptions(int Port)
{
    public static FoundationOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0)
        {
            return new FoundationOptions(0);
        }

        if (args.Length != 2 || args[0] != "--port" || string.IsNullOrEmpty(args[1]) ||
            args[1].Any(character => character is < '0' or > '9') ||
            !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port > 65535)
        {
            throw new ArgumentException("Use no arguments or exactly --port followed by an integer from 0 through 65535.", nameof(args));
        }

        return new FoundationOptions(port);
    }
}
