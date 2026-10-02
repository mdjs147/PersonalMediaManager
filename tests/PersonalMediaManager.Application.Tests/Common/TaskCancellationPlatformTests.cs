using PersonalMediaManager.Application.Common;

namespace PersonalMediaManager.Application.Tests.Common;

public sealed class TaskCancellationPlatformTests
{
    [Fact]
    public void Cancellation_UsesPlatformPathIdentity()
    {
        TaskCancellationManager manager = new();
        string upper = Path.Combine(Path.GetTempPath(), "Pmm-A.mkv");
        string lower = Path.Combine(Path.GetTempPath(), "pmm-a.mkv");
        using TaskCancellationRegistration active = manager.Register(upper, CancellationToken.None);
        manager.RequestCancellation(lower);
        active.IsCancellationRequested.Should().Be(OperatingSystem.IsWindows());
        manager.RequestCancellation(upper);
        active.IsCancellationRequested.Should().BeTrue();
    }
}
