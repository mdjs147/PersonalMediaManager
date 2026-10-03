namespace PersonalMediaManager.Application.Dtos.Diagnostics;

/// <summary>管理员可读取的本地诊断级别与有界容量快照</summary>
public sealed record ParseDiagnosticSettingsDto(string Level, int MaxTextUtf8Bytes, int MaxEventUtf8Bytes,
    long MaxFileBytes, long MaxTotalBytes, int RetentionDays, int MaxFiles, int MaxArtifactUtf8Bytes,
    long MaxArtifactTotalBytes, int MaxArtifacts, long StorageWriteFailures, string BodyBoundary);
