using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PersonalMediaManager.Launcher;

/// <summary>局域网访问地址探测工具</summary>
/// <remarks>
/// 用途：托盘「复制访问地址」「启动气泡」需要给出可被局域网内其他设备（手机 / 平板 / 另一台电脑）
/// 直接打开的地址，而非仅本机可用的 localhost。
///
/// 探测策略（纯本地、不触网）：枚举所有网卡，仅保留满足以下全部条件的 IPv4：
///   - OperationalStatus == Up
///   - 非环回 / 非隧道 / 非虚拟网卡（VMware / VirtualBox / Hyper-V vEthernet / WSL / Docker / TAP / VPN 等按名称关键字剔除）
///   - 私有网段（RFC1918：10/8、172.16-31/12、192.168/16）—— 公网 / CGNAT / APIPA(169.254) 不计
/// 多个候选时按「网段优先级 + 物理网卡类型」评分取最高（192.168 &gt; 172.16-31 &gt; 10；以太网 / 无线加权）。
///
/// 探测不到（离线 / 仅环回）时返回 null，调用方回退 localhost，保证本机访问不受影响。
/// 不带 #if：纯 BCL 实现。
/// </remarks>
public static class LocalNetwork
{
    /// <summary>分享用访问地址：局域网 IPv4 优先，探测不到回退 localhost</summary>
    public static string BuildShareUrl(int port) =>
        $"http://{GetPreferredLanIPv4() ?? "localhost"}:{port}";

    /// <summary>首选局域网 IPv4（私有网段，排除环回 / 虚拟网卡 / APIPA）；探测不到返回 null</summary>
    public static string? GetPreferredLanIPv4()
    {
        NetworkInterface[] nics;
        try
        {
            nics = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch
        {
            // 极端环境（权限 / 平台差异）枚举失败：按"探测不到"处理，调用方回退 localhost
            return null;
        }

        string? best = null;
        int bestScore = int.MinValue;

        foreach (NetworkInterface nic in nics)
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            if (IsVirtualAdapter(nic)) continue;

            // 物理网卡类型加权：以太网 / 无线优先于其它（如 PPP / 蜂窝）
            int typeBonus = nic.NetworkInterfaceType
                is NetworkInterfaceType.Ethernet
                or NetworkInterfaceType.Wireless80211 ? 10 : 0;

            IPInterfaceProperties props;
            try
            {
                props = nic.GetIPProperties();
            }
            catch
            {
                continue;
            }

            foreach (UnicastIPAddressInformation ua in props.UnicastAddresses)
            {
                IPAddress ip = ua.Address;
                if (ip.AddressFamily != AddressFamily.InterNetwork) continue;  // 仅 IPv4
                if (IPAddress.IsLoopback(ip)) continue;

                int netScore = PrivateNetworkScore(ip.GetAddressBytes());
                if (netScore == 0) continue;  // 非私有网段不计（局域网诉求）

                int score = netScore + typeBonus;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = ip.ToString();
                }
            }
        }

        return best;
    }

    /// <summary>RFC1918 私有网段评分：192.168/16 → 3；172.16-31/12 → 2；10/8 → 1；其余 → 0（非私有）</summary>
    private static int PrivateNetworkScore(byte[] b)
    {
        if (b.Length != 4) return 0;
        if (b[0] == 192 && b[1] == 168) return 3;
        if (b[0] == 172 && b[1] is >= 16 and <= 31) return 2;
        if (b[0] == 10) return 1;
        return 0;
    }

    /// <summary>按网卡名称 / 描述关键字判定是否虚拟网卡</summary>
    /// <remarks>
    /// Hyper-V 的 vEthernet（含 WSL2 / Docker Desktop 的 NAT 网卡）NetworkInterfaceType 会上报为 Ethernet，
    /// 仅靠类型无法剔除，必须用描述关键字命中。关键字均为小写，匹配前把 Description + Name 一并转小写。
    /// </remarks>
    private static bool IsVirtualAdapter(NetworkInterface nic)
    {
        string text = ((nic.Description ?? string.Empty) + " " + (nic.Name ?? string.Empty)).ToLowerInvariant();
        foreach (string kw in VirtualKeywords)
        {
            if (text.Contains(kw, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static readonly string[] VirtualKeywords =
    [
        "virtual", "vmware", "virtualbox", "hyper-v", "vethernet",
        "wsl", "docker", "loopback", "pseudo", "tunnel",
        "tap-windows", "tap adapter", "vpn", "bluetooth",
    ];
}
