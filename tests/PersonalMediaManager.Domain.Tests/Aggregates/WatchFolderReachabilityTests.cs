using PersonalMediaManager.Domain.Aggregates.WatchDirectories;

namespace PersonalMediaManager.Domain.Tests.Aggregates;

/// <summary>WatchFolder MarkReachable/Unreachable：状态扭转才发事件，幂等不重发</summary>
public sealed class WatchFolderReachabilityTests
{
    private static WatchFolder New(bool isNetworkShare = true) => new()
    {
        Path = "//nas/share",
        IsNetworkShare = isNetworkShare,
        Enabled = true,
    };

    [Fact]
    public void FirstMarkReachable_EmitsEvent()
    {
        WatchFolder f = New();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        f.MarkReachable(now);
        WatchFolderReachabilityChangedEvent[] events = f.DomainEvents.OfType<WatchFolderReachabilityChangedEvent>().ToArray();
        events.Should().HaveCount(1);
        events[0].IsReachable.Should().BeTrue();
        events[0].Path.Should().Be("//nas/share");
        f.LastReachableAt.Should().Be(now);
    }

    [Fact]
    public void RepeatedMarkReachable_Within2Min_DoesNotEmitDuplicateEvent()
    {
        WatchFolder f = New();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;
        f.MarkReachable(t0);
        f.ClearDomainEvents();

        f.MarkReachable(t0.AddSeconds(30));
        f.DomainEvents.Should().BeEmpty();
        f.LastReachableAt.Should().Be(t0.AddSeconds(30));
    }

    [Fact]
    public void ReachableAfterLongGap_EmitsEventAgain()
    {
        WatchFolder f = New();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;
        f.MarkReachable(t0);
        f.ClearDomainEvents();

        // 间隔 5 分钟 → 视为之前不可达 → 再次可达应发事件
        f.MarkReachable(t0.AddMinutes(5));
        f.DomainEvents.OfType<WatchFolderReachabilityChangedEvent>().Should().HaveCount(1);
    }

    [Fact]
    public void MarkUnreachable_FromReachable_EmitsEventAndClearsTimestamp()
    {
        WatchFolder f = New();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;
        f.MarkReachable(t0);
        f.ClearDomainEvents();

        f.MarkUnreachable(t0.AddSeconds(10));
        f.DomainEvents.OfType<WatchFolderReachabilityChangedEvent>()
            .Should().ContainSingle(e => e.IsReachable == false);
        f.LastReachableAt.Should().BeNull();
    }

    [Fact]
    public void MarkUnreachable_WhenAlreadyUnreachable_DoesNotEmitEvent()
    {
        WatchFolder f = New();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;
        f.MarkUnreachable(t0);
        f.DomainEvents.Should().BeEmpty();
    }
}
