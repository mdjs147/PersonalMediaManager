using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Host.Composition;

namespace PersonalMediaManager.Host.Tests.Composition;

/// <summary>r3 P3-r3.20：PmmHost.ValidatePaths 防御性校验单测</summary>
/// <remarks>
/// 验证启动期 AppPaths 缺目录 / 不可写时立即抛根因诊断异常，
/// 而不是延后到 Serilog 首次写盘或 DataProtection 加密时才报错（错位根因）。
/// </remarks>
public sealed class PmmHostValidatePathsTests
{
    [Fact]
    public void ValidatePaths_All_Dirs_Exist_And_Writable_DoesNotThrow()
    {
        string root = Path.Combine(Path.GetTempPath(), $"pmm-valid-{Guid.NewGuid():N}");
        try
        {
            AppPaths paths = AppPaths.ForRoot(root);

            Action act = () => PmmHost.ValidatePaths(paths);
            act.Should().NotThrow();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ValidatePaths_Null_Throws_ArgumentNullException()
    {
        Action act = () => PmmHost.ValidatePaths(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ValidatePaths_LogDir_Missing_Throws_DirectoryNotFoundException_With_Label()
    {
        string root = Path.Combine(Path.GetTempPath(), $"pmm-valid-{Guid.NewGuid():N}");
        try
        {
            AppPaths paths = AppPaths.ForRoot(root);
            // 模拟 LogDir 在 Resolve 后被删除（用户清理 / 权限改动）
            Directory.Delete(paths.LogDir, recursive: true);

            Action act = () => PmmHost.ValidatePaths(paths);
            act.Should().Throw<DirectoryNotFoundException>()
                .WithMessage("*LogDir*");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
