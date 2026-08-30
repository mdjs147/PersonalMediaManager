namespace PersonalMediaManager.Application.Contracts;

/// <summary>文件系统只读探测契约</summary>
/// <remarks>
/// 仅用于判定路径状态（当前只需「文件是否存在」），与 <see cref="IFileMover"/> 的「移动 / 复制」写动作职责分离。
/// 实现位于 Infrastructure.Platform/FileSystem/FileProbe.cs，薄封装 System.IO.File.Exists；
/// 抽成契约便于待确认队列「文件检查」等用例在单测中 Mock 文件存在性，不触真实磁盘。
/// </remarks>
public interface IFileProbe
{
    /// <summary>判定文件是否存在（仅文件，不含目录；路径为空白一律视为不存在）</summary>
    bool FileExists(string? path);
}
