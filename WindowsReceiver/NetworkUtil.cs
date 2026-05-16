using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PhoneShareReceiver;

public static class NetworkUtil
{
    private static readonly string[] VirtualKeywords =
    {
        "virtual", "vmware", "hyper-v", "vbox", "virtualbox", "wsl",
        "vpn", "tun", "tap", "wintun", "wireguard", "tailscale", "zerotier",
        "radmin", "clash", "mihomo", "nekoray", "openvpn", "bluetooth",
        "loopback", "pseudo"
    };

    public static List<string> GetLocalUrls(int port)
    {
        var candidates = new List<(int score, string url)>();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (IsLikelyVirtualAdapter(ni)) continue;

            var props = ni.GetIPProperties();
            var hasGateway = props.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(g.Address) &&
                g.Address.ToString() != "0.0.0.0");

            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(ua.Address)) continue;

                var ip = ua.Address.ToString();
                if (ip.StartsWith("169.254.")) continue;

                // 主流程只放 RFC1918 局域网地址，避免把 Radmin/VPN/TUN 这类 26.x/100.x 虚拟地址写进二维码。
                if (!IsPrivateIPv4(ip)) continue;

                var score = 0;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) score -= 30;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet) score -= 20;
                if (hasGateway) score -= 10;
                if (ip.StartsWith("192.168.")) score -= 3;
                if (ip.StartsWith("10.")) score -= 2;
                if (ip.StartsWith("172.")) score -= 1;

                candidates.Add((score, $"http://{ip}:{port}"));
            }
        }

        var urls = candidates
            .OrderBy(x => x.score)
            .Select(x => x.url)
            .Distinct()
            .ToList();

        if (urls.Count == 0)
            urls.Add($"http://127.0.0.1:{port}");

        return urls;
    }

    private static bool IsLikelyVirtualAdapter(NetworkInterface ni)
    {
        var text = (ni.Name + " " + ni.Description).ToLowerInvariant();
        return VirtualKeywords.Any(k => text.Contains(k));
    }

    private static bool IsPrivateIPv4(string ip)
    {
        var parts = ip.Split('.');
        if (parts.Length != 4) return false;
        if (!int.TryParse(parts[0], out var a)) return false;
        if (!int.TryParse(parts[1], out var b)) return false;

        if (a == 10) return true;
        if (a == 192 && b == 168) return true;
        if (a == 172 && b >= 16 && b <= 31) return true;
        return false;
    }
}
