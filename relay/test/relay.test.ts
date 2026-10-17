import { SELF, env, runDurableObjectAlarm, runInDurableObject } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { LIMITS, type Room } from "../src/index";

let counter = 0;
function newRoom(): string {
  counter++;
  return counter.toString(16).padStart(32, "0");
}
let ipCounter = 0;
function newIp(): string {
  ipCounter++;
  return `198.51.100.${ipCounter % 250}-t${ipCounter}`;
}

interface Client {
  ws: WebSocket;
  received: (ArrayBuffer | string)[];
  closed: Promise<{ code: number; reason: string }>;
  waitFor(n: number): Promise<void>;
}

function upgradeHeaders(ip: string): Record<string, string> {
  return { Upgrade: "websocket", "CF-Connecting-IP": ip };
}

async function tryOpen(room: string, role: string, ip: string): Promise<Response> {
  return SELF.fetch(`https://relay.test/v1/room/${room}?role=${role}`, { headers: upgradeHeaders(ip) });
}

async function connect(room: string, role: "pc" | "phone", ip = newIp()): Promise<Client> {
  const res = await tryOpen(room, role, ip);
  expect(res.status).toBe(101);
  const ws = res.webSocket!;
  ws.accept();
  ws.binaryType = "arraybuffer";
  const received: (ArrayBuffer | string)[] = [];
  let waiter: { n: number; resolve: () => void } | null = null;
  ws.addEventListener("message", (ev) => {
    received.push(ev.data as ArrayBuffer | string);
    if (waiter && received.length >= waiter.n) waiter.resolve();
  });
  const closed = new Promise<{ code: number; reason: string }>((resolve) => {
    ws.addEventListener("close", (ev) => {
      resolve({ code: ev.code, reason: ev.reason });
      // Karşı taraf kapatınca bizim uç da kapatılmalı (Workers'ta otomatik değil).
      try {
        ws.close(ev.code === 1005 ? 1000 : ev.code);
      } catch {
        /* zaten kapalı */
      }
    });
  });
  return {
    ws,
    received,
    closed,
    waitFor(n) {
      if (received.length >= n) return Promise.resolve();
      return new Promise<void>((resolve) => {
        waiter = { n, resolve };
      });
    },
  };
}

function bytes(len: number, fill = 0): Uint8Array {
  return new Uint8Array(len).fill(fill);
}

function stubFor(room: string) {
  return env.ROOM.get(env.ROOM.idFromName(room));
}

async function skew(room: string, ms: number): Promise<void> {
  await runInDurableObject(stubFor(room), (inst: Room) => {
    inst.clockSkewMs = ms;
  });
}

describe("HTTP yolları", () => {
  it("/healthz 200 döner", async () => {
    const res = await SELF.fetch("https://relay.test/healthz");
    expect(res.status).toBe(200);
  });

  it("bilinmeyen yol ve geçersiz roomId 404", async () => {
    expect((await SELF.fetch("https://relay.test/")).status).toBe(404);
    expect((await tryOpen("abc", "pc", newIp())).status).toBe(404);
    expect((await tryOpen("G".repeat(32), "pc", newIp())).status).toBe(404);
    expect((await tryOpen("A".repeat(32), "pc", newIp())).status).toBe(404); // yalnız küçük hex
    expect((await tryOpen("a".repeat(33), "pc", newIp())).status).toBe(404);
  });

  it("geçersiz rol veya Upgrade eksik 400", async () => {
    expect((await tryOpen(newRoom(), "tablet", newIp())).status).toBe(400);
    expect((await SELF.fetch(`https://relay.test/v1/room/${newRoom()}?role=pc`)).status).toBe(400);
    expect((await SELF.fetch(`https://relay.test/v1/room/${newRoom()}`, { headers: { Upgrade: "websocket" } })).status).toBe(400);
  });
});

describe("eşleme", () => {
  it("pc ve phone eşlenir, ikili mesajlar iki yönde iletilir", async () => {
    const room = newRoom();
    const pc = await connect(room, "pc");
    const phone = await connect(room, "phone");
    phone.ws.send(bytes(10, 1));
    pc.ws.send(bytes(20, 2));
    await pc.waitFor(1);
    await phone.waitFor(1);
    expect((pc.received[0] as ArrayBuffer).byteLength).toBe(10);
    expect(new Uint8Array(pc.received[0] as ArrayBuffer)[0]).toBe(1);
    expect((phone.received[0] as ArrayBuffer).byteLength).toBe(20);
    expect(new Uint8Array(phone.received[0] as ArrayBuffer)[0]).toBe(2);
    pc.ws.close(1000);
    await phone.closed;
  });

  it("aynı rolden ikinci bağlantı 409", async () => {
    const room = newRoom();
    await connect(room, "pc");
    expect((await tryOpen(room, "pc", newIp())).status).toBe(409);
    const phone = await connect(room, "phone");
    expect((await tryOpen(room, "phone", newIp())).status).toBe(409);
    phone.ws.close(1000);
  });

  it("üçüncü bağlantı 409", async () => {
    const room = newRoom();
    await connect(room, "pc");
    await connect(room, "phone");
    expect((await tryOpen(room, "pc", newIp())).status).toBe(409);
    expect((await tryOpen(room, "phone", newIp())).status).toBe(409);
  });

  it("kapanmış oda yeniden kullanılamaz (410)", async () => {
    const room = newRoom();
    const pc = await connect(room, "pc");
    const phone = await connect(room, "phone");
    pc.ws.close(1000);
    await phone.closed;
    expect((await tryOpen(room, "pc", newIp())).status).toBe(410);
  });

  it("bir taraf kapanınca diğeri de kapanır", async () => {
    const room = newRoom();
    const pc = await connect(room, "pc");
    const phone = await connect(room, "phone");
    phone.ws.close(1000);
    const c = await pc.closed;
    expect(c.code).toBe(1000);
  });
});

describe("mesaj kuralları", () => {
  it("metin mesajı bağlantıyı (ve odayı) kapatır", async () => {
    const room = newRoom();
    const pc = await connect(room, "pc");
    const phone = await connect(room, "phone");
    pc.ws.send("merhaba");
    expect((await pc.closed).code).toBe(1003);
    expect((await phone.closed).code).toBe(1003);
    expect(phone.received.length).toBe(0);
  });

  it("65535 B kabul, 65536 B 1009 ile kapanır", async () => {
    const room = newRoom();
    const pc = await connect(room, "pc");
    const phone = await connect(room, "phone");
    pc.ws.send(bytes(LIMITS.maxMessageBytes, 7));
    await phone.waitFor(1);
    expect((phone.received[0] as ArrayBuffer).byteLength).toBe(65535);
    pc.ws.send(bytes(LIMITS.maxMessageBytes + 1, 7));
    expect((await pc.closed).code).toBe(1009);
    expect((await phone.closed).code).toBe(1009);
    expect(phone.received.length).toBe(1);
  });

  it("sıra korunur", async () => {
    const room = newRoom();
    const pc = await connect(room, "pc");
    const phone = await connect(room, "phone");
    const N = 500;
    for (let i = 0; i < N; i++) {
      const b = new Uint8Array(4 + (i % 50));
      new DataView(b.buffer).setUint32(0, i);
      phone.ws.send(b);
    }
    await pc.waitFor(N);
    for (let i = 0; i < N; i++) {
      expect(new DataView(pc.received[i] as ArrayBuffer).getUint32(0)).toBe(i);
    }
    pc.ws.close(1000);
  });
});

describe("ön tampon (eş henüz yok)", () => {
  it("tamponlanan mesajlar eş gelince sırayla iletilir", async () => {
    const room = newRoom();
    const phone = await connect(room, "phone");
    for (let i = 0; i < 5; i++) phone.ws.send(bytes(100, i));
    phone.ws.send(bytes(10, 9));
    // Mesajların DO'ya ulaşması için küçük bir bekleme (aynı olay döngüsü)
    await new Promise((r) => setTimeout(r, 50));
    const pc = await connect(room, "pc");
    await pc.waitFor(6);
    expect(pc.received.map((m) => new Uint8Array(m as ArrayBuffer)[0])).toEqual([0, 1, 2, 3, 4, 9]);
    // Ardından gelen mesaj da sırayı bozmaz
    phone.ws.send(bytes(3, 42));
    await pc.waitFor(7);
    expect(new Uint8Array(pc.received[6] as ArrayBuffer)[0]).toBe(42);
    pc.ws.close(1000);
  });

  it("tam 1 MiB tamponlanır, 1 MiB'yi aşan 1008 ile kapanır", async () => {
    // 1 MiB = 16 x 65536 ama tek mesaj ≤ 65535: 16 mesaj 65535 B = 1048560 B (sığar); 17.'si sığmaz.
    const room = newRoom();
    const phone = await connect(room, "phone");
    for (let i = 0; i < 16; i++) phone.ws.send(bytes(65535, i));
    await new Promise((r) => setTimeout(r, 100));
    phone.ws.send(bytes(17, 1)); // 1048560 + 17 > 1048576
    expect((await phone.closed).code).toBe(1008);
    expect((await tryOpen(room, "pc", newIp())).status).toBe(410);
  });

  it("1 MiB sınırında kalan tampon eşe eksiksiz verilir", async () => {
    const room = newRoom();
    const phone = await connect(room, "phone");
    for (let i = 0; i < 16; i++) phone.ws.send(bytes(65535, i));
    phone.ws.send(bytes(16, 99)); // 1048560 + 16 = 1048576 tam sınır
    await new Promise((r) => setTimeout(r, 100));
    const pc = await connect(room, "pc");
    await pc.waitFor(17);
    expect(pc.received.length).toBe(17);
    pc.ws.close(1000);
  });
});

describe("süre sınırları (sahte zaman + alarm)", () => {
  it("eş 60 sn gelmezse oda 1008 ile kapanır", async () => {
    const room = newRoom();
    const pc = await connect(room, "pc");
    await skew(room, LIMITS.peerWaitMs + 1000);
    expect(await runDurableObjectAlarm(stubFor(room))).toBe(true);
    expect((await pc.closed).code).toBe(1008);
  });

  it("60 sn'den kısa sürede alarm odayı kapatmaz", async () => {
    const room = newRoom();
    const pc = await connect(room, "pc");
    await skew(room, 30_000);
    await runDurableObjectAlarm(stubFor(room));
    let closed = false;
    void pc.closed.then(() => (closed = true));
    await new Promise((r) => setTimeout(r, 50));
    expect(closed).toBe(false);
    pc.ws.close(1000);
  });

  it("eşlenmiş oda 60 sn boşta kalırsa 1008 ile kapanır", async () => {
    const room = newRoom();
    const pc = await connect(room, "pc");
    const phone = await connect(room, "phone");
    pc.ws.send(bytes(5));
    await phone.waitFor(1);
    await skew(room, LIMITS.idleMs + 1000);
    expect(await runDurableObjectAlarm(stubFor(room))).toBe(true);
    expect((await pc.closed).code).toBe(1008);
    expect((await phone.closed).code).toBe(1008);
  });

  it("trafik varken boşta sayılmaz", async () => {
    const room = newRoom();
    const pc = await connect(room, "pc");
    const phone = await connect(room, "phone");
    await skew(room, 50_000);
    pc.ws.send(bytes(5)); // lastActivity = t+50s
    await phone.waitFor(1);
    await skew(room, 100_000); // son hareketten 50 sn
    await runDurableObjectAlarm(stubFor(room));
    let closed = false;
    void pc.closed.then(() => (closed = true));
    await new Promise((r) => setTimeout(r, 50));
    expect(closed).toBe(false);
    pc.ws.close(1000);
  });

  it("2 saatlik ömür dolunca etkin oda da kapanır", async () => {
    const room = newRoom();
    const pc = await connect(room, "pc");
    const phone = await connect(room, "phone");
    await skew(room, LIMITS.roomLifetimeMs - 10_000);
    pc.ws.send(bytes(5)); // etkin: boşta değil
    await phone.waitFor(1);
    await skew(room, LIMITS.roomLifetimeMs + 1000);
    pc.ws.send(bytes(5));
    await phone.waitFor(2);
    await runDurableObjectAlarm(stubFor(room));
    expect((await pc.closed).code).toBe(1008);
  });
});

describe("toplam bayt sınırı", () => {
  it("oda 4 GiB'ı aşınca 1008 ile kapanır", async () => {
    const room = newRoom();
    const pc = await connect(room, "pc");
    const phone = await connect(room, "phone");
    await runInDurableObject(stubFor(room), (inst: Room) => {
      (inst as unknown as { totalBytes: number }).totalBytes = LIMITS.roomTotalBytes - 10;
    });
    pc.ws.send(bytes(10)); // tam sınır: kabul
    await phone.waitFor(1);
    pc.ws.send(bytes(1));
    expect((await pc.closed).code).toBe(1008);
    expect((await phone.closed).code).toBe(1008);
  });
});

describe("IP başına oda açma sınırı", () => {
  it("dakikada 10 oda; 11.'si 429; başka IP etkilenmez", async () => {
    const ip = newIp();
    const opened: Client[] = [];
    for (let i = 0; i < LIMITS.roomsPerIpPerWindow; i++) opened.push(await connect(newRoom(), "pc", ip));
    expect((await tryOpen(newRoom(), "pc", ip)).status).toBe(429);
    // Başka IP serbest
    const other = await connect(newRoom(), "pc", newIp());
    other.ws.close(1000);
    for (const c of opened) c.ws.close(1000);
  });

  it("mevcut odaya katılmak (eş bağlantısı) oda açma sayılmaz", async () => {
    const ip = newIp();
    for (let i = 0; i < LIMITS.roomsPerIpPerWindow - 1; i++) {
      const c = await connect(newRoom(), "pc", ip);
      c.ws.close(1000);
    }
    const room = newRoom();
    const pc = await connect(room, "pc", ip); // 10. oda
    const phone = await connect(room, "phone", ip); // katılım: sayılmaz
    expect(phone.ws).toBeDefined();
    expect((await tryOpen(newRoom(), "pc", ip)).status).toBe(429);
    pc.ws.close(1000);
  });
});
