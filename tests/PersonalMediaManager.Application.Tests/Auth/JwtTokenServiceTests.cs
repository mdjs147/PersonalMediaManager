using System.IdentityModel.Tokens.Jwt;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Platform.Security;

namespace PersonalMediaManager.Application.Tests.Auth;

/// <summary>JwtTokenService 单元测试：生成 / 解析 / 续签判定</summary>
/// <remarks>
/// 严格说本测试跨 Application + Infrastructure.Platform，但放 Application.Tests 体现「契约视角」；
/// Persistence.Tests 不涉及 JWT；Host.Tests 走全链路，这里仅验证算法层。
/// </remarks>
public sealed class JwtTokenServiceTests
{
    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class StubKeyProvider : IJwtSigningKeyProvider
    {
        public string GetSigningKey() => Convert.ToBase64String(new byte[32]); // 32 字节 = 256 位
    }

    [Fact]
    public void Generate_TokenContains_RequiredClaims()
    {
        DateTimeOffset now = new(2026, 5, 16, 12, 0, 0, TimeSpan.Zero);
        JwtTokenService svc = new(new StubKeyProvider(), new FixedClock(now));

        string token = svc.Generate(userId: 42, username: "alice", role: UserRole.Admin);

        JwtSecurityTokenHandler handler = new();
        JwtSecurityToken parsed = handler.ReadJwtToken(token);
        parsed.Claims.Should().Contain(c => c.Type == "userId" && c.Value == "42");
        parsed.Claims.Should().Contain(c => c.Type == "username" && c.Value == "alice");
        parsed.Claims.Should().Contain(c => c.Type == "role" && c.Value == "Admin");
        parsed.ValidTo.Should().BeCloseTo((now + JwtTokenService.TokenLifetime).UtcDateTime, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void ShouldRefresh_WithinThreshold_ReturnsTrue()
    {
        DateTimeOffset now = new(2026, 5, 16, 12, 0, 0, TimeSpan.Zero);
        JwtTokenService svc = new(new StubKeyProvider(), new FixedClock(now));

        // 剩余有效期小于续签阈值（RefreshThreshold）→ 应滑动续签；用常量派生，阈值调整时测试自适应
        DateTimeOffset expiresWithinThreshold = now + JwtTokenService.RefreshThreshold - TimeSpan.FromHours(1);
        svc.ShouldRefresh(expiresWithinThreshold).Should().BeTrue();
    }

    [Fact]
    public void ShouldRefresh_BeyondThreshold_ReturnsFalse()
    {
        DateTimeOffset now = new(2026, 5, 16, 12, 0, 0, TimeSpan.Zero);
        JwtTokenService svc = new(new StubKeyProvider(), new FixedClock(now));

        // 剩余有效期大于续签阈值 → 不续签
        DateTimeOffset expiresBeyondThreshold = now + JwtTokenService.RefreshThreshold + TimeSpan.FromDays(1);
        svc.ShouldRefresh(expiresBeyondThreshold).Should().BeFalse();
    }
}
