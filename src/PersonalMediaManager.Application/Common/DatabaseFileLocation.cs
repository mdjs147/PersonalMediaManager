namespace PersonalMediaManager.Application.Common;

/// <summary>运行时生效的 SQLite 主库物理路径（override 感知）</summary>
/// <remarks>
/// 由 Host 装配期从最终连接串（ConnectionStrings:Default，可被 local.json 覆盖）解析后注册为单例。
/// 与 [[AppPaths]].DbFile（仅默认路径、不感知 override）区分：导出 / 导入 / 容量统计都应以本类型为准。
/// 否则用户用托盘「配置数据库位置…」改库后，导出会读错库、导入会写错库（暂存包落在别处而应用读不到），
/// 同样表现为「导入没作用」。
/// </remarks>
public sealed class DatabaseFileLocation
{
    /// <summary>主库 .db 绝对路径</summary>
    public string Path { get; }

    public DatabaseFileLocation(string path) => Path = path;
}
