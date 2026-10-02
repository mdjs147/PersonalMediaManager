using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.LocalAi;
using PersonalMediaManager.Application.Services.LocalAi;
using PersonalMediaManager.Domain.Entities;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.LocalAi;

/// <summary>使用现有 KV 表保存有界本地模型设置</summary>
internal sealed class LocalAiSettingsService(IDbContextFactory<PmmDbContext> dbFactory) : ILocalAiSettingsService
{
    internal const string SettingsKey = "LocalAi_Settings";
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<LocalAiSettingsDto> GetAsync(CancellationToken ct = default)
    {
        await using PmmDbContext db = await dbFactory.CreateDbContextAsync(ct);
        string? json = await db.SystemSettings.AsNoTracking().Where(x => x.Key == SettingsKey).Select(x => x.Value).SingleOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            LocalAiSettingsDto settings = JsonSerializer.Deserialize<LocalAiSettingsDto>(json) ?? new();
            settings.Validate();
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or BusinessException or ArgumentException)
        {
            // 旧库或手工损坏配置一律回到关闭，不能扩大可执行程序或网络范围。
            return new();
        }
    }

    public async Task UpdateAsync(LocalAiSettingsDto settings, CancellationToken ct = default)
    {
        settings.Validate();
        await _gate.WaitAsync(ct);
        try
        {
            await using PmmDbContext db = await dbFactory.CreateDbContextAsync(ct);
            SystemSetting? row = await db.SystemSettings.SingleOrDefaultAsync(x => x.Key == SettingsKey, ct);
            if (row is null)
            {
                row = new SystemSetting { Key = SettingsKey, Category = "LocalAi", Description = "本地模型专用设置" };
                db.SystemSettings.Add(row);
            }
            row.Value = JsonSerializer.Serialize(settings);
            await db.SaveChangesAsync(ct);
        }
        finally { _gate.Release(); }
    }
}

/// <summary>本地模型持久化独立注册入口</summary>
public static class LocalAiPersistenceExtensions
{
    public static IServiceCollection AddLocalAiSettings(this IServiceCollection services) =>
        services.AddSingleton<ILocalAiSettingsService, LocalAiSettingsService>();
}
