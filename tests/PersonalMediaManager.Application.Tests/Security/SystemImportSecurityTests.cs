using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Infrastructure.Platform.System;

namespace PersonalMediaManager.Application.Tests.Security;

/// <summary>备份解压的路径、链接与权限防护</summary>
public sealed class SystemImportSecurityTests : IDisposable
{
    private readonly AppPaths _paths = AppPaths.ForRoot(Path.Combine(Path.GetTempPath(), $"pmm-import-test-{Guid.NewGuid():N}"));

    [Theory]
    [InlineData("../../escape")]
    [InlineData("..\\..\\escape")]
    [InlineData("/absolute-escape")]
    [InlineData("C:/absolute-escape")]
    public async Task Import_Rejects_Unsafe_Paths(string name)
    {
        using MemoryStream input = CreateArchive(name, 0);
        Func<Task> import = () => Service().ImportAsync(input);
        await import.Should().ThrowAsync<BusinessException>().WithMessage("*Zip Slip*");
    }

    [Theory]
    [InlineData(unchecked((int)0xA1FF0000))]
    [InlineData((int)FileAttributes.ReparsePoint)]
    public async Task Import_Rejects_Symbolic_Link_Entries(int attributes)
    {
        using MemoryStream input = CreateArchive("keys/link", attributes);
        Func<Task> import = () => Service().ImportAsync(input);
        await import.Should().ThrowAsync<BusinessException>().WithMessage("*符号链接*");
    }

    [Fact]
    public async Task Import_Stages_Private_Database_And_Key_Files()
    {
        using MemoryStream input = new();
        using (ZipArchive archive = new(input, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (Stream db = archive.CreateEntry("pmm.db").Open())
            {
                byte[] data = new byte[512];
                "SQLite format 3\0"u8.CopyTo(data);
                db.Write(data);
            }
            using Stream key = archive.CreateEntry("keys/nested/key.xml").Open();
            key.Write("测试密钥"u8);
        }
        input.Position = 0;
        await Service().ImportAsync(input);
        string pending = ImportStaging.PendingPathFor(_paths.DbFile);
        string keyFile = Path.Combine(_paths.KeyRingDir, "nested", "key.xml");
        File.Exists(pending).Should().BeTrue();
        File.Exists(keyFile).Should().BeTrue();
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(pending).Should().Be(PrivateFileSystem.FilePermissions);
            File.GetUnixFileMode(keyFile).Should().Be(PrivateFileSystem.FilePermissions);
            File.GetUnixFileMode(Path.GetDirectoryName(keyFile)!).Should().Be(PrivateFileSystem.DirectoryPermissions);
        }
    }

    private SystemService Service() => new(_paths, new DatabaseFileLocation(_paths.DbFile),
        NullLogger<SystemService>.Instance, Substitute.For<IVersionInfoProvider>());

    private static MemoryStream CreateArchive(string name, int attributes)
    {
        MemoryStream input = new();
        using (ZipArchive archive = new(input, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = archive.CreateEntry(name);
            entry.ExternalAttributes = attributes;
            using Stream content = entry.Open();
            content.Write("../目标"u8);
        }
        input.Position = 0;
        return input;
    }

    public void Dispose() => Directory.Delete(_paths.Root, recursive: true);
}
