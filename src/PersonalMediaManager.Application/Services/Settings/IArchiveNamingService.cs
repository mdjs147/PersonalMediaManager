using PersonalMediaManager.Application.Dtos.Settings;

namespace PersonalMediaManager.Application.Services.Settings;

/// <summary>归档命名模板设置服务契约（读 / 写 4 个 Archive_ key + 草稿预览）</summary>
/// <remarks>
/// 写入前用 NamingTemplateRenderer.ValidateTemplate 校验三模板（非法抛 BusinessException）；
/// 预览复用与实际归档相同的渲染管道（PlexNamingConventions），保证「预览 = 真实落点形态」。
/// </remarks>
public interface IArchiveNamingService
{
    /// <summary>读当前 4 个模板配置（缺失项回退默认）</summary>
    Task<ArchiveNamingResponse> GetAsync(CancellationToken ct = default);

    /// <summary>更新模板配置（三模板经 ValidateTemplate 校验，非法抛 1000）</summary>
    Task UpdateAsync(UpdateArchiveNamingRequest req, CancellationToken ct = default);

    /// <summary>用固定示例预览草稿模板渲染结果 + 返回各模板校验错误（不落库、不抛）</summary>
    ArchiveNamingPreviewResponse Preview(ArchiveNamingPreviewRequest req);
}
