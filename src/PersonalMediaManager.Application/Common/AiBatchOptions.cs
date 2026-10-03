namespace PersonalMediaManager.Application.Common;

/// <summary>有界批处理参数；默认保持单条语义</summary>
public sealed record AiBatchOptions
{
    public const int MaxAutomaticFallbackRequests = 4;
    public const int MaxItemInputBytes = 65536;
    public const long MaxPendingInputBytes = 8 * 1024 * 1024;
    public const long MaxPreparedManagedGrowthBytes = 128 * 1024 * 1024;
    public int MaxItems { get; init; } = 4;
    public int ExternalMaxItems { get; init; } = 1;
    public int LocalMaxItems { get; init; } = 1;
    public int MaxWaitMilliseconds { get; init; } = 50;
    public int ContextTokenBudget { get; init; } = 8192;
    public int MaxOutputTokens { get; init; } = 2048;
    public bool DisableThinking { get; init; }
    public int MaxResponseBytes { get; init; } = 65536;
    public void Validate()
    {
        if (MaxItems is < 1 or > 128 || ExternalMaxItems is < 1 or > 128 || LocalMaxItems is < 1 or > 2
            || MaxWaitMilliseconds is < 0 or > 1000 || ContextTokenBudget is < 512 or > 1048576
            || MaxOutputTokens is < 64 or > 262144 || MaxResponseBytes is < 1024 or > 8388608)
            throw new BusinessException("AI 批处理资源参数超出允许范围");
    }
}
