using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.LocalAi;
using PersonalMediaManager.Application.Dtos.Settings;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Infrastructure.Persistence.Services.LocalAi;
using PersonalMediaManager.Infrastructure.Persistence.Services.Settings;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

public sealed class LocalAiSettingsServiceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Factory _factory;
    private readonly LocalAiSettingsService _settings;
    private readonly GeneralSettingsService _general;

    public LocalAiSettingsServiceTests()
    {
        _connection.Open();
        _factory = new(_connection);
        using PmmDbContext db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        _settings = new(_factory);
        _general = new(_factory, Substitute.For<IProxyResolver>(), Substitute.For<IFfmpegToolTester>());
    }

    [Fact]
    public async Task AbsentSettings_AreDisabledWithoutWritingRows()
    {
        LocalAiSettingsDto result = await _settings.GetAsync();
        result.Mode.Should().Be(LocalAiMode.Disabled);
        result.RuntimeExecutablePath.Should().BeEmpty();
        using PmmDbContext db = _factory.CreateDbContext();
        db.SystemSettings.Any(s => s.Key == LocalAiSettingsService.SettingsKey).Should().BeFalse();
    }

    [Fact]
    public async Task Update_UsesExistingKvSchema_AndRoundTrips()
    {
        LocalAiSettingsDto expected = new() { Mode = LocalAiMode.BeforeRules, Threads = 3 };
        await _settings.UpdateAsync(expected);
        (await _settings.GetAsync()).Should().Be(expected);
        using PmmDbContext db = _factory.CreateDbContext();
        db.SystemSettings.Count(s => s.Key == LocalAiSettingsService.SettingsKey).Should().Be(1);
    }

    [Theory]
    [InlineData("LocalAi_Settings")]
    [InlineData("localai_settings")]
    [InlineData("LOCALAI_Port")]
    public async Task GeneralEndpoint_CannotReadOrOverwriteProtectedNamespace(string key)
    {
        using (PmmDbContext db = _factory.CreateDbContext())
        {
            db.SystemSettings.Add(new SystemSetting { Key = key, Value = "原值", Category = "LocalAi" });
            await db.SaveChangesAsync();
        }
        GroupedSettingsResponse list = await _general.ListAsync();
        list.Groups.Values.SelectMany(x => x).Should().NotContain(x => x.Key == key);
        await Assert.ThrowsAsync<BusinessException>(() => _general.UpdateAsync(new UpdateGeneralRequest([
            new UpdateGeneralItem("OtherSetting", "不能部分保存"), new UpdateGeneralItem(key, "新值")])));
        using PmmDbContext check = _factory.CreateDbContext();
        (await check.SystemSettings.SingleAsync(x => x.Key == key)).Value.Should().Be("原值");
        check.SystemSettings.Any(x => x.Key == "OtherSetting").Should().BeFalse();
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"Mode\":\"BeforeRules\",\"Port\":0}")]
    [InlineData("{\"Mode\":\"BeforeRules\",\"RuntimeExecutablePath\":\"/bin/sh\"}")]
    public async Task CorruptOrUnsafeStoredSettings_FailClosed(string json)
    {
        using (PmmDbContext db = _factory.CreateDbContext())
        {
            db.SystemSettings.Add(new SystemSetting { Key = LocalAiSettingsService.SettingsKey, Value = json });
            await db.SaveChangesAsync();
        }
        (await _settings.GetAsync()).Mode.Should().Be(LocalAiMode.Disabled);
        (await _settings.GetAsync()).RuntimeExecutablePath.Should().BeEmpty();
    }

    [Fact]
    public async Task InvalidParameters_AreRejectedBeforePersistence()
    {
        LocalAiSettingsDto[] invalid = [new() { Port = 80 }, new() { Threads = 17 }, new() { ContextTokens = 8192 },
            new() { MaxOutputTokens = 2048 }, new() { ContextTokens = 512, MaxOutputTokens = 512 },
            new() { TimeoutSeconds = 121 }, new() { StartupTimeoutSeconds = 181 }, new() { MemoryLimitMb = 4097 },
            new() { ModelId = "remote-model" }, new() { Mode = (LocalAiMode)99 }, new() { RuntimeExecutablePath = "/bin/sh" },
            new() { RuntimeExecutablePath = "llama-server" }, new() { RuntimeExecutablePath = "//remote/llama-server" }];
        foreach (LocalAiSettingsDto settings in invalid)
            await Assert.ThrowsAsync<BusinessException>(() => _settings.UpdateAsync(settings));
        using PmmDbContext db = _factory.CreateDbContext();
        db.SystemSettings.Any(x => x.Key == LocalAiSettingsService.SettingsKey).Should().BeFalse();
    }

    public void Dispose() => _connection.Dispose();

    private sealed class Factory(SqliteConnection connection) : IDbContextFactory<PmmDbContext>
    {
        public PmmDbContext CreateDbContext() => new(new DbContextOptionsBuilder<PmmDbContext>().UseSqlite(connection).Options);
    }
}
