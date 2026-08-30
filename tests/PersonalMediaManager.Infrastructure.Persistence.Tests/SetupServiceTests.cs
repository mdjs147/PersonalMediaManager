using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.Setup;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Persistence.Interceptors;
using PersonalMediaManager.Infrastructure.Persistence.Services.Setup;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>初始化向导服务 SetupService — 重点验证「创建管理员」幂等守门（P0 认证绕过修复回归网）</summary>
/// <remarks>
/// 安全背景：setup/admin 端点 [AllowAnonymous]，若 CreateAdminAsync 不校验「已存在 Admin」，
/// 则 setup 完成后局域网任意来源可匿名再建 Admin → 认证绕过。本测试钉死该守门。
/// </remarks>
public sealed class SetupServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly SetupService _sut;

    public SetupServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbFactory = new TestDbContextFactory(_connection);
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ctx.Database.EnsureCreated();
        IPasswordHasher hasher = Substitute.For<IPasswordHasher>();
        hasher.Hash(Arg.Any<string>()).Returns("hashed-pw");
        _sut = new SetupService(_dbFactory, hasher);
    }

    public void Dispose() => _connection.Dispose();

    [Fact(DisplayName = "创建管理员：首次空库成功建 Admin")]
    public async Task CreateAdmin_FirstTime_Succeeds()
    {
        CreateAdminResponse r = await _sut.CreateAdminAsync(new CreateAdminRequest("admin", "secret123"));

        r.Username.Should().Be("admin");
        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.UserAccounts.Count(u => u.Role == UserRole.Admin).Should().Be(1);
    }

    [Fact(DisplayName = "创建管理员：已存在 Admin 时拒绝（堵 setup 完成后匿名再建管理员后门）")]
    public async Task CreateAdmin_WhenAdminExists_Throws()
    {
        await _sut.CreateAdminAsync(new CreateAdminRequest("admin1", "secret123"));

        Func<Task> act = () => _sut.CreateAdminAsync(new CreateAdminRequest("admin2", "secret123"));

        await act.Should().ThrowAsync<BusinessException>().WithMessage("*管理员已存在*");
        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.UserAccounts.Count().Should().Be(1, "第二次创建被守门拒绝，不落库");
    }

    [Fact(DisplayName = "完成向导：无 Admin 时拒绝标记完成")]
    public async Task Complete_WithoutAdmin_Throws()
    {
        Func<Task> act = () => _sut.CompleteAsync();

        await act.Should().ThrowAsync<BusinessException>().WithMessage("*请先创建管理员*");
    }

    [Fact(DisplayName = "状态：建 Admin + 完成向导后 hasAdmin/isCompleted 反映正确")]
    public async Task Status_ReflectsProgress()
    {
        SetupStatusResponse before = await _sut.GetStatusAsync();
        before.HasAdmin.Should().BeFalse();
        before.IsCompleted.Should().BeFalse();

        await _sut.CreateAdminAsync(new CreateAdminRequest("admin", "secret123"));
        await _sut.CompleteAsync();

        SetupStatusResponse after = await _sut.GetStatusAsync();
        after.HasAdmin.Should().BeTrue();
        after.IsCompleted.Should().BeTrue();
    }

    [Fact(DisplayName = "就绪度：空库各项均为零/false")]
    public async Task Checklist_EmptyDb_AllZero()
    {
        SetupChecklistResponse c = await _sut.GetChecklistAsync();

        c.HasAdmin.Should().BeFalse();
        c.IsCompleted.Should().BeFalse();
        c.WatchFolderCount.Should().Be(0);
        c.TmdbHasApiKey.Should().BeFalse();
        c.CategoryTotalCount.Should().Be(0);
        c.CategoryUnsetCount.Should().Be(0);
        c.AiProviderCount.Should().Be(0);
    }

    [Fact(DisplayName = "就绪度：仅 TargetRoot=<UNSET> 的分类计入未配置落点数；TMDB 有 key 反映 true")]
    public async Task Checklist_ReflectsCategoryUnsetAndTmdbKey()
    {
        await using (PmmDbContext seed = _dbFactory.CreateDbContext())
        {
            seed.CategoryDefinitions.Add(new CategoryDefinition { Name = "电影", MediaType = MediaType.Movie, TargetRoot = "<UNSET>", Description = "占位未改" });
            seed.CategoryDefinitions.Add(new CategoryDefinition { Name = "动漫", MediaType = MediaType.Tv, TargetRoot = @"D:\Media\动漫", Description = "已配落点" });
            TmdbSetting tmdb = seed.TmdbSettings.First();
            tmdb.ApiKeyEncrypted = "enc-token";
            await seed.SaveChangesAsync();
        }

        SetupChecklistResponse c = await _sut.GetChecklistAsync();

        c.CategoryTotalCount.Should().Be(2);
        c.CategoryUnsetCount.Should().Be(1, "只有 TargetRoot=<UNSET> 的分类算未配置落点");
        c.TmdbHasApiKey.Should().BeTrue();
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
