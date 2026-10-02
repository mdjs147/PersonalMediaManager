using PersonalMediaManager.Application.Dtos.LocalAi;

namespace PersonalMediaManager.Application.Contracts.LocalAi;

/// <summary>无凭据的回环单次推理</summary>
public interface ILocalAiInferenceClient
{
    Task<LocalAiInferenceResult> GenerateAsync(LocalAiInferenceRequest request, CancellationToken ct = default);
}
