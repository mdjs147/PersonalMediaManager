using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Nodes;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Host.Composition;

namespace PersonalMediaManager.Launcher;

/// <summary>local.json 覆盖层读写工具</summary>
/// <remarks>
/// 详见 docs/需求规范-启动方式与托盘常驻.md §3：
/// - 路径：AppPaths.Root\local.json（数据根优先 exe 旁 data\，不可写回退 %LocalAppData%\PersonalMediaManager）
/// - 仅 2 项 pre-boot override：Web:Port、ConnectionStrings:Default
/// - 无 DI 依赖（Tray 无 DI 上下文也能用）；IO/JSON 异常静默吞；JSON 损坏覆盖式自愈
/// - 清空策略：清字段后所在节空则删节；整文件空则删文件（保持目录干净）
/// 设计：static class + 显式 AppPaths 入参（避免静态字段持有路径导致测试串扰）
/// </remarks>
public static class LocalConfigStore
{
    public const string FileName = "local.json";

    /// <summary>local.json 绝对路径</summary>
    public static string GetFilePath(AppPaths paths) => Path.Combine(paths.Root, FileName);

    // ============ 端口 override ============

    public static int? ReadOverridePort(AppPaths paths)
    {
        JsonObject? root = TryLoad(paths);
        if (root?["Web"] is JsonObject web && web["Port"] is JsonValue v && v.TryGetValue(out int port))
        {
            return port;
        }
        return null;
    }

    public static void SetOverridePort(AppPaths paths, int port)
    {
        JsonObject root = TryLoad(paths) ?? new JsonObject();
        JsonObject web = root["Web"] as JsonObject ?? new JsonObject();
        web["Port"] = port;
        root["Web"] = web;
        TrySave(paths, root);
    }

    public static void ClearOverridePort(AppPaths paths)
    {
        JsonObject? root = TryLoad(paths);
        if (root is null) return;
        if (root["Web"] is not JsonObject web || !web.ContainsKey("Port")) return;

        web.Remove("Port");
        if (web.Count == 0) root.Remove("Web");
        TrySaveOrDelete(paths, root);
    }

    // ============ 数据库路径 override ============

    public static string? ReadOverrideDbPath(AppPaths paths)
    {
        JsonObject? root = TryLoad(paths);
        if (root?["ConnectionStrings"] is JsonObject conn
            && conn["Default"] is JsonValue v
            && v.TryGetValue(out string? cs)
            && cs is not null)
        {
            return ParseDataSource(cs);
        }
        return null;
    }

    public static void SetOverrideDbPath(AppPaths paths, string dbPath)
    {
        JsonObject root = TryLoad(paths) ?? new JsonObject();
        JsonObject conn = root["ConnectionStrings"] as JsonObject ?? new JsonObject();
        conn["Default"] = $"Data Source={dbPath}";
        root["ConnectionStrings"] = conn;
        TrySave(paths, root);
    }

    public static void ClearOverrideDbPath(AppPaths paths)
    {
        JsonObject? root = TryLoad(paths);
        if (root is null) return;
        if (root["ConnectionStrings"] is not JsonObject conn || !conn.ContainsKey("Default")) return;

        conn.Remove("Default");
        if (conn.Count == 0) root.Remove("ConnectionStrings");
        TrySaveOrDelete(paths, root);
    }

    // ============ 端口解析（含兜底）============

    /// <summary>有效监听端口：优先 local.json override，回退 PmmHost.DefaultPort（7288）</summary>
    public static int ResolveEffectivePort(AppPaths paths) =>
        ReadOverridePort(paths) ?? PmmHost.DefaultPort;

    // ============ 升级气泡去重 ============

    /// <summary>读上次气泡过的版本号（升级检查 D9.1：同版本不重复弹气泡）</summary>
    public static string? ReadLastBalloonedVersion(AppPaths paths)
    {
        JsonObject? root = TryLoad(paths);
        if (root?["Update"] is JsonObject upd
            && upd["LastBalloonedVersion"] is JsonValue v
            && v.TryGetValue(out string? s))
        {
            return s;
        }
        return null;
    }

    /// <summary>记录本次已气泡的版本号；下次同版本不再弹</summary>
    public static void SetLastBalloonedVersion(AppPaths paths, string version)
    {
        JsonObject root = TryLoad(paths) ?? new JsonObject();
        JsonObject upd = root["Update"] as JsonObject ?? new JsonObject();
        upd["LastBalloonedVersion"] = version;
        root["Update"] = upd;
        TrySave(paths, root);
    }

    // ============ 校验 ============

    public static bool TryValidatePort(int candidate, out string? error)
    {
        if (candidate is < 1 or > 65535)
        {
            error = "端口必须在 1-65535 范围内";
            return false;
        }
        try
        {
            IPGlobalProperties props = IPGlobalProperties.GetIPGlobalProperties();
            if (props.GetActiveTcpListeners().Any(ep => ep.Port == candidate))
            {
                error = $"端口 {candidate} 已被其他进程监听";
                return false;
            }
        }
        catch
        {
            // GetActiveTcpListeners 偶发失败（系统调用限速 / 权限），按"未检测到占用"处理而不阻断
        }
        error = null;
        return true;
    }

    public static bool TryValidateDbPath(string candidate, out string? error)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            error = "数据库路径不能为空";
            return false;
        }
        if (!Path.IsPathFullyQualified(candidate))
        {
            error = "必须是绝对路径";
            return false;
        }

        string? parentDir = Path.GetDirectoryName(candidate);
        if (string.IsNullOrEmpty(parentDir) || !Directory.Exists(parentDir))
        {
            error = $"父目录不存在：{parentDir}";
            return false;
        }

        // 探测父目录可写：写一个唯一名 probe 再删，提前暴露权限问题
        try
        {
            string probe = Path.Combine(parentDir, $".pmm-write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            error = $"父目录不可写：{ex.Message}";
            return false;
        }

        error = null;
        return true;
    }

    // ============ 内部工具 ============

    private static JsonObject? TryLoad(AppPaths paths)
    {
        string path = GetFilePath(paths);
        try
        {
            if (!File.Exists(path)) return null;
            string text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return null;
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            // JSON 损坏：返回 null 让后续 setter 覆盖式重写（自愈）
            return null;
        }
        catch
        {
            // IO / UnauthorizedAccess / SecurityException 等：按"无覆盖"处理，绝不阻断启动
            return null;
        }
    }

    private static void TrySave(AppPaths paths, JsonObject root)
    {
        try
        {
            string path = GetFilePath(paths);
            string text = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, text);
        }
        catch
        {
            // 写入失败静默吞
        }
    }

    private static void TrySaveOrDelete(AppPaths paths, JsonObject root)
    {
        if (root.Count == 0)
        {
            // 整文件空：删文件保持目录干净
            try
            {
                string path = GetFilePath(paths);
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // 删除失败留着也无害（下次 setter 会覆盖写）
            }
        }
        else
        {
            TrySave(paths, root);
        }
    }

    /// <summary>从 "Data Source=path" 连接串解出 path；兼容纯路径输入</summary>
    private static string ParseDataSource(string connectionString)
    {
        const string prefix = "Data Source=";
        if (connectionString.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            string raw = connectionString[prefix.Length..];
            // 兼容多分号场景（"Data Source=a;Cache=Shared"）：截到第一个 ;
            int semi = raw.IndexOf(';');
            string head = semi >= 0 ? raw[..semi] : raw;
            return head.Trim().Trim('"');
        }
        return connectionString;
    }
}
