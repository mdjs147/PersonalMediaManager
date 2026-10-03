using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.Ai;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Domain.Entities;
namespace PersonalMediaManager.Infrastructure.Persistence.Services.Settings;

/// <summary>沿用现有 KV 表保存独立批量参数</summary>
internal sealed class AiBatchSettingsService(IDbContextFactory<PmmDbContext> factory) : IAiBatchSettingsService
{
    internal const string SettingsKey = "Parse_AiBatchSettings";
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task<AiBatchSettingsDto> GetAsync(CancellationToken ct = default)
    {
        await using PmmDbContext db = await factory.CreateDbContextAsync(ct);
        string? json = await db.SystemSettings.AsNoTracking().Where(x => x.Key == SettingsKey).Select(x => x.Value).SingleOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            AiBatchSettingsDto settings = JsonSerializer.Deserialize<AiBatchSettingsDto>(json) ?? new();
            settings.Validate();
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or BusinessException) { return new(); }
    }
    public async Task<IReadOnlyList<AiBatchProviderInfoDto>> GetProvidersAsync(CancellationToken ct = default)
    {
        await using PmmDbContext db = await factory.CreateDbContextAsync(ct);
        var rows = await db.ParseAiProviders.AsNoTracking().OrderBy(p => p.Id)
            .Select(p => new { p.Id, p.Name, p.Type, p.BaseUrl, p.Model }).ToListAsync(ct);
        return rows.Select(p => AiBatchProviderPresets.Describe(p.Id, p.Name, p.Type, p.BaseUrl, p.Model)).ToArray();
    }
    public async Task UpdateAsync(AiBatchSettingsDto settings, CancellationToken ct = default)
    {
        settings.Validate();
        string json = JsonSerializer.Serialize(settings);
        if (json.Length > 4000) throw new BusinessException("AI 批量配置超过既有设置存储上限，请减少提供商覆盖项");
        await _gate.WaitAsync(ct);
        try
        {
            IReadOnlyList<AiBatchProviderInfoDto> providers = await GetProvidersAsync(ct);
            AiBatchSettingsDto previous = await GetAsync(ct);
            if (settings.ProviderSettings.Any(setting => !previous.ProviderSettings.Contains(setting)
                && !providers.Any(p => p.ProviderId == setting.ProviderId && p.ConfigurationKey == setting.ConfigurationKey
                    && (!setting.DisableThinking || p.RecommendedSettings is not null))))
                throw new BusinessException("AI 提供商模型或端点已变化，请刷新后重新核对批量覆盖");
            await using PmmDbContext db = await factory.CreateDbContextAsync(ct);
            SystemSetting? row = await db.SystemSettings.SingleOrDefaultAsync(x => x.Key == SettingsKey, ct);
            if (row is null)
            {
                row = new() { Key = SettingsKey, Category = "Parse", Description = "AI 有界批量参数（默认单条）" };
                db.SystemSettings.Add(row);
            }
            row.Value = json;
            await db.SaveChangesAsync(ct);
        }
        finally { _gate.Release(); }
    }
}
