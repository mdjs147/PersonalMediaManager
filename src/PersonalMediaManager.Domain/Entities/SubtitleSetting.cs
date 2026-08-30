using PersonalMediaManager.Domain.Common;

namespace PersonalMediaManager.Domain.Entities;

/// <summary>字幕下载配置（Subtitle_Setting，独表单例 Id=1）</summary>
/// <remarks>
/// AssrtTokenEncrypted 走 DataProtection；应用层强制 Id = 1，EF 配置 HasData 注入种子。
/// 继承 AggregateRoot 带 RowVersion 乐观并发，防止配置页并发保存互相覆盖。
/// </remarks>
public sealed class SubtitleSetting : AggregateRoot
{
    /// <summary>Assrt Token（DataProtection 加密存储）</summary>
    public string? AssrtTokenEncrypted { get; set; }

    /// <summary>Assrt API 基地址</summary>
    public string AssrtBaseUrl { get; set; } = "https://api.assrt.net";

    /// <summary>请求超时（秒）</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>是否通过代理访问（仅在系统代理总开关启用时生效）</summary>
    public bool UseProxy { get; set; } = false;
}
