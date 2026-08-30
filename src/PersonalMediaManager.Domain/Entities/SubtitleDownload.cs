using PersonalMediaManager.Domain.Common;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Domain.Entities;

/// <summary>字幕下载成功记录（Subtitle_Download）</summary>
/// <remarks>
/// 成功下载日志：用户从外部字幕源手动搜索并下载字幕、落地到归档媒体旁后写入一行（仅 Add，无逐行 Update）。
/// 生命周期锚定「归档存在」——FileName/TargetPath 指向归档位旁的字幕落点：
/// · CASCADE 关联 Media_Item：删除媒体处理记录时一并清；
/// · 撤销归档 / 退回重改解除归档时（HistoryService）一并清——否则字幕随归档解除被移回源位 / 随副本删除，
///   记录的 FileName/TargetPath 必然悬空。
/// </remarks>
public sealed class SubtitleDownload : Entity
{
    /// <summary>关联 Media_Item 主键</summary>
    public long MediaItemId { get; set; }

    /// <summary>字幕源</summary>
    public SubtitleProviderType Provider { get; set; }

    /// <summary>字幕站候选名</summary>
    public string? SubtitleName { get; set; }

    /// <summary>落地文件名</summary>
    public string FileName { get; set; } = default!;

    /// <summary>落地完整路径</summary>
    public string TargetPath { get; set; } = default!;

    /// <summary>识别出的语言码（如 zh / zh-TW / en / und）</summary>
    public string Language { get; set; } = default!;

    /// <summary>文件大小（字节）</summary>
    public long FileSize { get; set; }
}
