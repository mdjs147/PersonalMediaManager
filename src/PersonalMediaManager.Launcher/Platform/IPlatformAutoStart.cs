namespace PersonalMediaManager.Launcher.Platform;

/// <summary>开机自启抽象（隔离 OS 专属 API，便于单测）</summary>
/// <remarks>
/// Windows 实现：HKCU\Software\Microsoft\Windows\CurrentVersion\Run 写注册表值（无需管理员权限）
/// 三方法均要求幂等（重复 Enable / Disable 不抛异常）
/// </remarks>
public interface IPlatformAutoStart
{
    /// <summary>当前是否已注册开机自启</summary>
    bool IsEnabled();

    /// <summary>启用开机自启（幂等，已存在直接返回成功）</summary>
    void Enable();

    /// <summary>禁用开机自启（幂等，不存在直接返回成功）</summary>
    void Disable();
}
