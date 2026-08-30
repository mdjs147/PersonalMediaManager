using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalMediaManager.Infrastructure.Platform.FileSystem;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>EmptyDirectoryCleaner — 归档后源端空目录回收（严格 / 宽松 / 多级上溯 / 边界守护）</summary>
/// <remarks>用真实临时目录跑端到端：验证「可视为空」判定、连残留删除、监控根绝不被删。</remarks>
public sealed class EmptyDirectoryCleanerTests : IDisposable
{
    private readonly EmptyDirectoryCleaner _sut = new(NullLogger<EmptyDirectoryCleaner>.Instance);
    private readonly string _root;

    public EmptyDirectoryCleanerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"pmm-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 临时目录清理失败无碍 */ }
    }

    private static IReadOnlySet<string> Ignore(params string[] exts)
        => new HashSet<string>(exts, StringComparer.OrdinalIgnoreCase);

    /// <summary>在 _root 下逐段创建目录并返回绝对路径</summary>
    private string Dir(params string[] parts)
    {
        string p = _root;
        foreach (string part in parts) p = Path.Combine(p, part);
        Directory.CreateDirectory(p);
        return p;
    }

    private static void Touch(string dir, string name)
        => File.WriteAllText(Path.Combine(dir, name), "x");

    /// <summary>与 cleaner 内部同口径的路径规范化（GetFullPath + 去尾分隔符）</summary>
    private static string Norm(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    [Fact(DisplayName = "严格模式：真正的空目录被回收")]
    public void Strict_EmptyDir_Removed()
    {
        string watch = Dir("watch");
        string sub = Dir("watch", "MovieA");

        IReadOnlyList<string> deleted = _sut.CleanUpward(sub, watch, Ignore());

        deleted.Should().ContainSingle().Which.Should().Be(Norm(sub));
        Directory.Exists(sub).Should().BeFalse();
        Directory.Exists(watch).Should().BeTrue("监控根绝不被删");
    }

    [Fact(DisplayName = "严格模式：仅剩 .torrent 不算空，不删")]
    public void Strict_TorrentOnly_NotRemoved()
    {
        string watch = Dir("watch");
        string sub = Dir("watch", "MovieA");
        Touch(sub, "MovieA.torrent");

        IReadOnlyList<string> deleted = _sut.CleanUpward(sub, watch, Ignore());

        deleted.Should().BeEmpty();
        Directory.Exists(sub).Should().BeTrue();
    }

    [Fact(DisplayName = "宽松模式：仅剩 .torrent 视为空，连残留一并删")]
    public void Relaxed_TorrentOnly_Removed()
    {
        string watch = Dir("watch");
        string sub = Dir("watch", "MovieA");
        Touch(sub, "MovieA.torrent");

        IReadOnlyList<string> deleted = _sut.CleanUpward(sub, watch, Ignore(".torrent"));

        deleted.Should().ContainSingle().Which.Should().Be(Norm(sub));
        Directory.Exists(sub).Should().BeFalse();
    }

    [Fact(DisplayName = "宽松模式扩展名大小写不敏感（.TORRENT 命中 .torrent 清单）")]
    public void Relaxed_ExtensionCaseInsensitive()
    {
        string watch = Dir("watch");
        string sub = Dir("watch", "MovieA");
        Touch(sub, "MovieA.TORRENT");

        IReadOnlyList<string> deleted = _sut.CleanUpward(sub, watch, Ignore(".torrent"));

        deleted.Should().ContainSingle();
        Directory.Exists(sub).Should().BeFalse();
    }

    [Fact(DisplayName = "宽松模式：.torrent + 递归空子目录，删整棵")]
    public void Relaxed_TorrentPlusEmptySubdir_Removed()
    {
        string watch = Dir("watch");
        string sub = Dir("watch", "MovieA");
        Dir("watch", "MovieA", "Sample"); // 空子目录
        Touch(sub, "MovieA.torrent");

        IReadOnlyList<string> deleted = _sut.CleanUpward(sub, watch, Ignore(".torrent"));

        deleted.Should().Contain(Norm(sub));
        Directory.Exists(sub).Should().BeFalse();
    }

    [Fact(DisplayName = "子目录含真实文件 → 父不算空，不删")]
    public void Relaxed_SubdirHasRealFile_NotRemoved()
    {
        string watch = Dir("watch");
        string sub = Dir("watch", "MovieA");
        string sample = Dir("watch", "MovieA", "Sample");
        Touch(sub, "MovieA.torrent");
        Touch(sample, "sample.mkv"); // 子目录里有真实内容

        IReadOnlyList<string> deleted = _sut.CleanUpward(sub, watch, Ignore(".torrent"));

        deleted.Should().BeEmpty();
        Directory.Exists(sub).Should().BeTrue();
    }

    [Fact(DisplayName = "含真实文件：不删并停止上溯")]
    public void RealContent_NotRemoved()
    {
        string watch = Dir("watch");
        string sub = Dir("watch", "MovieA");
        Touch(sub, "MovieA.mkv");
        Touch(sub, "MovieA.torrent");

        IReadOnlyList<string> deleted = _sut.CleanUpward(sub, watch, Ignore(".torrent"));

        deleted.Should().BeEmpty();
        Directory.Exists(sub).Should().BeTrue();
    }

    [Fact(DisplayName = "多级上溯：删空子目录后父目录也空 → 连删到监控根下一层")]
    public void MultiLevel_CollapsesUpToBoundary()
    {
        string watch = Dir("watch");
        string lvl1 = Dir("watch", "Group");
        string lvl2 = Dir("watch", "Group", "MovieA");
        Touch(lvl2, "MovieA.torrent");

        IReadOnlyList<string> deleted = _sut.CleanUpward(lvl2, watch, Ignore(".torrent"));

        deleted.Should().HaveCount(2);
        Directory.Exists(lvl2).Should().BeFalse();
        Directory.Exists(lvl1).Should().BeFalse();
        Directory.Exists(watch).Should().BeTrue("回收止于监控根，绝不删根本身");
    }

    [Fact(DisplayName = "兄弟目录有内容时父目录不空，仅删本支")]
    public void SiblingWithContent_StopsAtParent()
    {
        string watch = Dir("watch");
        string group = Dir("watch", "Group");
        string movieA = Dir("watch", "Group", "MovieA");
        string movieB = Dir("watch", "Group", "MovieB");
        Touch(movieA, "MovieA.torrent");
        Touch(movieB, "MovieB.mkv"); // 兄弟有真实内容

        IReadOnlyList<string> deleted = _sut.CleanUpward(movieA, watch, Ignore(".torrent"));

        deleted.Should().ContainSingle().Which.Should().Be(Norm(movieA));
        Directory.Exists(movieA).Should().BeFalse();
        Directory.Exists(group).Should().BeTrue("父目录因兄弟 MovieB 有内容而非空，停止上溯");
        Directory.Exists(movieB).Should().BeTrue();
    }

    [Fact(DisplayName = "边界守护：起点等于监控根时不做任何删除")]
    public void Boundary_StartEqualsRoot_NoDelete()
    {
        string watch = Dir("watch");

        IReadOnlyList<string> deleted = _sut.CleanUpward(watch, watch, Ignore(".torrent"));

        deleted.Should().BeEmpty();
        Directory.Exists(watch).Should().BeTrue();
    }

    [Fact(DisplayName = "边界守护：起点在监控根之外时不做任何删除")]
    public void Boundary_StartOutsideRoot_NoDelete()
    {
        string watch = Dir("watch");
        string outside = Dir("other", "MovieA"); // 不在 watch 之下
        Touch(outside, "MovieA.torrent");

        IReadOnlyList<string> deleted = _sut.CleanUpward(outside, watch, Ignore(".torrent"));

        deleted.Should().BeEmpty();
        Directory.Exists(outside).Should().BeTrue();
    }
}
