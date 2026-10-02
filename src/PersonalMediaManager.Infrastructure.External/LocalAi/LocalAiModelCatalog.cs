using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.LocalAi;

namespace PersonalMediaManager.Infrastructure.External.LocalAi;

/// <summary>不可由 API 修改的模型制品目录</summary>
internal sealed record LocalAiModelArtifact(string Id, string Name, string SourceUrl, string? Revision,
    string FileName, Uri? DownloadUri, long? SizeBytes, string? Sha256, string? UnavailableReason = null,
    string? ConversionRevision = null)
{
    public bool CanVerify => SizeBytes is > 0 && Sha256 is { Length: 64 } && Sha256.All(Uri.IsHexDigit);
    public bool CanDownload => DownloadUri is not null && CanVerify;
}

internal static class LocalAiModelCatalog
{
    // 下载件使用发布者固定提交；实验自转件仅允许下列已核验大小与哈希，不提供下载地址。
    internal static readonly IReadOnlyList<LocalAiModelArtifact> Models = [
        new(LocalAiModelIds.Qwen, "Qwen2.5 0.5B Instruct Q8_0",
            "https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF/tree/9217f5db79a29953eb74d5343926648285ec7e67",
            "9217f5db79a29953eb74d5343926648285ec7e67", "qwen2.5-0.5b-instruct-q8_0.gguf",
            new Uri("https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF/resolve/9217f5db79a29953eb74d5343926648285ec7e67/qwen2.5-0.5b-instruct-q8_0.gguf"),
            675710816, "ca59ca7f13d0e15a8cfa77bd17e65d24f6844b554a7b6c12e07a5f89ff76844e"),
        new(LocalAiModelIds.Huihui, "Huihui Qwen2.5 0.5B v3 Q8_0（实验，本地自转）",
            "https://huggingface.co/huihui-ai/Qwen2.5-0.5B-Instruct-abliterated-v3/tree/3dee99dac7c99318ed2b4e9932bfbbac060fb024",
            "3dee99dac7c99318ed2b4e9932bfbbac060fb024", "huihui-qwen2.5-0.5b-v3-q8_0.gguf", null,
            531068416, "15c5d19fd98774df4fa4df949e4f8cc7c8b32ff366895e23d69f9222b4082816",
            "尚无已核验官方 GGUF 下载；仅支持手动放入固定路径、与指定大小和 SHA256 完全一致的本地自转 Q8_0 文件",
            "13b4d7135a6351f81e1eccf6361a4eafd50350eb")
    ];

    internal static LocalAiModelArtifact Get(string id) => Models.SingleOrDefault(x => x.Id == id)
        ?? throw new BusinessException("本地模型不在允许列表中");
}
