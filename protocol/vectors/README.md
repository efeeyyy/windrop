# Ortak test vektörleri

PROTOCOL §1: Windows ve iOS Noise uygulamaları resmi vektörlerden geçmeden kullanılmaz.

## cacophony.txt

- **Kaynak:** https://raw.githubusercontent.com/haskell-cryptography/cacophony/master/vectors/cacophony.txt
  (cacophony projesi, `haskell-cryptography/cacophony`)
- **İndirme tarihi:** 2026-10-17
- **SHA-256:** `3bde7c09a6f349ee11c825c50fcc02649f8f02a47c857a459206b357f9386cae`
- **Boyut:** 1 709 817 bayt, 944 vektör
- **Lisans:** Unlicense (kamu malı; kaynak deposunun `LICENSE` dosyası ile doğrulandı)
- **Biçim:** JSON (`vectors[]`: `protocol_name`, `init_*`/`resp_*` anahtarlar ve psk'lar, `handshake_hash`,
  `messages[]` = `payload` + `ciphertext`). Dosya olduğu gibi saklanır, değiştirilmez.
- **Kullanım:** yalnız veridir. WinDrop yalnız `*_25519_ChaChaPoly_SHA256` girdilerini kullanır (59 vektör,
  IK ve XXpsk3 dahil); testler `windows/tests/WinDrop.Core.Tests/Noise/CacophonyVectorTests.cs`.
  iOS (CryptoKit) uygulaması aynı dosyadan aynı 59 girdiyi çalıştırmalıdır.
- **Okuma kuralları** (iOS testi için): başlatan ve yanıtlayan ayrı kurulur, ephemeral anahtarlar vektördeki
  `init_ephemeral`/`resp_ephemeral` ile sabitlenir. `messages` indeks `i` çift → başlatan yazar, tek →
  yanıtlayan; el sıkışma bittikten sonra aynı dönüşüm taşıma mesajlarında sürer; tek mesajlık (tek yönlü:
  N, K, X) desenlerde taşıma mesajlarının hepsini başlatan gönderir. `init_psks`/`resp_psks` psk belirteçlerinin
  kullanım sırasıdır.

- `sas.json`: PROTOCOL §2b doğrulama kodu vektörü (sabit anahtarlarla bir `Noise_XX` el sıkışması, `handshake_hash`, `sas` = 4 hane). Kaynak: bağımsız Node referansı (Swift ve C# testleri aynı değeri doğrular).

- `relay.json`: PROTOCOL §6.3 `roomId = hex(SHA-256("WinDrop-relay" || token))[0:32]` vektörleri. `token` 32 ham bayttır; `relay` sinyalinde 43 karakterlik dolgusuz base64url (`token_b64url`) olarak taşınır, oda kimliği ham bayttan hesaplanır. Kaynak: bağımsız Python (hashlib) referansı; C# testi `RelayRoomTests`, Swift testi aynı değeri doğrulamalıdır.
