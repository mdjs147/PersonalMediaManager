using PersonalMediaManager.Application.Common;

namespace PersonalMediaManager.Application.Tests.Common;

public sealed class MountedVolumeResolverTests
{
    private static string Full(string value) => Path.GetFullPath(value);

    [Fact]
    public void NestedMount_UsesLongestBoundaryMatch()
    {
        string[] mounts = [Full("/"), Full("/mnt"), Full("/mnt/media"), Full("/mnt/media/archive")];
        MountedVolumeResolver.TryResolveUnix(Full("/mnt/media/archive/movie/file.mkv"), () => mounts)
            .Should().Be(Full("/mnt/media/archive"));
    }

    [Fact]
    public void SharedPrefix_DoesNotMatchSibling()
    {
        string[] mounts = [Full("/"), Full("/mnt/A")];
        MountedVolumeResolver.TryResolveUnix(Full("/mnt/AB/movie.mkv"), () => mounts).Should().Be(Full("/"));
    }

    [Fact]
    public void RootItself_AndTrailingSeparator_Match()
    {
        string mount = Full("/mnt/media");
        MountedVolumeResolver.TryResolveUnix(mount + Path.DirectorySeparatorChar, () => [mount])
            .Should().Be(mount);
    }

    [Fact]
    public void PathCasing_UsesPlatformIdentity()
    {
        string[] mounts = [Full("/"), Full("/mnt/A")];
        MountedVolumeResolver.TryResolveUnix(Full("/mnt/a/movie.mkv"), () => mounts)
            .Should().Be(OperatingSystem.IsWindows() ? Full("/mnt/A") : Full("/"));
    }

    [Fact]
    public void UnavailableEnumeration_DoesNotPretendSystemDrive()
    {
        MountedVolumeResolver.TryResolveUnix(Full("/mnt/media/file.mkv"), () => throw new IOException("不可用"))
            .Should().BeNull();
        MountedVolumeResolver.TryResolveUnix(Full("/mnt/media/file.mkv"), () => []).Should().BeNull();
    }

    [Fact]
    public void UnreadyVolume_RemainsSelectedForCallerToSkip()
    {
        // 解析器不探测容量/IsReady；卷离线时调用方应跳过，不能错查父挂载点容量。
        string[] mounts = [Full("/"), Full("/mnt/offline")];
        MountedVolumeResolver.TryResolveUnix(Full("/mnt/offline/file.mkv"), () => mounts)
            .Should().Be(Full("/mnt/offline"));
    }
}
