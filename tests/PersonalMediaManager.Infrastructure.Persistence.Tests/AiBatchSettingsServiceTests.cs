using PersonalMediaManager.Domain.Aggregates.AiProviders;
using PersonalMediaManager.Domain.Enums;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.Ai;
using PersonalMediaManager.Infrastructure.Persistence.Services.Settings;
namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

public sealed class AiBatchSettingsServiceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly Factory _factory;
    public AiBatchSettingsServiceTests()
    {
        _connection.Open(); _factory = new(_connection);
        using PmmDbContext db = _factory.CreateDbContext(); db.Database.EnsureCreated();
    }
    [Fact]
    public async Task DefaultsStaySingleAndSavingDoesNotTouchLocalModel()
    {
        using (PmmDbContext db = _factory.CreateDbContext())
        {
            db.SystemSettings.Add(new() { Key = "LocalAi_Settings", Value = "{\"Mode\":2}", Category = "LocalAi" });
            db.SaveChanges();
        }
        AiBatchSettingsService service = new(_factory);
        (await service.GetAsync()).Should().Be(new AiBatchSettingsDto());
        await service.UpdateAsync(new() { ExternalBatchSize = 4, LocalBatchSize = 2 });
        (await service.GetAsync()).ExternalBatchSize.Should().Be(4);
        (await service.GetAsync()).ToOptions().MaxItems.Should().Be(4);
        using PmmDbContext check = _factory.CreateDbContext();
        check.SystemSettings.Single(x => x.Key == "LocalAi_Settings").Value.Should().Be("{\"Mode\":2}");
    }
    [Theory]
    [InlineData(0, 1)]
    [InlineData(129, 1)]
    [InlineData(1, 3)]
    public async Task InvalidSizesNeverPersist(int external, int local)
    {
        AiBatchSettingsService service = new(_factory);
        Func<Task> save = () => service.UpdateAsync(new() { ExternalBatchSize = external, LocalBatchSize = local });
        await save.Should().ThrowAsync<BusinessException>();
        (await service.GetAsync()).Should().Be(new AiBatchSettingsDto());
    }
    [Fact]
    public async Task BoundPresetRoundTripsAndNeverAppliesToAnotherModel()
    {
        long id;
        using (PmmDbContext db = _factory.CreateDbContext())
        {
            ParseAiProvider provider = new() { Name = "官方合成配置", Type = AiProviderType.OpenAiCompatible,
                BaseUrl = "https://api.deepseek.com", Model = "deepseek-flash", Enabled = true };
            db.ParseAiProviders.Add(provider); db.SaveChanges(); id = provider.Id;
        }
        AiBatchSettingsService service = new(_factory);
        AiBatchProviderInfoDto info = (await service.GetProvidersAsync()).Single();
        await service.UpdateAsync(new() { ProviderSettings = [info.RecommendedSettings!] });
        AiBatchSettingsDto loaded = await service.GetAsync();
        loaded.ToOptions(id, info.ConfigurationKey).ExternalMaxItems.Should().Be(32);
        loaded.ToOptions(id + 1, info.ConfigurationKey).ExternalMaxItems.Should().Be(1);
        using (PmmDbContext db = _factory.CreateDbContext())
        {
            db.ParseAiProviders.Single().Model = "small-private-model"; db.SaveChanges();
        }
        AiBatchProviderInfoDto changed = (await service.GetProvidersAsync()).Single();
        changed.RecommendedSettings.Should().BeNull();
        loaded.ToOptions(id, changed.ConfigurationKey).ContextTokenBudget.Should().Be(8192);
        // 未编辑的旧绑定允许保留，不阻断其他设置；该旧绑定不会应用到新模型。
        await service.UpdateAsync(loaded with { LocalBatchSize = 2 });
        Func<Task> staleEdit = () => service.UpdateAsync(loaded with
            { ProviderSettings = [info.RecommendedSettings! with { BatchSize = 64 }] });
        await staleEdit.Should().ThrowAsync<BusinessException>();
        (await service.GetAsync()).LocalBatchSize.Should().Be(2);
    }
    [Fact]
    public async Task DisplayNameCannotEnableOfficialPresetAndStoredJsonStaysBounded()
    {
        using (PmmDbContext db = _factory.CreateDbContext())
        {
            db.ParseAiProviders.Add(new() { Name = "DeepSeek", Type = AiProviderType.OpenAiCompatible,
                BaseUrl = "https://proxy.invalid", Model = "deepseek-flash", Enabled = true });
            db.SaveChanges();
        }
        AiBatchSettingsService service = new(_factory);
        (await service.GetProvidersAsync()).Single().RecommendedSettings.Should().BeNull();
        await service.UpdateAsync(new() { ExternalBatchSize = 128 });
        using PmmDbContext check = _factory.CreateDbContext();
        check.SystemSettings.Single(x => x.Key == AiBatchSettingsService.SettingsKey).Value!.Length.Should().BeLessThanOrEqualTo(4000);
    }
    [Fact]
    public async Task MoreThanSixteenProviderOverridesNeverPersist()
    {
        AiBatchSettingsService service = new(_factory);
        Func<Task> save = () => service.UpdateAsync(new() { ProviderSettings = Enumerable.Range(1, 17)
            .Select(id => new AiProviderBatchSettingsDto { ProviderId = id, ConfigurationKey = new string('A', 64) }).ToArray() });
        await save.Should().ThrowAsync<BusinessException>();
        using PmmDbContext check = _factory.CreateDbContext();
        check.SystemSettings.Where(x => x.Key == AiBatchSettingsService.SettingsKey).Should().BeEmpty();
    }
    public void Dispose() => _connection.Dispose();
    private sealed class Factory(SqliteConnection connection) : IDbContextFactory<PmmDbContext>
    {
        public PmmDbContext CreateDbContext() => new(new DbContextOptionsBuilder<PmmDbContext>().UseSqlite(connection).Options);
    }
}
