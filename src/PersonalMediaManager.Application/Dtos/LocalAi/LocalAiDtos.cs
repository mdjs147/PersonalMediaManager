using System.Text.Json.Serialization;
using PersonalMediaManager.Application.Common;

namespace PersonalMediaManager.Application.Dtos.LocalAi;

/// <summary>本地候选建议的介入位置</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LocalAiMode>))]
public enum LocalAiMode { Disabled, BeforeRules, AfterRules }

/// <summary>固定模型标识</summary>
public static class LocalAiModelIds
{
    public const string Qwen = "qwen2.5-0.5b-instruct-q8_0";
    public const string Huihui = "huihui-qwen2.5-0.5b-v3-q8_0";
}

/// <summary>本地模型设置；默认关闭</summary>
public sealed record LocalAiSettingsDto
{
    public LocalAiMode Mode { get; init; } = LocalAiMode.Disabled;
    public string ModelId { get; init; } = LocalAiModelIds.Qwen;
    public string RuntimeExecutablePath { get; init; } = string.Empty;
    public int Port { get; init; } = 18081;
    public int Threads { get; init; } = 2;
    public int ContextTokens { get; init; } = 2048;
    public int MaxOutputTokens { get; init; } = 512;
    public int TimeoutSeconds { get; init; } = 30;
    public int StartupTimeoutSeconds { get; init; } = 90;
    public int MemoryLimitMb { get; init; } = 2048;

    /// <summary>统一校验持久化与运行时的配置边界</summary>
    public void Validate()
    {
        if (!Enum.IsDefined(Mode)) throw new BusinessException("本地模型模式无效");
        if (ModelId is not (LocalAiModelIds.Qwen or LocalAiModelIds.Huihui))
            throw new BusinessException("本地模型不在允许列表中");
        if (RuntimeExecutablePath is null || RuntimeExecutablePath.Length > 1024 || RuntimeExecutablePath.Any(char.IsControl))
            throw new BusinessException("运行时路径无效");
        if (RuntimeExecutablePath.Length > 0)
        {
            string expectedName = OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server";
            if (!Path.IsPathFullyQualified(RuntimeExecutablePath) || RuntimeExecutablePath.StartsWith("\\\\", StringComparison.Ordinal)
                || RuntimeExecutablePath.StartsWith("//", StringComparison.Ordinal)
                || !string.Equals(Path.GetFileName(RuntimeExecutablePath), expectedName,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new BusinessException("请填写本机已安装的 llama-server 可执行文件绝对路径");
        }
        if (Port is < 1024 or > 65535 || Threads is < 1 or > 16 || ContextTokens is < 512 or > 4096
            || MaxOutputTokens is < 64 or > 1024 || MaxOutputTokens >= ContextTokens
            || TimeoutSeconds is < 5 or > 120 || StartupTimeoutSeconds is < 5 or > 180
            || MemoryLimitMb is < 768 or > 4096)
            throw new BusinessException("本地模型资源参数超出允许范围");
    }
}

/// <summary>固定模型与本机安装状态</summary>
public sealed record LocalAiModelDto(string Id, string Name, string SourceUrl, string? Revision,
    long? SizeBytes, string? Sha256, bool CanDownload, string? UnavailableReason, bool Installed,
    bool CanVerify, string FileName, string LocalPath, string? ConversionRevision = null);

/// <summary>受管运行时与下载状态</summary>
public sealed record LocalAiStatusDto(string State, string? ModelId, bool RuntimeConfigured, bool PlatformSupported,
    string Message, string? DownloadModelId = null, string DownloadState = "Idle", long DownloadedBytes = 0,
    long? DownloadTotalBytes = null, string? DownloadError = null);

/// <summary>白名单模型下载请求</summary>
public sealed record LocalAiDownloadRequest(string ModelId);

/// <summary>单次有界推理请求</summary>
public sealed record LocalAiInferenceRequest(string SystemPrompt, string UserPrompt, int? MaxOutputTokens = null,
    int? AllowedSpanCount = null);

/// <summary>可回退的本地推理结果</summary>
public sealed record LocalAiInferenceResult(string? Content, string? FailureReason, string? FinishReason = null,
    long ElapsedMilliseconds = 0, string? ModelId = null, bool Attempted = false)
{
    public bool Success => Content is not null && FailureReason is null;
}
