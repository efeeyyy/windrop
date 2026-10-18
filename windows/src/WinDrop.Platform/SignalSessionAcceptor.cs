using WinDrop.Core.Noise;
using WinDrop.Core.Protocol;
using WinDrop.Core.Session;

namespace WinDrop.Platform;

/// <summary>Kurulmuş sinyal oturumunu işleyen koşucu (oturum bitince döner).</summary>
public delegate Task PeerSessionHandler(PeerSession session, CancellationToken cancellationToken);

/// <summary>
/// BLE bağlantısından (<see cref="IBleLink"/>) oturum üretir (PROTOCOL §1, §2b, §9a): ön ek → eşleştirme modu açıksa XXpsk3
/// (<see cref="PairingService"/>), değilse IK + <see cref="TrustedDevices"/> → ilk taşıma mesajı → <see cref="SessionHandler"/>.
/// GATT'tan bağımsızdır; gerçek BLE yığını <see cref="BleSignalServer"/> içindedir, testte bellek içi bağlantı verilir.
/// </summary>
public sealed class SignalSessionAcceptor
{
    private readonly IdentityStore _identity;
    private readonly TrustedDevices _trusted;
    private readonly PairingService _pairing;

    public SignalSessionAcceptor(IdentityStore identity, TrustedDevices trusted, PairingService pairing)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(trusted);
        ArgumentNullException.ThrowIfNull(pairing);
        _identity = identity;
        _trusted = trusted;
        _pairing = pairing;
    }

    /// <summary>Kurulan eşli oturum için çağrılır (genelde <see cref="TransferCoordinator.HandleSessionAsync"/>).</summary>
    public PeerSessionHandler? SessionHandler { get; set; }

    /// <summary>Ön ek + el sıkışma + ilk taşıma mesajı için üst süre.</summary>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Bir BLE bağlantısını baştan sona yürütür; bağlantı koptuğunda veya oturum bittiğinde döner. Hata fırlatmaz.
    /// Protokol hatasıyla biterse istemciye <c>WDR</c> gönderir (PROTOCOL §3a) ama yeniden senkron beklemez.</summary>
    public Task RunLinkAsync(IBleLink link, CancellationToken ct) => RunOnceAsync(link, ct);

    /// <summary>
    /// PROTOCOL §3a: <see cref="RunLinkAsync"/> gibi, ama oturum protokol hatasıyla bittiğinde (<c>WDR</c> gönderildikten sonra)
    /// aynı bağlantıdan geçerli bir 20 baytlık ön ek gelene kadar yazmaları sessizce yok sayar, ön ek gelince oturumu baştan kurar.
    /// Bağlantı kopunca (<see cref="IBleLink.ReceiveAsync"/> null) veya <paramref name="ct"/> iptal edilince döner.
    /// </summary>
    public async Task RunLinkWithResyncAsync(IBleLink link, CancellationToken ct)
    {
        IBleLink current = link;
        while (!ct.IsCancellationRequested)
        {
            if (!await RunOnceAsync(current, ct).ConfigureAwait(false)) return;

            PlatformLog.Write("BLE: senkron bekleniyor (yeni ön ek gelene kadar eski oturum yazmaları yok sayılıyor).");
            byte[]? prefix = null;
            try
            {
                while ((prefix = await link.ReceiveAsync(ct).ConfigureAwait(false)) is not null)
                {
                    if (Prefix.TryDecode(prefix, out _)) break;
                }
            }
            catch (OperationCanceledException) { return; }
            if (prefix is null) return;
            PlatformLog.Write("BLE: yeni ön ek geldi, oturum yeniden kuruluyor.");
            current = new ReplayLink(link, prefix);
        }
    }

    /// <summary>Okunmuş ilk yükü bir kez daha veren sarmalayıcı.</summary>
    private sealed class ReplayLink(IBleLink inner, byte[] first) : IBleLink
    {
        private byte[]? _first = first;

        public int Mtu => inner.Mtu;

        public ValueTask SendAsync(ReadOnlyMemory<byte> gattPayload, CancellationToken cancellationToken) =>
            inner.SendAsync(gattPayload, cancellationToken);

        public ValueTask SendResetAsync(CancellationToken cancellationToken) => inner.SendResetAsync(cancellationToken);

        public ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken)
        {
            var f = Interlocked.Exchange(ref _first, null);
            return f is not null ? ValueTask.FromResult<byte[]?>(f) : inner.ReceiveAsync(cancellationToken);
        }
    }

    /// <returns>Oturum protokol/şifre hatasıyla bittiyse true (<c>WDR</c> gönderilmiştir).</returns>
    private async Task<bool> RunOnceAsync(IBleLink link, CancellationToken ct)
    {
        var fault = false;
        SecureChannel? channel = null;
        try
        {
            var transport = new BleMessageTransport(link);
            var key = _identity.LoadOrCreate();
            var mode = _pairing.Current;
            using var hs = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hs.CancelAfter(HandshakeTimeout);

            if (mode is not null)
            {
                var wait = _pairing.RemainingBackoff();
                if (wait > TimeSpan.Zero) await Task.Delay(wait, hs.Token).ConfigureAwait(false);   // başarısız denemelere 1/sn
            }

            // Tel üzerinde desen işareti yok: ilk mesajın uzunluğundan IK (96 B) / XX ailesi (32 B) ayrılır.
            try
            {
                channel = await SecureChannel.AcceptAutoAsync(transport, key,
                    k => _trusted.FindByPublicKey(k.ToArray()) is not null,   // IK: yalnız listedeki
                    _ => true,                                                // XX: yakındaki herkes (§2b); onay teklifte
                    mode?.Psk, hs.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (mode is not null && ex is NoiseException or ProtocolException)
            {
                _pairing.NoteFailure();
                throw;
            }

            if (channel.Kind == HandshakeKind.XXpsk3)
            {
                // Sır tek kullanımlık: yarışan ikinci bağlantı (aynı QR) burada elenir; yalnız tüketimi kazanan ilerler.
                if (!_pairing.TryConsume(mode!))
                {
                    PlatformLog.Write("BLE: eşleştirme sırrı zaten tüketildi/geçersiz, bağlantı kapatıldı.");
                    return false;
                }
                PlatformLog.Write("BLE: XXpsk3 el sıkışması tamam (eşleştirme).");
                var paired = await _pairing.HandleChannelAsync(channel, mode!, ct).ConfigureAwait(false);
                PlatformLog.Write(paired ? "BLE: eşleştirme tamamlandı." : "BLE: eşleştirme tamamlanmadı.");
                return false;
            }

            await channel.EnsureEstablishedAsync(hs.Token).ConfigureAwait(false);   // IK: 0-RTT yok
            var remoteKey = channel.RemoteStaticPublicKey;
            var peer = _trusted.FindByPublicKey(remoteKey);
            if (peer is null && channel.Kind == HandshakeKind.IK)
            {
                PlatformLog.Write("BLE: eş el sıkışma sırasında listeden kaldırıldı, kapatıldı.");
                return false;
            }

            var handler = SessionHandler;
            if (handler is null)
            {
                PlatformLog.Write("BLE: oturum işleyicisi yok, kapatıldı.");
                return false;
            }
            using var session = new PeerSession(remoteKey, peer?.Name, peer is not null, peer is { IsFamiliar: false }, channel);
            channel = null;   // sahipliği session aldı
            PlatformLog.Write($"BLE: oturum kuruldu ({session.Channel.Kind}, {(session.IsKnown ? (session.IsPaired ? "eşli" : "tanıdık") : "bilinmeyen")})");
            await handler(session, ct).ConfigureAwait(false);
            fault = session.Faulted;
            PlatformLog.Write($"BLE: oturum bitti ({session.Channel.Kind}, {(session.IsKnown ? (session.IsPaired ? "eşli" : "tanıdık") : "bilinmeyen")})");
        }
        catch (PeerNotTrustedException)
        {
            PlatformLog.Write("BLE: güvenilmeyen anahtar, bağlantı kapatıldı.");
        }
        catch (VersionMismatchException ex)
        {
            PlatformLog.Write($"BLE: sürüm uyuşmazlığı (istemci {ex.PeerVersion}).");
        }
        catch (Exception ex) when (ex is ProtocolException or NoiseException or IOException or OperationCanceledException
                                       or ObjectDisposedException or InvalidOperationException or ArgumentException)
        {
            PlatformLog.Write($"BLE: bağlantı sona erdi ({ex.GetType().Name}: {ex.Message})");
            fault = ex is ProtocolException or NoiseException;
        }
        catch (Exception ex)
        {
            PlatformLog.Write($"BLE: beklenmeyen hata: {ex}");
        }
        finally
        {
            channel?.Dispose();
        }

        if (fault && !ct.IsCancellationRequested)
        {
            // PROTOCOL §3a: iPhone oturumun öldüğünü bilmez; tek bildirimle haber ver (en iyi çaba, abone yoksa atlanır).
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(3));
                await link.SendResetAsync(cts.Token).ConfigureAwait(false);
                PlatformLog.Write("BLE: istemciye oturum sıfırlama işareti (WDR) gönderildi.");
            }
            catch (Exception ex)
            {
                PlatformLog.Write($"BLE: sıfırlama işareti gönderilemedi ({ex.GetType().Name}).");
            }
        }
        return fault;
    }
}
