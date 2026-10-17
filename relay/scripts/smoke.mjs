// Yerel duman testi: iki istemci (pc, phone) 10 MB'ı birbirine aktarır, SHA-256 doğrular.
// Kullanım: önce `npx wrangler dev --local` (varsayılan 8787), sonra `node scripts/smoke.mjs [ws://127.0.0.1:8787] [MB]`
import { createHash, randomBytes } from "node:crypto";
import WebSocket from "ws";

const base = process.argv[2] ?? "ws://127.0.0.1:8787";
const mb = Number(process.argv[3] ?? 10);
const total = mb * 1024 * 1024;
const CHUNK = 65535;
const room = randomBytes(16).toString("hex");

function open(role) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(`${base}/v1/room/${room}?role=${role}`);
    ws.binaryType = "nodebuffer";
    ws.once("open", () => resolve(ws));
    ws.once("unexpected-response", (_, res) => reject(new Error(`${role}: HTTP ${res.statusCode}`)));
    ws.once("error", reject);
  });
}

// Bir yönde `total` bayt gönderir; hash'i döner.
async function sendAll(ws) {
  const h = createHash("sha256");
  let sent = 0;
  while (sent < total) {
    const n = Math.min(CHUNK, total - sent);
    const buf = randomBytes(n);
    h.update(buf);
    await new Promise((res, rej) => ws.send(buf, { binary: true }, (e) => (e ? rej(e) : res())));
    sent += n;
    while (ws.bufferedAmount > 4 * 1024 * 1024) await new Promise((r) => setTimeout(r, 1));
  }
  return h.digest("hex");
}

function receiveAll(ws) {
  return new Promise((resolve, reject) => {
    const h = createHash("sha256");
    let got = 0;
    ws.on("message", (data, isBinary) => {
      if (!isBinary) return reject(new Error("beklenmeyen metin mesajı"));
      h.update(data);
      got += data.length;
      if (got >= total) resolve({ got, hash: h.digest("hex") });
    });
    ws.once("close", (code) => got < total && reject(new Error(`erken kapandı (${code}), ${got}/${total}`)));
  });
}

const phone = await open("phone");
const pc = await open("pc");
const t0 = performance.now();
const [sentHashPhone, sentHashPc, rxAtPc, rxAtPhone] = await Promise.all([
  sendAll(phone),
  sendAll(pc),
  receiveAll(pc),
  receiveAll(phone),
]);
const secs = (performance.now() - t0) / 1000;
const ok = rxAtPc.hash === sentHashPhone && rxAtPhone.hash === sentHashPc && rxAtPc.got === total && rxAtPhone.got === total;
const aggregate = (2 * mb) / secs;
console.log(
  `${ok ? "OK" : "HATA"}: her yönde ${mb} MB, ${secs.toFixed(2)} sn, toplam ${aggregate.toFixed(1)} MB/s (yön başına ${(mb / secs).toFixed(1)} MB/s)`,
);
phone.close(1000);
pc.close(1000);
process.exit(ok ? 0 : 1);
