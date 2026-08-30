using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Domain.Aggregates.AiProviders;
using PersonalMediaManager.Domain.Aggregates.WebhookSubscriptions;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Persistence;
using PersonalMediaManager.Infrastructure.Platform.Security;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>验证 IProtectedFieldService 加解密往返 + 数据库列存的是密文（不是明文）</summary>
/// <remarks>
/// 阶段 B 采用「应用服务显式 Protect / Unprotect」模式；EF ValueConverter 自动转换留 C 阶段
/// （需要 OnModelCreating 接收 IProtectedFieldService，与 EF Migration 设计时实例化冲突，方案复杂）。
/// 测试用 EphemeralDataProtectionProvider 避免文件 IO 与跨测试密钥环污染。
/// </remarks>
public sealed class EncryptionRoundTripTests : IClassFixture<PmmDbContextTestFixture>
{
    private readonly PmmDbContextTestFixture _fixture;
    private readonly IProtectedFieldService _crypto;

    public EncryptionRoundTripTests(PmmDbContextTestFixture fixture)
    {
        _fixture = fixture;
        _crypto = new DataProtectionFieldService(new EphemeralDataProtectionProvider());
    }

    [Theory]
    [InlineData("TMDB-test-key-abc123")]
    [InlineData("sk-or-v1-超长 unicode 密 钥 测 试 ✓")]
    public void Protect_Unprotect_RoundTrip_ExactMatch(string plaintext)
    {
        string ciphertext = _crypto.Protect(plaintext);
        ciphertext.Should().NotBe(plaintext, "密文必须与明文不同");
        _crypto.Unprotect(ciphertext).Should().Be(plaintext);
    }

    [Fact]
    public async Task TmdbSetting_ApiKeyEncrypted_StoresCipher_NotPlaintext()
    {
        const string plainKey = "TMDB-secret-987";
        string cipher = _crypto.Protect(plainKey);

        using (PmmDbContext writer = _fixture.CreateContext())
        {
            TmdbSetting? setting = await writer.TmdbSettings.FirstOrDefaultAsync(s => s.Id == 1);
            setting.Should().NotBeNull();
            setting!.ApiKeyEncrypted = cipher;
            await writer.SaveChangesAsync();
        }

        using PmmDbContext reader = _fixture.CreateContext();
        string? raw = await reader.Database.SqlQuery<string>(
            $"SELECT ApiKeyEncrypted as Value FROM Tmdb_Setting WHERE Id = 1")
            .FirstOrDefaultAsync();
        raw.Should().NotBeNull();
        raw.Should().NotContain(plainKey, "SQL 列必须存密文");
        _crypto.Unprotect(raw!).Should().Be(plainKey, "通过 IProtectedFieldService 读出应等于原文");
    }

    [Fact]
    public async Task ParseAiProvider_ApiKeyEncrypted_StoresCipher()
    {
        const string plainKey = "ollama-no-key";
        string cipher = _crypto.Protect(plainKey);

        long id;
        using (PmmDbContext writer = _fixture.CreateContext())
        {
            ParseAiProvider provider = new()
            {
                Name = "测试-Ollama",
                Type = AiProviderType.Ollama,
                BaseUrl = "http://localhost:11434",
                Model = "llama3",
                ApiKeyEncrypted = cipher,
            };
            writer.ParseAiProviders.Add(provider);
            await writer.SaveChangesAsync();
            id = provider.Id;
        }

        using PmmDbContext reader = _fixture.CreateContext();
        string? raw = await reader.Database.SqlQuery<string>(
            $"SELECT ApiKeyEncrypted as Value FROM Parse_AiProvider WHERE Id = {id}")
            .FirstOrDefaultAsync();
        raw.Should().NotBeNull();
        raw.Should().NotContain(plainKey);
        _crypto.Unprotect(raw!).Should().Be(plainKey);
    }

    [Fact]
    public async Task WebhookSubscription_SecretEncrypted_StoresCipher()
    {
        const string plainSecret = "webhook-hmac-secret-XYZ";
        string cipher = _crypto.Protect(plainSecret);

        long id;
        using (PmmDbContext writer = _fixture.CreateContext())
        {
            WebhookSubscription sub = new()
            {
                Name = "测试 Webhook",
                Url = "https://example.com/h",
                SecretEncrypted = cipher,
                Events = ["media.archived"],
            };
            writer.WebhookSubscriptions.Add(sub);
            await writer.SaveChangesAsync();
            id = sub.Id;
        }

        using PmmDbContext reader = _fixture.CreateContext();
        string? raw = await reader.Database.SqlQuery<string>(
            $"SELECT SecretEncrypted as Value FROM Webhook_Subscription WHERE Id = {id}")
            .FirstOrDefaultAsync();
        raw.Should().NotBeNull();
        raw.Should().NotContain(plainSecret);
        _crypto.Unprotect(raw!).Should().Be(plainSecret);
    }
}
