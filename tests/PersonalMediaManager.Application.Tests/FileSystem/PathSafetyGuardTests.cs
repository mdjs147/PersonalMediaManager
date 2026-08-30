using PersonalMediaManager.Application.Common.Archiving;

namespace PersonalMediaManager.Application.Tests.FileSystem;

/// <summary>PathSafetyGuard（D4.5）— 归档前防路径穿越</summary>
public sealed class PathSafetyGuardTests
{
    private static string Root => OperatingSystem.IsWindows() ? @"C:\Media\Movies" : "/Media/Movies";

    [Fact]
    public void EnsureWithinRoot_LegitNested_Passes()
    {
        string target = Path.Combine(Root, "盗梦空间 (2010)", "盗梦空间 (2010).mkv");
        Action a = () => PathSafetyGuard.EnsureWithinRoot(Root, target);
        a.Should().NotThrow();
    }

    [Fact]
    public void EnsureWithinRoot_DotDotTraversal_Throws()
    {
        string target = Path.Combine(Root, "..", "Evil", "x.mkv");
        Action a = () => PathSafetyGuard.EnsureWithinRoot(Root, target);
        a.Should().Throw<PathTraversalException>();
    }

    [Fact]
    public void EnsureWithinRoot_ExactRootMatch_Throws()
    {
        Action a = () => PathSafetyGuard.EnsureWithinRoot(Root, Root);
        a.Should().Throw<PathTraversalException>("根本身不能作为归档目标（必须在子目录）");
    }

    [Fact]
    public void EnsureWithinRoot_SiblingDirWithSamePrefix_Throws()
    {
        // /Media/Movies vs /Media/Movies-evil/x — prefix 字符串相同但路径不同
        string evil = OperatingSystem.IsWindows()
            ? @"C:\Media\Movies-evil\x.mkv"
            : "/Media/Movies-evil/x.mkv";
        Action a = () => PathSafetyGuard.EnsureWithinRoot(Root, evil);
        a.Should().Throw<PathTraversalException>("加尾分隔符避免前缀骗过 StartsWith");
    }

    [Fact]
    public void EnsureWithinRoot_RedundantSeparators_StillValid()
    {
        string target = OperatingSystem.IsWindows()
            ? @"C:\Media\\Movies\\foo\\bar.mkv"
            : "/Media//Movies//foo//bar.mkv";
        Action a = () => PathSafetyGuard.EnsureWithinRoot(Root, target);
        a.Should().NotThrow("Path.GetFullPath 折叠多斜杠");
    }

    [Fact]
    public void IsWithinRoot_NonThrowingProbe()
    {
        PathSafetyGuard.IsWithinRoot(Root, Path.Combine(Root, "a.mkv")).Should().BeTrue();
        PathSafetyGuard.IsWithinRoot(Root, Path.Combine(Root, "..", "evil.mkv")).Should().BeFalse();
    }

    [Fact]
    public void EnsureWithinRoot_EmptyArgs_ThrowsArgumentException()
    {
        Action a = () => PathSafetyGuard.EnsureWithinRoot("", "x");
        Action b = () => PathSafetyGuard.EnsureWithinRoot(Root, "");
        a.Should().Throw<ArgumentException>();
        b.Should().Throw<ArgumentException>();
    }
}
