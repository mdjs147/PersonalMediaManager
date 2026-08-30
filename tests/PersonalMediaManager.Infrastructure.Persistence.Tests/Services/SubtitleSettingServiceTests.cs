using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.Subtitles;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Persistence.Interceptors;
using PersonalMediaManager.Infrastructure.Persistence.Services.Subtitles;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests.Services;

/// <summary>SubtitleSettingService — Get 脱敏 / Update Token 三态 / Test 委托 registry（照 TmdbSettingService 模式）</summary>
public sealed class SubtitleSettingServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly IProtectedFieldService _protector = Substitute.For<IProtectedFieldService>();
    private readonly ISubtitleProviderRegistry _registry = Substitute.For<ISubtitleProviderRegistry>();
    private readonly ISubtitleProvider _provider = Substitute.For<ISubtitleProvider>();
    private readonly SubtitleSettingService _sut;

    public SubtitleSettingServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbFactory = new TestDbContextFactory(_connection);
        using (PmmDbContext ctx = _dbFactory.CreateDbContext())
        {
            ctx.Database.EnsureCreated(); // Subtitle_Setting Id=1 种子由 HasData 注入
        }

        // 加解密桩：Protect 前缀 enc:、Unprotect 去前缀（可逆，便于断言密文与明文）
        _protector.Protect(Arg.Any<string>()).Returns(ci => "enc:" + ci.Arg<string>());
        _protector.Unprotect(Arg.Any<string>()).Returns(ci => ci.Arg<string>()["enc:".Length..]);
        _registry.Resolve(SubtitleProviderType.Assrt).Returns(_provider);

        _sut = new SubtitleSettingService(_dbFactory, _protector, _registry);
    }

    public void Dispose() => _connection.Dispose();

    /// <summary>构造合法更新请求（默认与种子一致）</summary>
    private static UpdateSubtitleSettingRequest BuildRequest(
        string? token = null,
        string baseUrl = "https://api.assrt.net",
        int timeoutSeconds = 30,
        bool useProxy = false) =>
        new(token, baseUrl, timeoutSeconds, useProxy);

    private SubtitleSetting ReadEntity()
    {
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        return ctx.SubtitleSettings.AsNoTracking().Single(s => s.Id == 1);
    }

    // ---------- Get 脱敏 ----------

    [Fact(DisplayName = "Get：种子默认未配 Token → hasToken=false，其余字段回传种子值")]
    public async Task Get_Default_HasTokenFalse()
    {
        SubtitleSettingResponse resp = await _sut.GetAsync();

        resp.HasToken.Should().BeFalse("种子行 AssrtTokenEncrypted 为 null");
        resp.BaseUrl.Should().Be("https://api.assrt.net");
        resp.TimeoutSeconds.Should().Be(30);
        resp.UseProxy.Should().BeFalse();
    }

    [Fact(DisplayName = "Get：已配 Token → hasToken=true 且绝不回显密文")]
    public async Task Get_WithToken_HasTokenTrue()
    {
        await _sut.UpdateAsync(BuildRequest(token: "tok-1"));

        SubtitleSettingResponse resp = await _sut.GetAsync();

        resp.HasToken.Should().BeTrue();
        // 响应 DTO 只有 HasToken 布尔位，无任何密文 / 明文字段（编译期已保证），此处守护布尔翻转即可
    }

    // ---------- Update Token 三态 ----------

    [Fact(DisplayName = "Update：非空 Token → Protect 被调且密文落库")]
    public async Task Update_NonEmptyToken_ProtectsAndStores()
    {
        SubtitleSettingResponse resp = await _sut.UpdateAsync(BuildRequest(token: "tok-1", timeoutSeconds: 60, useProxy: true));

        _protector.Received(1).Protect("tok-1");
        resp.HasToken.Should().BeTrue();
        resp.TimeoutSeconds.Should().Be(60);
        resp.UseProxy.Should().BeTrue();
        ReadEntity().AssrtTokenEncrypted.Should().Be("enc:tok-1", "落库的必须是 Protect 后密文");
    }

    [Fact(DisplayName = "Update：Token=null → 保持不变（其余字段照常更新）")]
    public async Task Update_NullToken_KeepsExisting()
    {
        await _sut.UpdateAsync(BuildRequest(token: "tok-1"));

        SubtitleSettingResponse resp = await _sut.UpdateAsync(BuildRequest(token: null, baseUrl: "https://mirror.assrt.net"));

        resp.HasToken.Should().BeTrue("null 表示不动 Token");
        resp.BaseUrl.Should().Be("https://mirror.assrt.net");
        ReadEntity().AssrtTokenEncrypted.Should().Be("enc:tok-1", "密文不应被 null 覆盖");
        _protector.Received(1).Protect(Arg.Any<string>()); // 仅第一次 Update 调过
    }

    [Fact(DisplayName = "Update：Token=空串 → 清空")]
    public async Task Update_EmptyToken_Clears()
    {
        await _sut.UpdateAsync(BuildRequest(token: "tok-1"));

        SubtitleSettingResponse resp = await _sut.UpdateAsync(BuildRequest(token: ""));

        resp.HasToken.Should().BeFalse();
        ReadEntity().AssrtTokenEncrypted.Should().BeNull("空串语义 = 清空");
    }

    // ---------- Test ----------

    [Fact(DisplayName = "Test：未配 Token → 抛 BusinessException")]
    public async Task Test_WithoutToken_Throws()
    {
        Func<Task> act = () => _sut.TestAsync();

        await act.Should().ThrowAsync<BusinessException>().WithMessage("尚未配置 Assrt Token");
        await _provider.DidNotReceiveWithAnyArgs().TestAsync(default!, default);
    }

    [Fact(DisplayName = "Test：委托 registry 策略并映射结果（Endpoint 携带解密 Token 与当前配置）")]
    public async Task Test_DelegatesToRegistry_AndMapsResult()
    {
        await _sut.UpdateAsync(BuildRequest(token: "tok-1", baseUrl: "https://mirror.assrt.net", timeoutSeconds: 20, useProxy: true));
        SubtitleProviderEndpoint? captured = null;
        _provider.TestAsync(Arg.Do<SubtitleProviderEndpoint>(e => captured = e), Arg.Any<CancellationToken>())
            .Returns(new SubtitleProviderTestResult(Success: false, Quota: 42, ElapsedMilliseconds: 120, ErrorMessage: "配额不足"));

        TestSubtitleResponse resp = await _sut.TestAsync();

        resp.Success.Should().BeFalse("网络 / 鉴权失败归集为 Success=false，不抛异常");
        resp.Quota.Should().Be(42);
        resp.ElapsedMilliseconds.Should().Be(120);
        resp.ErrorMessage.Should().Be("配额不足");
        captured.Should().NotBeNull();
        SubtitleProviderEndpoint endpoint = captured!;
        endpoint.Token.Should().Be("tok-1", "Endpoint 必须携带 Unprotect 后明文");
        endpoint.BaseUrl.Should().Be("https://mirror.assrt.net");
        endpoint.TimeoutSeconds.Should().Be(20);
        endpoint.UseProxy.Should().BeTrue();
    }

    private sealed class TestDbContextFactory : IDbContextFactory<PmmDbContext>
    {
        private readonly SqliteConnection _connection;
        public TestDbContextFactory(SqliteConnection c) { _connection = c; }
        public PmmDbContext CreateDbContext()
        {
            DbContextOptionsBuilder<PmmDbContext> opts = new();
            opts.UseSqlite(_connection);
            opts.AddInterceptors(new TimestampInterceptor(), new RowVersionInterceptor());
            return new PmmDbContext(opts.Options);
        }
    }
}
