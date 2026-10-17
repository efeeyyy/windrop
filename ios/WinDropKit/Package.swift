// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "WinDropKit",
    platforms: [.iOS(.v15), .macOS(.v12)],
    products: [
        .library(name: "WinDropKit", targets: ["WinDropKit"]),
    ],
    dependencies: [
        // Linux (ve WSL/CI testleri) için CryptoKit yerine swift-crypto. Apple'da CryptoKit kullanılır.
        .package(url: "https://github.com/apple/swift-crypto.git", "3.0.0"..<"5.0.0"),
    ],
    targets: [
        .target(
            name: "WinDropKit",
            dependencies: [
                .product(name: "Crypto", package: "swift-crypto", condition: .when(platforms: [.linux])),
            ]
        ),
        // Çapraz uyum (C# <-> Swift) için TCP üzerinden gönderen/alıcı test aracı; Kit koduna dokunmaz. docs/INTEROP.md
        .executableTarget(
            name: "windrop-interop",
            dependencies: [
                "WinDropKit",
                .product(name: "Crypto", package: "swift-crypto", condition: .when(platforms: [.linux])),
            ]
        ),
        .testTarget(
            name: "WinDropKitTests",
            dependencies: ["WinDropKit"]
        ),
    ]
)
