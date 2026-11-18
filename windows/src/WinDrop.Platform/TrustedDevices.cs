using System.Security.Cryptography;
using System.Text.Json;
using WinDrop.Core.Protocol;

namespace WinDrop.Platform;

/// <summary>
/// Güvenilen cihaz. <see cref="Name"/> eşleştirme/ilk kabul anındaki addır; arayüz hep bunu gösterir.
/// <see cref="IsFamiliar"/>: QR ile eşleştirilmedi, PROTOCOL §3.1 "yakındaki herkes" modunda alıcı onayıyla hatırlandı
/// (sonraki bağlantılar IK; otomatik kabul <b>yok</b>). false = QR ile eşli.
/// <see cref="LastBleId"/>/<see cref="LastSeen"/>: en son bağlanılan BLE bağlantı kimliği ve zamanı (yinelenen kayıtları ayırt etmek için); yoksa null.
/// <see cref="PushId"/>: cihazın aktarma sunucusundaki push kaydı kimliği (PROTOCOL §9; yalnız cihazın kendi bildirimiyle, biçimi doğrulanmış); yoksa null.
/// </summary>
public sealed record TrustedDevice(byte[] PublicKey, string Name, DateTimeOffset PairedAt, bool IsFamiliar = false, string? PushId = null,
    string? LastBleId = null, DateTimeOffset? LastSeen = null)
{
    public byte[] DeviceId => IdentityStore.ComputeDeviceId(PublicKey);
}

/// <summary>Güvenilen cihaz listesi: <c>trusted.bin</c> (DPAPI CurrentUser, JSON). İş parçacığı güvenli.</summary>
public sealed class TrustedDevices
{
    private const string Purpose = "trusted.v1";

    private readonly string _path;
    private readonly object _lock = new();
    private List<TrustedDevice>? _items;

    public TrustedDevices(string? rootDirectory = null)
    {
        _path = Path.Combine(rootDirectory ?? Storage.DefaultRoot, "trusted.bin");
    }

    public string FilePath => _path;

    /// <summary>Dosya bozuk çıktıysa true: liste boş kabul edildi (kimse güvenilmez), bozuk dosya <c>.corrupt</c> olarak yana alındı.</summary>
    public bool WasCorrupt { get; private set; }

    public IReadOnlyList<TrustedDevice> List()
    {
        lock (_lock) return EnsureLoaded().ToArray();
    }

    /// <summary>Ekler; aynı açık anahtar varsa ad/tarih güncellenir. QR ile eşli bir kayıt "tanıdık" olarak düşürülmez.</summary>
    public void Add(byte[] publicKey, string name, DateTimeOffset? pairedAt = null, bool familiar = false)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (publicKey.Length != 32) throw new ArgumentException("Açık anahtar 32 bayt olmalı.", nameof(publicKey));
        lock (_lock)
        {
            var items = EnsureLoaded();
            var previous = items.FirstOrDefault(d => d.PublicKey.AsSpan().SequenceEqual(publicKey));
            if (previous is { IsFamiliar: false }) familiar = false;
            items.RemoveAll(d => d.PublicKey.AsSpan().SequenceEqual(publicKey));
            items.Add(new TrustedDevice((byte[])publicKey.Clone(), name, pairedAt ?? DateTimeOffset.UtcNow, familiar, previous?.PushId, previous?.LastBleId, previous?.LastSeen));
            Save(items);
        }
    }

    /// <summary>
    /// Kayıtlı cihaza pushId yazar (null/geçersiz biçim: silinir değil, yok sayılır → false). Cihaz kayıtlı değilse false.
    /// Değişmediyse diske yazmaz.
    /// </summary>
    public bool SetPushId(byte[] publicKey, string? pushId)
    {
        if (!PushIds.IsValid(pushId)) return false;
        lock (_lock)
        {
            var items = EnsureLoaded();
            int idx = items.FindIndex(d => d.PublicKey.AsSpan().SequenceEqual(publicKey));
            if (idx < 0) return false;
            if (items[idx].PushId == pushId) return true;
            items[idx] = items[idx] with { PushId = pushId };
            Save(items);
            return true;
        }
    }

    /// <summary>Kayıtlı cihazın adını günceller (en çok 40 karakter, denetim karakterleri temizlenir). Değişmediyse/kayıt yoksa false.</summary>
    public bool UpdateName(byte[] publicKey, string name)
    {
        var clean = CleanName(name);
        if (clean is null) return false;
        lock (_lock)
        {
            var items = EnsureLoaded();
            int idx = items.FindIndex(d => d.PublicKey.AsSpan().SequenceEqual(publicKey));
            if (idx < 0 || items[idx].Name == clean) return false;
            items[idx] = items[idx] with { Name = clean };
            Save(items);
            return true;
        }
    }

    /// <summary>Ad temizliği: denetim/yön karakterleri atılır, 40 karaktere kısaltılır; boşsa null.</summary>
    public static string? CleanName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = PairingService.SanitizeName(raw);
        if (s.Length > 40) s = s[..40].TrimEnd();
        return s.Length == 0 ? null : s;
    }

    /// <summary>Cihazın son görülme zamanını ve BLE bağlantı kimliğini yazar. Kayıt yoksa false.</summary>
    public bool NoteSeen(byte[] publicKey, string? bleId, DateTimeOffset now)
    {
        lock (_lock)
        {
            var items = EnsureLoaded();
            int idx = items.FindIndex(d => d.PublicKey.AsSpan().SequenceEqual(publicKey));
            if (idx < 0) return false;
            items[idx] = items[idx] with { LastBleId = bleId ?? items[idx].LastBleId, LastSeen = now };
            Save(items);
            return true;
        }
    }

    /// <summary>pushId'yi siler (sunucu "bilinmiyor/geçersiz" dedi).</summary>
    public bool ClearPushId(byte[] publicKey)
    {
        lock (_lock)
        {
            var items = EnsureLoaded();
            int idx = items.FindIndex(d => d.PublicKey.AsSpan().SequenceEqual(publicKey));
            if (idx < 0 || items[idx].PushId is null) return false;
            items[idx] = items[idx] with { PushId = null };
            Save(items);
            return true;
        }
    }

    public bool Remove(byte[] publicKey)
    {
        lock (_lock)
        {
            var items = EnsureLoaded();
            int idx = items.FindIndex(d => d.PublicKey.AsSpan().SequenceEqual(publicKey));
            if (idx < 0) return false;
            items.RemoveAt(idx);
            Save(items);
            return true;
        }
    }

    public bool RemoveByDeviceId(byte[] deviceId)
    {
        lock (_lock)
        {
            var items = EnsureLoaded();
            int idx = items.FindIndex(d => d.DeviceId.AsSpan().SequenceEqual(deviceId));
            if (idx < 0) return false;
            items.RemoveAt(idx);
            Save(items);
            return true;
        }
    }

    public TrustedDevice? FindByPublicKey(byte[] publicKey)
    {
        lock (_lock) return EnsureLoaded().FirstOrDefault(d => d.PublicKey.AsSpan().SequenceEqual(publicKey));
    }

    public TrustedDevice? FindByDeviceId(byte[] deviceId)
    {
        lock (_lock) return EnsureLoaded().FirstOrDefault(d => d.DeviceId.AsSpan().SequenceEqual(deviceId));
    }

    private sealed record Entry(string Key, string Name, long PairedAtUnixMs, bool Familiar = false, string? PushId = null,
        string? BleId = null, long? LastSeenUnixMs = null);

    private List<TrustedDevice> EnsureLoaded() => _items ??= Load();

    private List<TrustedDevice> Load()
    {
        if (!File.Exists(_path)) return new();
        try
        {
            var json = Storage.Unprotect(File.ReadAllBytes(_path), Purpose);
            var entries = JsonSerializer.Deserialize<List<Entry>>(json) ?? throw new JsonException("null");
            var list = new List<TrustedDevice>();
            foreach (var e in entries)
            {
                var key = Convert.FromBase64String(e.Key);
                if (key.Length != 32 || string.IsNullOrWhiteSpace(e.Name)) throw new JsonException("geçersiz kayıt");
                list.Add(new TrustedDevice(key, e.Name, DateTimeOffset.FromUnixTimeMilliseconds(e.PairedAtUnixMs), e.Familiar,
                    PushIds.IsValid(e.PushId) ? e.PushId : null, e.BleId,
                    e.LastSeenUnixMs is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null));
            }
            return list;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException or ArgumentOutOfRangeException or ArgumentNullException)
        {
            WasCorrupt = true;
            try { File.Move(_path, _path + ".corrupt", overwrite: true); } catch (IOException) { }
            return new();
        }
    }

    private void Save(List<TrustedDevice> items)
    {
        var entries = items.Select(d => new Entry(Convert.ToBase64String(d.PublicKey), d.Name, d.PairedAt.ToUnixTimeMilliseconds(), d.IsFamiliar, d.PushId,
            d.LastBleId, d.LastSeen?.ToUnixTimeMilliseconds()));
        Storage.WriteAtomic(_path, Storage.Protect(JsonSerializer.SerializeToUtf8Bytes(entries), Purpose));
    }
}
