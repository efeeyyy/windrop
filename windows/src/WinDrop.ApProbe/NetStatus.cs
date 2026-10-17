using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace WinDrop.ApProbe;

internal static class NetStatus
{
    /// <summary>Wi-Fi Direct sanal arayüz adayları: "Local Area Connection*" / "Yerel Ağ Bağlantısı*" ya da 192.168.137.x.</summary>
    public static string ApInterfaces()
    {
        var found = new List<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var ip = ua.Address.ToString();
                var virt = nic.Name.Contains('*')
                    || (nic.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                        && nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
                if (virt || ip.StartsWith("192.168.137.", StringComparison.Ordinal))
                    found.Add($"{ip} ({nic.Name})");
            }
        }
        return found.Count == 0 ? "YOK" : string.Join(", ", found);
    }

    /// <summary>netsh wlan show interfaces: arayüz başına "Name / State / SSID".</summary>
    public static string WlanState()
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", "wlan show interfaces")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            var parts = new List<string>();
            string? name = null, state = null, ssid = null;
            void Flush()
            {
                if (name is not null) parts.Add($"{name}: {state ?? "?"}, SSID={ssid ?? "-"}");
                name = state = ssid = null;
            }
            foreach (var line in text.Split('\n'))
            {
                var m = Regex.Match(line, @"^\s*(Name|Ad|SSID|State|Durum)\s*:\s*(.*?)\s*$");
                if (!m.Success) continue;
                var v = m.Groups[2].Value;
                switch (m.Groups[1].Value)
                {
                    case "Name": case "Ad": Flush(); name = v; break;
                    case "State": case "Durum": state = v; break;
                    case "SSID": ssid = v; break;
                }
            }
            Flush();
            return parts.Count == 0 ? "arayüz yok" : string.Join(" | ", parts);
        }
        catch (Exception ex)
        {
            return "netsh hata: " + ex.Message;
        }
    }

    public static async Task<string> InternetAsync()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Parse("1.1.1.1"), 443, cts.Token);
            return $"VAR (1.1.1.1:443, {sw.ElapsedMilliseconds} ms)";
        }
        catch (OperationCanceledException)
        {
            return "YOK (zaman aşımı 2 sn)";
        }
        catch (Exception ex)
        {
            return "YOK (" + ex.GetType().Name + ")";
        }
    }
}
