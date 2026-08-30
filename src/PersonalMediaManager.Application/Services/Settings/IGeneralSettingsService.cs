using PersonalMediaManager.Application.Dtos.Settings;

namespace PersonalMediaManager.Application.Services.Settings;

/// <summary>通用设置服务契约（System_Setting 表的 KV CRUD，按 Category 聚合）</summary>
public interface IGeneralSettingsService
{
    Task<GroupedSettingsResponse> ListAsync(CancellationToken ct = default);

    Task UpdateAsync(UpdateGeneralRequest req, CancellationToken ct = default);

    /// <summary>自检 ffmpeg / ffprobe 路径是否可用（解析 Audio_FfmpegPath → 跑 -version）</summary>
    Task<TestFfmpegResponse> TestFfmpegAsync(TestFfmpegRequest req, CancellationToken ct = default);
}
