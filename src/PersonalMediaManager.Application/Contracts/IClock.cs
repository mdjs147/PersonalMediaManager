namespace PersonalMediaManager.Application.Contracts;

/// <summary>时间提供者抽象（便于测试注入固定时间）</summary>
/// <remarks>
/// 默认实现 SystemClock 返回 DateTimeOffset.UtcNow；
/// 应用服务一律走本接口而非直接 DateTimeOffset.UtcNow，让 ParseTask 超时、AI 冷却窗口、JWT 续签判定等场景可单元测试。
/// </remarks>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>默认时钟实现</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
