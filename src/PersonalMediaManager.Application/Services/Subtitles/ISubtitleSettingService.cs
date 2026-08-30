using PersonalMediaManager.Application.Dtos.Subtitles;

namespace PersonalMediaManager.Application.Services.Subtitles;

/// <summary>字幕源配置服务契约（单例 Id=1）</summary>
/// <remarks>
/// 单例：Get 永远读 Id=1，Update 永远写 Id=1；不暴露 Create / Delete。
/// Token 走 IProtectedFieldService 加密落库；响应仅暴露 HasToken 布尔，绝不回显密文。
/// Update 的 Token 三态：null=保持不变 / 空串=清空 / 非空=加密替换（照 TMDB ApiKey 模式）。
/// Test 读库解密封 SubtitleProviderEndpoint 后走 ISubtitleProvider.TestAsync（GET /v1/user/quota），网络失败归集不抛。
/// 实现位于 Infrastructure.Persistence/Services/Subtitles（需读写设置表 + 解密 Token）。
/// </remarks>
public interface ISubtitleSettingService
{
    /// <summary>读当前字幕源配置（Token 脱敏为 HasToken）</summary>
    Task<SubtitleSettingResponse> GetAsync(CancellationToken ct = default);

    /// <summary>更新字幕源配置（Token 三态语义见 remarks）</summary>
    Task<SubtitleSettingResponse> UpdateAsync(UpdateSubtitleSettingRequest request, CancellationToken ct = default);

    /// <summary>连通性 + 配额测试（网络失败不抛，归集为 Success=false）</summary>
    Task<TestSubtitleResponse> TestAsync(CancellationToken ct = default);
}
