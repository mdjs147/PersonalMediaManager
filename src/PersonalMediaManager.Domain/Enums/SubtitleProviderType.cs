namespace PersonalMediaManager.Domain.Enums;

/// <summary>字幕源类型（Subtitle_Download.Provider）</summary>
/// <remarks>
/// 首发仅支持 Assrt（伪射手 assrt.net）；预留后续扩展（如 OpenSubtitles = 2）。
/// 数据库以字符串存储（"Assrt"），EF 配置 HasConversion；新增成员只能追加，勿改既有值。
/// </remarks>
public enum SubtitleProviderType
{
    /// <summary>Assrt 伪射手字幕网（api.assrt.net）</summary>
    Assrt = 1,
}
