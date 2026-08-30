using PersonalMediaManager.Application.Contracts;

namespace PersonalMediaManager.Infrastructure.Platform.FileSystem;

/// <summary>IFileProbe 实现 — System.IO.File.Exists 薄封装</summary>
/// <remarks>无状态、线程安全，注册为单例。File.Exists 对空白 / 非法路径返回 false 而非抛异常，这里额外短路空白路径。</remarks>
internal sealed class FileProbe : IFileProbe
{
    public bool FileExists(string? path)
        => !string.IsNullOrWhiteSpace(path) && File.Exists(path);
}
