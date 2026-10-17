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

    /// <summary>Bir BLE bağlantısını baştan sona yürütür; bağlantı koptuğunda veya oturum bittiğinde döner. Hata fırlatmaz.</summary>
    public async Task RunLinkAsync(IBleLink link, CancellationToken ct)
    {
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
                    return;
                }
                PlatformLog.Write("BLE: XXpsk3 el sıkışması tamam (eşleştirme).");
                var paired = await _pairing.HandleChannelAsync(channel, mode!, ct).ConfigureAwait(false);
                PlatformLog.Write(paired ? "BLE: eşleştirme tamamlandı." : "BLE: eşleştirme tamamlanmadı.");
                return;
            }

            await channel.EnsureEstablishedAsync(hs.Token).ConfigureAwait(false);   // IK: 0-RTT yok
            var remoteKey = channel.RemoteStaticPublicKey;
            var peer = _trusted.FindByPublicKey(remoteKey);
            if (peer is null && channel.Kind == HandshakeKind.IK)
            {
                PlatformLog.Write("BLE: eş el sıkışma sırasında listeden kaldırıldı, kapatıldı.");
                return;
            }

            var handler = SessionHandler;
            if (handler is null)
            {
                PlatformLog.Write("BLE: oturum işleyicisi yok, kapatıldı.");
                return;
            }
            using var session = new PeerSession(remoteKey, peer?.Name, peer is not null, peer is { IsFamiliar: false }, channel);
            channel = null;   // sahipliği session aldı
            PlatformLog.Write($"BLE: oturum kuruldu ({session.Channel.Kind}, {(session.IsKnown ? (session.IsPaired ? "eşli" : "tanıdık") : "bilinmeyen")})");
            await handler(session, ct).ConfigureAwait(false);
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
        }
        catch (Exception ex)
        {
            PlatformLog.Write($"BLE: beklenmeyen hata: {ex}");
        }
        finally
        {
            channel?.Dispose();
        }
    }
}
