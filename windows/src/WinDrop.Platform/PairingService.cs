using System.Buffers.Text;
using System.Security.Cryptography;
using WinDrop.Core.Protocol;
using WinDrop.Core.Session;

namespace WinDrop.Platform;

/// <summary>Açık eşleştirme modu (QR ekranda): tek kullanımlık 32 baytlık sır + süre (PROTOCOL §3.2).</summary>
public sealed class PairingMode
{
    private readonly PairingService _owner;

    internal PairingMode(PairingService owner, string qrPayload, byte[] psk, DateTimeOffset expiresAt, TimeSpan duration)
    {
        _owner = owner;
        QrPayload = qrPayload;
        Psk = psk;
        ExpiresAt = expiresAt;
        Duration = duration;
    }

    public string QrPayload { get; }
    public DateTimeOffset ExpiresAt { get; }
    public TimeSpan Duration { get; }
    internal byte[] Psk { get; }

    /// <summary>Eşleştirme modunu kapatır (QR geçersiz olur).</summary>
    public void Cancel() => _owner.End(this);
}

/// <summary>Eşleştirme el sıkışması sonucu.</summary>
public enum PairingOutcome { Failed, Paired, AlreadyPaired }

/// <summary>PC'de onay bekleyen eşleştirme (iPhone XXpsk3'ü tamamladı, <c>hello.name</c> geldi).</summary>
/// <param name="VerificationCode">El sıkışma özetinden 4 haneli kod (<see cref="Sas"/>); iPhone ekranındakiyle karşılaştırılır.</param>
public sealed record PairingRequestInfo(string RequestId, string DeviceName, string? VerificationCode = null);

/// <summary>
/// PROTOCOL §3.2: QR yükü, 2 dk eşleştirme modu, XXpsk3 sonrası UI onayı → <see cref="TrustedDevices"/> →
/// etkin nokta bilgisi (<c>pairInfo</c>) şifreli kanaldan. Sır tek kullanımlıktır: başarılı XXpsk3 el sıkışmasında (onaydan
/// önce) tüketilir — QR fotoğrafıyla gelen biri ikinci deneme yapamaz; reddedilirse yeni QR gerekir.
/// Başarısız denemelere hız sınırı (1/sn) uygulanır; sayaçla QR iptal edilmez.
/// </summary>
public sealed class PairingService
{
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromMinutes(2);

    private readonly IdentityStore _identity;
    private readonly TrustedDevices _trusted;
    private readonly HotspotConfigStore _hotspot;
    private readonly string _localName;
    private readonly TimeSpan _duration;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly Dictionary<string, TaskCompletionSource<bool>> _requests = [];
    private PairingMode? _current;
    private long _nextAttemptTicks;

    public PairingService(IdentityStore identity, TrustedDevices trusted, HotspotConfigStore hotspot, string localName,
        TimeSpan? duration = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(trusted);
        ArgumentNullException.ThrowIfNull(hotspot);
        _identity = identity;
        _trusted = trusted;
        _hotspot = hotspot;
        _localName = string.IsNullOrWhiteSpace(localName) ? "WinDrop" : localName;
        _duration = duration ?? DefaultDuration;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>PC onayı için üst süre.</summary>
    public TimeSpan ApprovalTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>XXpsk3'ten sonra iPhone'un <c>hello</c>'sunu bekleme süresi.</summary>
    public TimeSpan HelloTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>PC'de onay istendi (herhangi bir iş parçacığında çağrılabilir).</summary>
    public event Action<PairingRequestInfo>? PairingRequested;

    /// <summary>Cihaz güvenilen listeye eklendi.</summary>
    public event Action<TrustedDevice>? DeviceTrusted;

    /// <summary>QR ile eşleşmeye çalışan cihaz zaten eşli (onay istenmedi); parametre: kayıtlı ad.</summary>
    public event Action<string>? AlreadyPaired;

    /// <summary>Yeni eşleştirme modu açar (öncekini geçersiz kılar).</summary>
    public PairingMode Start()
    {
        var psk = RandomNumberGenerator.GetBytes(32);
        var payload = BuildQrPayload(_identity.PublicKey, psk, _localName);
        var mode = new PairingMode(this, payload, psk, _time.GetUtcNow() + _duration, _duration);
        lock (_lock) _current = mode;
        PlatformLog.Write($"Eşleştirme modu açıldı ({_duration.TotalSeconds:0} sn).");
        return mode;
    }

    /// <summary>QR yükü: <c>windrop://pair?v=1&amp;k=&lt;b64url&gt;&amp;s=&lt;b64url&gt;&amp;n=&lt;yüzde-kodlu ad&gt;</c>.</summary>
    public static string BuildQrPayload(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> psk, string name) =>
        $"windrop://pair?v=1&k={Base64Url.EncodeToString(publicKey)}&s={Base64Url.EncodeToString(psk)}&n={Uri.EscapeDataString(name)}";

    /// <summary>Süresi dolmamış, tüketilmemiş mod; yoksa null.</summary>
    public PairingMode? Current
    {
        get
        {
            lock (_lock)
            {
                if (_current is not null && _current.ExpiresAt <= _time.GetUtcNow()) _current = null;
                return _current;
            }
        }
    }

    public bool IsActive => Current is not null;

    /// <summary>
    /// QR sırrını atomik tüketir: <paramref name="mode"/> hâlâ geçerli ve tüketilmemişse kapatır ve true döner; aksi halde (aynı QR ile
    /// yarışan ikinci bağlantı, iptal, süre dolması) false. XXpsk3 el sıkışmasından hemen sonra çağrılır; false → bağlantı kapatılır.
    /// </summary>
    internal bool TryConsume(PairingMode mode)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_current, mode) || mode.ExpiresAt <= _time.GetUtcNow()) return false;
            _current = null;
            return true;
        }
    }

    internal void End(PairingMode mode)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_current, mode)) _current = null;
        }
    }

    // ---- hız sınırı ----

    /// <summary>Son başarısız denemeden beri 1 sn dolmadıysa kalan süre.</summary>
    internal TimeSpan RemainingBackoff()
    {
        var left = new DateTimeOffset(Interlocked.Read(ref _nextAttemptTicks), TimeSpan.Zero) - _time.GetUtcNow();
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    internal void NoteFailure() =>
        Interlocked.Exchange(ref _nextAttemptTicks, (_time.GetUtcNow() + TimeSpan.FromSeconds(1)).UtcTicks);

    // ---- UI onayı ----

    /// <summary>Bekleyen eşleştirme isteğine yanıt (PC'de "iPhone 'X' eşleşsin mi?").</summary>
    public void Respond(string requestId, bool accept)
    {
        TaskCompletionSource<bool>? tcs;
        lock (_lock) _requests.TryGetValue(requestId, out tcs);
        tcs?.TrySetResult(accept);
    }

    // ---- kanal işleyici ----

    /// <summary>
    /// XXpsk3 kurulmuş kanalda: sırrı tüket → iPhone <c>hello</c> → UI onayı → güvenilene ekle → PC <c>hello</c> + <c>pairInfo</c>.
    /// </summary>
    /// <returns>Sonuç: yeni eşleşme, zaten eşli (onay istenmez) veya başarısız.</returns>
    internal async Task<PairingOutcome> HandleChannelAsync(SecureChannel channel, PairingMode mode, CancellationToken ct)
    {
        End(mode);   // tek kullanımlık
        string name;
        using (var helloCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            helloCts.CancelAfter(HelloTimeout);
            var first = await channel.ReceiveSignalAsync(helloCts.Token).ConfigureAwait(false);
            if (first is not HelloMessage hello)
            {
                PlatformLog.Write("Eşleştirme: ilk mesaj hello değil, iptal.");
                return PairingOutcome.Failed;
            }
            name = SanitizeName(hello.Name);
        }

        // Zaten QR ile eşli (tanıdık değil): onay istenmez, kayıt değişmez; iPhone yine normal başarı gibi pairInfo alır.
        var existing = _trusted.FindByPublicKey(channel.RemoteStaticPublicKey);
        var alreadyPaired = existing is { IsFamiliar: false };
        if (alreadyPaired)
        {
            PlatformLog.Write($"Eşleştirme: '{existing!.Name}' zaten eşli, onay istenmedi.");
            AlreadyPaired?.Invoke(existing.Name);
        }

        if (!alreadyPaired)
        {
            var requestId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock) _requests[requestId] = tcs;
            try
            {
                PlatformLog.Write($"Eşleştirme isteği: '{name}'");
                PairingRequested?.Invoke(new PairingRequestInfo(requestId, name, Sas.Compute(channel.HandshakeHash)));
                using var approveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                approveCts.CancelAfter(ApprovalTimeout);
                bool accepted;
                try { accepted = await tcs.Task.WaitAsync(approveCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { accepted = false; }
                if (!accepted)
                {
                    PlatformLog.Write("Eşleştirme reddedildi veya zaman aşımı.");
                    return PairingOutcome.Failed;
                }
            }
            finally
            {
                lock (_lock) _requests.Remove(requestId);
            }

            _trusted.Add(channel.RemoteStaticPublicKey, name);
            var device = _trusted.FindByPublicKey(channel.RemoteStaticPublicKey)!;
            PlatformLog.Write($"Eşleştirildi: '{name}' ({DeviceId.ToHex(device.DeviceId)})");
            DeviceTrusted?.Invoke(device);
        }

        var cfg = _hotspot.GetOrCreate();
        await channel.SendSignalAsync(new HelloMessage(Prefix.CurrentVersion, _localName, ["tcp", "hotspot"], null, false), ct).ConfigureAwait(false);
        await channel.SendSignalAsync(new PairInfoMessage(cfg.Ssid, cfg.Passphrase), ct).ConfigureAwait(false);

        // iPhone'un bilgiyi alıp bağlantıyı kapatmasını kısaca bekle (bildirim kuyruğu boşalsın).
        using var tail = CancellationTokenSource.CreateLinkedTokenSource(ct);
        tail.CancelAfter(TimeSpan.FromSeconds(3));
        try { await channel.ReceiveAsync(tail.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ProtocolException) { }
        return alreadyPaired ? PairingOutcome.AlreadyPaired : PairingOutcome.Paired;
    }

    internal static string SanitizeName(string? raw)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in raw ?? "")
        {
            if (char.IsControl(ch) || ch is '‎' or '‏' or (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩'))
                continue;
            sb.Append(ch);
        }
        var s = sb.ToString().Trim();
        if (s.Length > 64) s = s[..64].TrimEnd();
        return s.Length == 0 ? "iPhone" : s;
    }
}
