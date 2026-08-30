using PersonalMediaManager.Domain.Common;

namespace PersonalMediaManager.Domain.Entities;

/// <summary>TMDB 配置（Tmdb_Setting，独表单例 Id=1）</summary>
/// <remarks>
/// ApiKeyEncrypted 走 DataProtection；应用层强制 Id = 1，EF 配置 HasData 注入种子（A4.7）。
/// 综合打分权重总和约定为 1.0（前端表单校验）；候选阈值 N=3 触发 AI 兜底（需求文档 §3.4）。
/// </remarks>
public sealed class TmdbSetting : Entity
{
    /// <summary>API Key（DataProtection 加密存储）</summary>
    public string? ApiKeyEncrypted { get; set; }

    /// <summary>主语言</summary>
    public string Language { get; set; } = "zh-CN";

    /// <summary>回退语言（中文为空时使用）</summary>
    public string FallbackLanguage { get; set; } = "en-US";

    /// <summary>候选阈值 N（&gt; N 触发 AI 兜底）</summary>
    public int CandidateThreshold { get; set; } = 3;

    /// <summary>Polly 节流速率（req/s）</summary>
    public int RateLimitPerSecond { get; set; } = 40;

    /// <summary>元数据缓存 TTL（小时）</summary>
    public int MetadataCacheHours { get; set; } = 24;

    /// <summary>搜索缓存 TTL（分钟）</summary>
    public int SearchCacheMinutes { get; set; } = 60;

    public double ScoreWeightTitle { get; set; } = 0.5;
    public double ScoreWeightYear { get; set; } = 0.3;
    public double ScoreWeightPopularity { get; set; } = 0.1;
    public double ScoreWeightLanguage { get; set; } = 0.1;
}
