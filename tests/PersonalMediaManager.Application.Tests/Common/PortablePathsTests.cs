using PersonalMediaManager.Application.Common;

namespace PersonalMediaManager.Application.Tests.Common;

/// <summary>跨平台数据路径与权限约束</summary>
public sealed class PortablePathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pmm-path-test-{Guid.NewGuid():N}");

    [Fact]
    public void Mac_Uses_ApplicationSupport_Regardless_Of_Xdg()
    {
        string home = Path.Combine(Path.GetTempPath(), "home");
        AppPaths.ResolveUnixRoot(home, Path.GetTempPath(), macOS: true)
            .Should().Be(Path.Combine(home, "Library", "Application Support", "PersonalMediaManager"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("relative/data")]
    public void Linux_Ignores_Empty_Or_Relative_Xdg(string? xdg)
    {
        string home = Path.Combine(Path.GetTempPath(), "home");
        AppPaths.ResolveUnixRoot(home, xdg, macOS: false)
            .Should().Be(Path.Combine(home, ".local", "share", "PersonalMediaManager"));
    }

    [Fact]
    public void Linux_Uses_Absolute_Xdg()
    {
        AppPaths.ResolveUnixRoot("unused", _root, macOS: false)
            .Should().Be(Path.Combine(_root, "PersonalMediaManager"));
    }

    [Fact]
    public void ForRoot_Creates_Private_Data_Directories()
    {
        AppPaths paths = AppPaths.ForRoot(_root);
        paths.Root.Should().Be(Path.GetFullPath(_root));
        foreach (string path in new[] { paths.Root, paths.LogDir, paths.KeyRingDir, paths.CacheDir, paths.PostersDir, paths.BackupDir })
        {
            Directory.Exists(path).Should().BeTrue();
            if (!OperatingSystem.IsWindows())
                File.GetUnixFileMode(path).Should().Be(PrivateFileSystem.DirectoryPermissions);
        }
    }

    [Fact]
    public void ForRoot_Rejects_Public_Existing_Root_Without_Chmod()
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_root);
        UnixFileMode original = PrivateFileSystem.DirectoryPermissions | UnixFileMode.GroupRead | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(_root, original);

        Action create = () => AppPaths.ForRoot(_root);
        create.Should().Throw<UnauthorizedAccessException>().WithMessage("*0700*");
        File.GetUnixFileMode(_root).Should().Be(original);
        Directory.Exists(Path.Combine(_root, "keys")).Should().BeFalse();
    }

    [Fact]
    public void ForRoot_Rejects_Symbolic_Root()
    {
        if (OperatingSystem.IsWindows()) return;
        PrivateFileSystem.EnsureDirectory(_root);
        string target = Path.Combine(_root, "target");
        PrivateFileSystem.EnsureDirectory(target);
        string link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, target);

        Action create = () => AppPaths.ForRoot(link);
        create.Should().Throw<UnauthorizedAccessException>().WithMessage("*符号链接*");
    }

    [Fact]
    public void Containment_Does_Not_Accept_Sibling_Prefix_Or_Traversal()
    {
        PlatformPaths.IsWithinDirectory(Path.Combine(_root, "media.mkv"), _root).Should().BeTrue();
        PlatformPaths.IsWithinDirectory(_root + "-other/file", _root).Should().BeFalse();
        PlatformPaths.IsWithinDirectory(Path.Combine(_root, "..", "escape"), _root).Should().BeFalse();
        PlatformPaths.IsWithinDirectory(_root, _root).Should().BeFalse();
        PlatformPaths.IsWithinDirectory(_root + Path.DirectorySeparatorChar, _root).Should().BeFalse();
        string filesystemRoot = Path.GetPathRoot(_root)!;
        PlatformPaths.IsWithinDirectory(filesystemRoot, filesystemRoot).Should().BeFalse();
        PlatformPaths.IsWithinDirectory(Path.Combine(_root.ToUpperInvariant(), "file"), _root)
            .Should().Be(OperatingSystem.IsWindows());
    }

    [Fact]
    public void Sensitive_Files_And_Temporary_Directories_Are_Private()
    {
        string temporary = PrivateFileSystem.CreateTemporaryDirectory("pmm-permissions-");
        try
        {
            string file = Path.Combine(temporary, "secret");
            using (PrivateFileSystem.CreateNew(file)) { }
            if (!OperatingSystem.IsWindows())
            {
                File.GetUnixFileMode(temporary).Should().Be(PrivateFileSystem.DirectoryPermissions);
                File.GetUnixFileMode(file).Should().Be(PrivateFileSystem.FilePermissions);
            }
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
