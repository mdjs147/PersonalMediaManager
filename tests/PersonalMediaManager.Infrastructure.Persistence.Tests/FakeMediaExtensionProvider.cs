using PersonalMediaManager.Application.Contracts;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>IMediaExtensionProvider 测试替身：固定返回内置视频扩展名白名单（无 I/O）</summary>
/// <remarks>
/// FileIntakeService / ScanService 依赖此契约判断「是否视频」。测试用真实行为替身（非 mock），
/// 扩展名集合与生产编译期内置白名单一致，IsVideo 按集合判断，避免每个测试单独配置 stub。
/// </remarks>
internal sealed class FakeMediaExtensionProvider : IMediaExtensionProvider
{
    private static readonly IReadOnlySet<string> VideoExtensions = new HashSet<string>(
        new[] { ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".ts", ".mpg", ".mpeg", ".m2ts" },
        StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> GetEnabledExtensions() => VideoExtensions;

    public bool IsVideo(string fileNameOrPath) =>
        VideoExtensions.Contains(Path.GetExtension(fileNameOrPath));

    public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
}
