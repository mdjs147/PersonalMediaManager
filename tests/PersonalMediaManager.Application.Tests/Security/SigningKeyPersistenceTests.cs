using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Infrastructure.Platform.Security;

namespace PersonalMediaManager.Application.Tests.Security;

/// <summary>签名密钥私有存储与并发初始化</summary>
public sealed class SigningKeyPersistenceTests : IDisposable
{
    private readonly AppPaths _paths = AppPaths.ForRoot(Path.Combine(Path.GetTempPath(), $"pmm-key-test-{Guid.NewGuid():N}"));
    private readonly IConfiguration _configuration = new ConfigurationBuilder().Build();

    [Fact]
    public async Task Concurrent_Initializers_Reuse_One_Complete_Private_Key()
    {
        Task<string>[] starters = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => new SigningKeyProvider(_configuration, _paths).GetSigningKey())).ToArray();
        string[] keys = await Task.WhenAll(starters);
        keys.Distinct().Should().ContainSingle();
        Convert.FromBase64String(keys[0]).Should().HaveCount(32);
        string path = Path.Combine(_paths.Root, "jwt-signing-key.txt");
        File.ReadAllText(path).Should().Be(keys[0]);
        Directory.GetFiles(_paths.Root, "*.tmp-*").Should().BeEmpty();
        if (!OperatingSystem.IsWindows()) File.GetUnixFileMode(path).Should().Be(PrivateFileSystem.FilePermissions);
    }

    [Fact]
    public void Existing_Key_Is_Preserved_And_Restricted()
    {
        string path = Path.Combine(_paths.Root, "jwt-signing-key.txt");
        File.WriteAllText(path, "existing-signing-key");
        new SigningKeyProvider(_configuration, _paths).GetSigningKey().Should().Be("existing-signing-key");
        if (!OperatingSystem.IsWindows()) File.GetUnixFileMode(path).Should().Be(PrivateFileSystem.FilePermissions);
    }

    [Fact]
    public void Empty_Existing_Key_Is_Not_Silently_Replaced()
    {
        File.WriteAllText(Path.Combine(_paths.Root, "jwt-signing-key.txt"), " ");
        Action create = () => new SigningKeyProvider(_configuration, _paths);
        create.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void DataProtection_Creates_Private_KeyRing_Files()
    {
        IDataProtectionProvider provider = DataProtectionProvider.Create(new DirectoryInfo(_paths.KeyRingDir));
        provider.CreateProtector("测试私有密钥环").Protect("测试内容");
        string[] keys = Directory.GetFiles(_paths.KeyRingDir, "*.xml");
        keys.Should().NotBeEmpty();
        if (!OperatingSystem.IsWindows())
            foreach (string file in keys) File.GetUnixFileMode(file).Should().Be(PrivateFileSystem.FilePermissions);
    }

    public void Dispose() => Directory.Delete(_paths.Root, recursive: true);
}
