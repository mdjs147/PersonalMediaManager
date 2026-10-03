namespace PersonalMediaManager.Application.Contracts;

/// <summary>批量自动回退预算用尽，留待显式重试</summary>
/// <remarks>不属于提供商故障或瞬时错误，调用链不能自动升级或重发这些项。</remarks>
public sealed class AiProviderBatchDeferredException(string message) : Exception(message);
