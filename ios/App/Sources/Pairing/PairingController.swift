import CoreBluetooth
import Foundation
import Observation
@preconcurrency import WinDropKit

/// Eşleştirme akışı (PROTOCOL §2): QR -> BLE'de PC'yi bul -> XXpsk3 -> `k` doğrulaması ->
/// PC'de kullanıcı onayı -> kaydet + etkin nokta bilgisi.
@MainActor
@Observable
final class PairingController {
    enum Phase: Equatable {
        case scanning
        case searching(String)
        case connecting(String)
        /// (PC adı, 4 haneli doğrulama kodu; Windows onay ekranındakiyle aynı olmalı)
        case waitingApproval(String, String)
        case succeeded(String)
        case failed(String)
    }

    private(set) var phase: Phase = .scanning

    @ObservationIgnored private let identity: IdentityStore
    @ObservationIgnored private let hub: SignalHub
    @ObservationIgnored private let helloInfo: @MainActor () -> HelloInfo
    @ObservationIgnored private var task: Task<Void, Never>?

    init(
        identity: IdentityStore, hub: SignalHub,
        helloInfo: @escaping @MainActor () -> HelloInfo
    ) {
        self.identity = identity
        self.hub = hub
        self.helloInfo = helloInfo
    }

    func reset() {
        task?.cancel()
        task = nil
        phase = .scanning
    }

    func cancel() {
        task?.cancel()
        task = nil
        if case .succeeded = phase { return }
        phase = .scanning
    }

    /// Kameradan okunan metin. WinDrop kodu olmayanlar sessizce yok sayılır.
    func handleScanned(_ code: String) {
        guard case .scanning = phase, task == nil else { return }
        guard code.lowercased().hasPrefix("windrop://") else { return }
        do {
            let invite = try PairingInvite.parse(code)
            phase = .searching(invite.pcName)
            task = Task { [weak self] in
                await self?.run(invite)
            }
        } catch {
            phase = .failed(error.localizedDescription)
        }
    }

    /// Bir sinyali en çok `seconds` bekler; süre dolarsa bağlantıyı kapatıp nil döner (Kit okuması zaman aşımsız).
    private func receive(_ channel: SecureChannel, link: BlePeripheralLink, timeout seconds: TimeInterval) async throws -> SignalMessage? {
        let timer = Task { @MainActor in
            try? await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
            if !Task.isCancelled { link.close() }
        }
        defer { timer.cancel() }
        while let message = try await channel.receiveMessage() {
            if case .signal(let signal) = message { return signal }
        }
        return nil
    }

    private func run(_ invite: PairingInvite) async {
        var openedLink: BlePeripheralLink?
        let central = hub.beginPairing()
        defer { hub.endPairing() }
        do {
            let key = try identity.deviceKey()
            await central.awaitReady()
            // Önce mevcut bağlantılar (iOS bağlı çevre birimini taramada göstermez), yoksa tarama.
            let target = hub.pairingTarget(publicKey: invite.pcPublicKey)
            let peripheral: CBPeripheral
            let link: BlePeripheralLink
            if let existing = central.findConnectedPeripheral(preferred: target.match, excluding: target.others) {
                peripheral = existing
                phase = .connecting(invite.pcName)
                link = try await central.reopenForPairing(existing, timeout: 15)
            } else {
                do {
                    peripheral = try await central.discoverForPairing(pcName: invite.pcName, timeout: 20)
                } catch BleError.timeout {
                    throw PairingError.pcNotFound
                }
                phase = .connecting(invite.pcName)
                link = try await central.openLink(to: peripheral, timeout: 15)
            }
            openedLink = link
            let transport = BleMessageTransport(link: BleLinkAdapter(link: link))
            // XXpsk3; PC anahtarı QR'daki `k` ile uyuşmazsa Kit 3. mesajdan önce SessionError.peerKeyMismatch fırlatır.
            let channel = try await SecureChannel.connectPairing(
                transport: transport, prefix: Prefix.ble(), localKey: key, psk: invite.secret,
                expectedRemoteKey: invite.pcPublicKey)

            // PC'de onay: kullanıcı onaylamadan PC bir şey göndermez (PROTOCOL §2.4). Önce `hello`.
            phase = .waitingApproval(invite.pcName, channel.verificationCode)
            try await channel.sendSignal(.hello(helloInfo().helloMessage))

            var credentials: HotspotCredentials?
            func absorb(_ message: SignalMessage) {
                switch message {
                case .pairInfo(let p):   // PROTOCOL §2.7/§4: eşleştirmede etkin nokta bilgisi
                    credentials = HotspotCredentials(ssid: p.ssid, pass: p.pass)
                case .hotspot(let h):
                    if let ssid = h.ssid, let pass = h.pass { credentials = HotspotCredentials(ssid: ssid, pass: pass) }
                default:
                    break
                }
            }
            guard let first = try await receive(channel, link: link, timeout: 150) else {
                throw PairingError.rejectedByPC   // süre doldu / PC kapattı
            }
            absorb(first)
            if credentials == nil {
                while credentials == nil, let next = try? await receive(channel, link: link, timeout: 3) {
                    absorb(next)
                }
            }

            let pc = KnownPC(
                id: DeviceId.hex(publicKey: invite.pcPublicKey), name: invite.pcName, publicKey: invite.pcPublicKey,
                peripheralID: peripheral.identifier, addedAt: Date(), autoAccept: true)
            do {
                try identity.upsert(pc, hotspot: credentials)
            } catch {
                throw PairingError.saveFailed
            }
            hub.syncWanted()   // bekleyen bağlantı listesi güncellenir
            // Eşleştirme oturumu (XXpsk3) kapatılır; IK bağlantısı otomatik yeniden kurulur.
            central.closeLink(peripheral.identifier)
            phase = .succeeded(invite.pcName)
        } catch {
            if let openedLink, openedLink.isOpen { central.closeLink(openedLink.peripheralID) }
            if Task.isCancelled { return }
            if case .waitingApproval(_, _) = phase, !(error is PairingError) {
                phase = .failed(PairingError.rejectedByPC.localizedDescription)
            } else if let session = error as? SessionError, session == .peerKeyMismatch {
                phase = .failed("Karşı cihazın anahtarı QR koduyla uyuşmuyor. Eşleştirme iptal edildi.")
            } else if let session = error as? SessionError, case .versionMismatch = session {
                phase = .failed("Bilgisayardaki WinDrop sürümü uyumsuz. Güncelleme gerekli.")
            } else {
                phase = .failed(error.localizedDescription)
            }
        }
        task = nil
    }
}
