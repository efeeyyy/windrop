import AVFoundation
import SwiftUI
import UIKit

/// AVCaptureSession'ı arka kuyrukta başlatıp durdurur (`startRunning` bloklayıcıdır).
final class CaptureSessionBox: @unchecked Sendable {
    let session = AVCaptureSession()
    private let queue = DispatchQueue(label: "com.efe.windrop.capture")

    func start() {
        queue.async {
            if !self.session.isRunning { self.session.startRunning() }
        }
    }

    func stop() {
        queue.async {
            if self.session.isRunning { self.session.stopRunning() }
        }
    }
}

/// Kamerayla QR okuyan UIKit denetleyicisi (`AVCaptureMetadataOutput`, `.qr`).
final class QRScannerViewController: UIViewController {
    var onCode: ((String) -> Void)?
    var onUnavailable: ((_ permissionDenied: Bool) -> Void)?

    private let capture = CaptureSessionBox()
    private var previewLayer: AVCaptureVideoPreviewLayer?
    private var configured = false

    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = .black
        Task { await prepare() }
    }

    override func viewDidAppear(_ animated: Bool) {
        super.viewDidAppear(animated)
        if configured { capture.start() }
    }

    override func viewWillDisappear(_ animated: Bool) {
        super.viewWillDisappear(animated)
        capture.stop()
    }

    override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        previewLayer?.frame = view.bounds
    }

    private func prepare() async {
        switch AVCaptureDevice.authorizationStatus(for: .video) {
        case .authorized:
            break
        case .notDetermined:
            let granted = await AVCaptureDevice.requestAccess(for: .video)
            if !granted {
                onUnavailable?(true)
                return
            }
        default:
            onUnavailable?(true)
            return
        }
        guard configure() else {
            onUnavailable?(false)
            return
        }
        capture.start()
    }

    private func configure() -> Bool {
        guard let device = AVCaptureDevice.default(for: .video),
              let input = try? AVCaptureDeviceInput(device: device) else { return false }
        let session = capture.session
        session.beginConfiguration()
        guard session.canAddInput(input) else {
            session.commitConfiguration()
            return false
        }
        session.addInput(input)
        let output = AVCaptureMetadataOutput()
        guard session.canAddOutput(output) else {
            session.commitConfiguration()
            return false
        }
        session.addOutput(output)
        output.setMetadataObjectsDelegate(self, queue: .main)
        output.metadataObjectTypes = [.qr]   // çıkış oturuma eklendikten sonra ayarlanmalı
        session.commitConfiguration()

        let layer = AVCaptureVideoPreviewLayer(session: session)
        layer.videoGravity = .resizeAspectFill
        layer.frame = view.bounds
        view.layer.addSublayer(layer)
        previewLayer = layer
        configured = true
        return true
    }
}

extension QRScannerViewController: @preconcurrency AVCaptureMetadataOutputObjectsDelegate {
    func metadataOutput(
        _ output: AVCaptureMetadataOutput,
        didOutput metadataObjects: [AVMetadataObject],
        from connection: AVCaptureConnection
    ) {
        for object in metadataObjects {
            if let code = (object as? AVMetadataMachineReadableCodeObject)?.stringValue {
                onCode?(code)
                return
            }
        }
    }
}

struct QRScannerView: UIViewControllerRepresentable {
    let onCode: (String) -> Void
    let onUnavailable: (_ permissionDenied: Bool) -> Void

    func makeUIViewController(context: Context) -> QRScannerViewController {
        let controller = QRScannerViewController()
        controller.onCode = onCode
        controller.onUnavailable = onUnavailable
        return controller
    }

    func updateUIViewController(_ controller: QRScannerViewController, context: Context) {
        controller.onCode = onCode
        controller.onUnavailable = onUnavailable
    }
}
