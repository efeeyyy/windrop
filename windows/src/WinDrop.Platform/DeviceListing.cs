namespace WinDrop.Platform;

/// <summary>Cihaz listesi yardımcıları: aynı telefonun eski kimliğinin yinelenen kaydını gizler.</summary>
public static class DeviceListing
{
    /// <summary>
    /// Aynı ada ve aynı son BLE kimliğine sahip kayıtlar tek telefonun iki anahtarıdır. Grupta yakında (aktif) olan varsa
    /// yalnız o gösterilir; yoksa en son görülen kalır. Yakında olan kayıt asla gizlenmez. Gizlenecek kayıtların açık anahtarlarını döner.
    /// Kayıtlar silinmez; ayarlardaki tam liste hepsini gösterir.
    /// </summary>
    public static HashSet<string> SupersededKeys(IEnumerable<TrustedDevice> devices, Func<TrustedDevice, bool> isNearby)
    {
        var hidden = new HashSet<string>();
        foreach (var group in devices.Where(d => d.LastBleId is not null)
                     .GroupBy(d => (d.LastBleId, Name: d.Name.ToUpperInvariant())))
        {
            var members = group.ToList();
            if (members.Count < 2) continue;
            var nearby = members.Where(isNearby).ToList();
            var keep = nearby.Count > 0
                ? nearby
                : [members.OrderByDescending(d => d.LastSeen ?? d.PairedAt).First()];
            foreach (var d in members)
                if (!keep.Contains(d)) hidden.Add(Convert.ToBase64String(d.PublicKey));
        }
        return hidden;
    }
}
