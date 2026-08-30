using PersonalMediaManager.Domain.Common;

namespace PersonalMediaManager.Domain.Entities;

/// <summary>受支持视频扩展名（System_MediaExtension）</summary>
/// <remarks>
/// Extension 字段含前导点，小写存储（如 .mkv），OrdinalIgnoreCase 比较；
/// 14 条内置种子由 Migration HasData 注入，用户可增删改但不强制保护。
/// </remarks>
public sealed class SystemMediaExtension : Entity
{
    /// <summary>扩展名（含前导点，小写，如 .mkv）</summary>
    public string Extension { get; set; } = default!;

    /// <summary>说明（可选）</summary>
    public string? Description { get; set; }

    /// <summary>是否启用</summary>
    public bool Enabled { get; set; } = true;
}
