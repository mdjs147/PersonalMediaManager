using PersonalMediaManager.Application.Common;
namespace PersonalMediaManager.Application.Dtos.Ai;

/// <summary>独立批量设置；不更改模型开关</summary>
public sealed record AiBatchSettingsDto
{
    public int ExternalBatchSize { get; init; } = 1;
    public int LocalBatchSize { get; init; } = 1;
    public int MaxWaitMilliseconds { get; init; } = 50;
    public int ContextTokenBudget { get; init; } = 8192;
    public int MaxOutputTokens { get; init; } = 2048;
    public int MaxResponseBytes { get; init; } = 65536;
    public IReadOnlyList<AiProviderBatchSettingsDto> ProviderSettings { get; init; } = [];
    public AiBatchOptions ToOptions(long? providerId = null, string? configurationKey = null)
    {
        AiBatchOptions options = new()
        {
            MaxItems = Math.Max(Math.Max(ExternalBatchSize, LocalBatchSize), ProviderSettings.Select(p => p.BatchSize).DefaultIfEmpty(1).Max()), ExternalMaxItems = ExternalBatchSize,
            LocalMaxItems = LocalBatchSize, MaxWaitMilliseconds = MaxWaitMilliseconds,
            ContextTokenBudget = ContextTokenBudget, MaxOutputTokens = MaxOutputTokens, MaxResponseBytes = MaxResponseBytes,
        };
        options.Validate();
        AiProviderBatchSettingsDto? provider = ProviderSettings.FirstOrDefault(p => p.ProviderId == providerId
            && p.ConfigurationKey == configurationKey);
        return provider is null ? options : options with { ExternalMaxItems = provider.BatchSize,
            ContextTokenBudget = provider.ContextTokenBudget, MaxOutputTokens = provider.MaxOutputTokens, MaxResponseBytes = provider.MaxResponseBytes, DisableThinking = provider.DisableThinking };
    }
    public void Validate()
    {
        if (ProviderSettings is null || ProviderSettings.Count > 16 || ProviderSettings.Any(p => p is null)
            || ProviderSettings.Select(p => p.ProviderId).Distinct().Count() != ProviderSettings.Count)
            throw new BusinessException("AI 提供商批量覆盖列表无效");
        foreach (AiProviderBatchSettingsDto provider in ProviderSettings) provider.Validate();
        ToOptions();
    }
}

/// <summary>绑定提供商当前模型和端点的批量预算</summary>
public sealed record AiProviderBatchSettingsDto
{
    public long ProviderId { get; init; }
    public string ConfigurationKey { get; init; } = "";
    public int BatchSize { get; init; } = 1;
    public bool DisableThinking { get; init; }
    public int ContextTokenBudget { get; init; } = 8192;
    public int MaxOutputTokens { get; init; } = 2048;
    public int MaxResponseBytes { get; init; } = 65536;
    public void Validate()
    {
        if (ProviderId <= 0 || ConfigurationKey is not { Length: 64 } || !ConfigurationKey.All(Uri.IsHexDigit))
            throw new BusinessException("AI 提供商覆盖缺少有效的配置绑定");
        new AiBatchOptions { ExternalMaxItems = BatchSize, ContextTokenBudget = ContextTokenBudget,
            MaxOutputTokens = MaxOutputTokens, MaxResponseBytes = MaxResponseBytes }.Validate();
    }
}

/// <summary>提供商安全显示信息及显式推荐预设</summary>
public sealed record AiBatchProviderInfoDto(long ProviderId, string Name, string Model, string ConfigurationKey,
    AiProviderBatchSettingsDto? RecommendedSettings, AiProviderBatchSettingsDto? AdvancedSettings);
