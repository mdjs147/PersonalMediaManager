using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.Subtitles;
using PersonalMediaManager.Application.Services.Subtitles;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Subtitles;

/// <summary>字幕源配置服务实现（单例 Id=1）</summary>
/// <remarks>
/// 种子：Migration HasData 注入 Id=1 一条记录，所以 GetAsync 正常情况下永远能拿到非 null 实体。
/// Update：始终写 Id=1；Token 三态 null=不变 / ""=清空 / 非空=Protect（照 TmdbSettingService.ApiKey 模式）。
/// Test：读 Id=1 → 解密 Token → 封 SubtitleProviderEndpoint → 委托 ISubtitleProvider.TestAsync
/// （契约约定网络失败归集为 Success=false 不抛，ErrorMessage 原样透传给前端）。
/// </remarks>
internal sealed class SubtitleSettingService : ISubtitleSettingService
{
    private const long SingletonId = 1;

    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly IProtectedFieldService _protector;
    private readonly ISubtitleProviderRegistry _registry;

    public SubtitleSettingService(
        IDbContextFactory<PmmDbContext> dbFactory,
        IProtectedFieldService protector,
        ISubtitleProviderRegistry registry)
    {
        _dbFactory = dbFactory;
        _protector = protector;
        _registry = registry;
    }

    public async Task<SubtitleSettingResponse> GetAsync(CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        SubtitleSetting entity = await LoadOrThrowAsync(ctx, ct);
        return ToResponse(entity);
    }

    public async Task<SubtitleSettingResponse> UpdateAsync(UpdateSubtitleSettingRequest request, CancellationToken ct = default)
    {
        // BaseUrl 非空 / http(s) 绝对地址 / 长度、TimeoutSeconds 区间等 A 类校验已由 DTO 注解在模型绑定阶段承接
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        SubtitleSetting entity = await LoadOrThrowAsync(ctx, ct);

        // Token 三态：null=保持不变 / 空串=清空 / 非空=加密替换（照 TMDB ApiKey 模式）
        if (request.Token is not null)
            entity.AssrtTokenEncrypted = request.Token.Length == 0 ? null : _protector.Protect(request.Token);
        entity.AssrtBaseUrl = request.BaseUrl.Trim();
        entity.TimeoutSeconds = request.TimeoutSeconds;
        entity.UseProxy = request.UseProxy;

        await ctx.SaveChangesAsync(ct);
        return ToResponse(entity);
    }

    public async Task<TestSubtitleResponse> TestAsync(CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        SubtitleSetting entity = await LoadOrThrowAsync(ctx, ct);
        if (string.IsNullOrEmpty(entity.AssrtTokenEncrypted))
            throw new BusinessException("尚未配置 Assrt Token");

        string token = _protector.Unprotect(entity.AssrtTokenEncrypted);
        SubtitleProviderEndpoint endpoint = new(token, entity.AssrtBaseUrl, entity.TimeoutSeconds, entity.UseProxy);
        // 契约：TestAsync 网络 / 鉴权失败不抛，归集为 Success=false + ErrorMessage（照 TmdbConnectivityTester）
        SubtitleProviderTestResult result = await _registry.Resolve(SubtitleProviderType.Assrt).TestAsync(endpoint, ct);
        return new TestSubtitleResponse(result.Success, result.Quota, result.ElapsedMilliseconds, result.ErrorMessage);
    }

    /// <summary>读单例行（缺行 = Migration 种子缺失，抛业务异常）</summary>
    private static async Task<SubtitleSetting> LoadOrThrowAsync(PmmDbContext ctx, CancellationToken ct)
    {
        SubtitleSetting? entity = await ctx.SubtitleSettings.FirstOrDefaultAsync(s => s.Id == SingletonId, ct);
        if (entity is null)
            throw new BusinessException("字幕配置单例（Id=1）缺失，请检查数据库 Migration 种子");
        return entity;
    }

    /// <summary>脱敏映射：Token 仅暴露 HasToken 布尔，绝不回显密文</summary>
    private static SubtitleSettingResponse ToResponse(SubtitleSetting s) =>
        new(!string.IsNullOrEmpty(s.AssrtTokenEncrypted), s.AssrtBaseUrl, s.TimeoutSeconds, s.UseProxy);
}
