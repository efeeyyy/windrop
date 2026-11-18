using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Windows.Networking.Connectivity;

namespace WinDrop.Platform;

/// <summary>Yerel adres adayı (PROTOCOL §7.1 <c>addr</c> için).</summary>
public sealed record CandidateAddress(IPAddress Address, string InterfaceName, bool IsWifi);

public static class NetworkInfo
{
    /// <summary>
    /// Up arayüzlerdeki adaylar: loopback/link-local hariç IPv4 + global IPv6 (ULA, site-local, Teredo hariç).
    /// Sıra: Wi-Fi arayüzleri önce, sonra diğerleri; her arayüzde IPv4 önce, sonra IPv6.
    /// Tünel arayüzleri atlanır.
    /// </summary>
    public static IReadOnlyList<CandidateAddress> GetCandidateAddresses()
    {
        var result = new List<(int Rank, CandidateAddress Addr)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            bool wifi = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                var ip = ua.Address;
                if (!IsCandidate(ip)) continue;
                if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ua.DuplicateAddressDetectionState != DuplicateAddressDetectionState.Preferred)
                    continue;
                int rank = (wifi ? 0 : 10) + (ip.AddressFamily == AddressFamily.InterNetwork ? 0 : 1);
                result.Add((rank, new CandidateAddress(ip, nic.Name, wifi)));
            }
        }
        return result.OrderBy(r => r.Rank).Select(r => r.Addr).ToList();
    }

    /// <summary>Adres aday olabilir mi (arayüzden bağımsız kural).</summary>
    public static bool IsCandidate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 169 && b[1] == 254) return false; // link-local
            if (b[0] == 0 || b[0] >= 224) return false;   // belirsiz / multicast / ayrılmış
            return true;
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6UniqueLocal || ip.IsIPv6Teredo)
                return false;
            if (ip.Equals(IPAddress.IPv6None) || ip.Equals(IPAddress.IPv6Any)) return false;
            return true;
        }
        return false;
    }

    /// <summary>Basit internet denetimi: Windows bağlantı profili InternetAccess diyorsa true.</summary>
    public static bool HasInternet()
    {
        try
        {
            var p = NetworkInformation.GetInternetConnectionProfile();
            return p != null && p.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
