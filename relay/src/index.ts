// WinDrop aktarma sunucusu (docs/PROTOCOL.md §6.3).
// Sunucu yalnız şifreli baytları iletir; içerik, roomId ve IP LOGLANMAZ (yalnız toplu sayaçlar).
import { DurableObject } from "cloudflare:workers";

export interface Env {
  ROOM: DurableObjectNamespace<Room>;
  IP_LIMITER: DurableObjectNamespace<IpLimiter>;
}

type Role = "pc" | "phone";

/** Sınırlar (PROTOCOL.md §6.3). */
export const LIMITS = {
  maxMessageBytes: 65535,
  preBufferBytes: 1024 * 1024,
  roomTotalBytes: 4 * 1024 * 1024 * 1024,
  roomLifetimeMs: 2 * 60 * 60 * 1000,
  peerWaitMs: 60_000,
  idleMs: 60_000,
  roomsPerIpPerWindow: 10,
  ipWindowMs: 60_000,
};

const ROOM_PATH = /^\/v1\/room\/([0-9a-f]{32})$/;

// ---------------------------------------------------------------- Worker girişi
export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);
    if (url.pathname === "/healthz") {
      return new Response("ok", { status: 200, headers: { "cache-control": "no-store" } });
    }
    const m = ROOM_PATH.exec(url.pathname);
    if (!m) return new Response("not found", { status: 404 });
    if (request.method !== "GET") return new Response("method not allowed", { status: 405 });
    const role = url.searchParams.get("role");
    if (role !== "pc" && role !== "phone") return new Response("bad role", { status: 400 });
    if (request.headers.get("Upgrade")?.toLowerCase() !== "websocket") {
      return new Response("websocket required", { status: 400 });
    }

    // IP yalnız karma olarak (Durable Object adı) kullanılır; asla saklanmaz/loglanmaz.
    const ip = request.headers.get("CF-Connecting-IP") ?? "unknown";
    const ipKey = await sha256Hex(`WinDrop-ip|${ip}`);

    const headers = new Headers(request.headers);
    headers.set("x-windrop-role", role); // istemciden gelen değerin üzerine yazılır
    headers.set("x-windrop-ipkey", ipKey);
    const stub = env.ROOM.get(env.ROOM.idFromName(m[1]));
    return stub.fetch(new Request(request, { headers }));
  },
} satisfies ExportedHandler<Env>;

async function sha256Hex(s: string): Promise<string> {
  const d = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(s));
  return [...new Uint8Array(d)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

// ---------------------------------------------------------------- Oda
/**
 * Bir transfer odası. Standart (hibernate etmeyen) WebSocket API kullanılır: ≤ 1 MiB ön tampon ve
 * sayaçlar bellekte durur; bağlantılar açıkken nesne bellekten atılmaz. Yalnız "oda yaratıldı/öldü"
 * durumu depolamaya yazılır (oda kimliği yeniden kullanılamasın diye).
 */
export class Room extends DurableObject<Env> {
  private sockets: Partial<Record<Role, WebSocket>> = {};
  private buffer: ArrayBuffer[] = [];
  private bufferBytes = 0;
  private createdAt = 0;
  private lastActivity = 0;
  private totalBytes = 0;
  private messages = 0;
  private dead = false;
  /** Yalnız test için sahte zaman: Date.now() değerine eklenir. */
  public clockSkewMs = 0;

  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    ctx.blockConcurrencyWhile(async () => {
      this.createdAt = (await ctx.storage.get<number>("createdAt")) ?? 0;
      this.dead = (await ctx.storage.get<boolean>("dead")) ?? false;
    });
  }

  private now(): number {
    return Date.now() + this.clockSkewMs;
  }

  async fetch(request: Request): Promise<Response> {
    const role = request.headers.get("x-windrop-role") as Role | null;
    if (role !== "pc" && role !== "phone") return new Response("bad role", { status: 400 });
    if (request.headers.get("Upgrade")?.toLowerCase() !== "websocket") {
      return new Response("websocket required", { status: 400 });
    }
    if (this.dead) return new Response("gone", { status: 410 });
    if (this.sockets[role]) return new Response("role already taken", { status: 409 });

    if (this.createdAt === 0) {
      // Yeni oda: IP başına oda açma sınırı.
      const ipKey = request.headers.get("x-windrop-ipkey") ?? "unknown";
      const limiter = this.env.IP_LIMITER.get(this.env.IP_LIMITER.idFromName(ipKey));
      if (!(await limiter.hit())) {
        console.log(JSON.stringify({ event: "room_rejected", reason: "ip_rate_limit" }));
        return new Response("too many rooms", { status: 429, headers: { "retry-after": "60" } });
      }
      // await sırasında başka bir istek odayı yaratmış / rolü almış olabilir.
      if (this.dead) return new Response("gone", { status: 410 });
      if (this.sockets[role]) return new Response("role already taken", { status: 409 });
      if (this.createdAt === 0) {
        this.createdAt = this.now();
        this.lastActivity = this.createdAt;
        await this.ctx.storage.put("createdAt", this.createdAt);
        await this.ctx.storage.setAlarm(this.createdAt + LIMITS.peerWaitMs - this.clockSkewMs);
        console.log(JSON.stringify({ event: "room_created" }));
      }
      if (this.sockets[role]) return new Response("role already taken", { status: 409 });
    }

    const pair = new WebSocketPair();
    const client = pair[0];
    const server = pair[1];
    server.accept();
    server.binaryType = "arraybuffer"; // yeni compat tarihlerinde varsayılan "blob"; byteLength için gerekli
    this.sockets[role] = server;
    const other: Role = role === "pc" ? "phone" : "pc";

    server.addEventListener("message", (ev) => this.onMessage(role, ev.data));
    server.addEventListener("close", (ev) => this.onPeerClosed(role, ev.code));
    server.addEventListener("error", () => this.onPeerClosed(role, 1011));

    // Eş zaten bekliyorsa onun ön tamponunu sırayla yeni gelene boşalt.
    if (this.sockets[other] && this.buffer.length > 0) {
      const pending = this.buffer;
      this.buffer = [];
      this.bufferBytes = 0;
      for (const msg of pending) server.send(msg);
    }
    return new Response(null, { status: 101, webSocket: client });
  }

  private onMessage(from: Role, data: string | ArrayBuffer): void {
    if (this.dead) return;
    if (typeof data === "string") {
      this.closeRoom(1003, "binary only", "text_message");
      return;
    }
    const len = data.byteLength;
    if (len > LIMITS.maxMessageBytes) {
      this.closeRoom(1009, "message too big", "message_too_big");
      return;
    }
    this.lastActivity = this.now();
    this.totalBytes += len;
    this.messages++;
    if (this.totalBytes > LIMITS.roomTotalBytes) {
      this.closeRoom(1008, "room byte limit", "byte_limit");
      return;
    }
    const to: Role = from === "pc" ? "phone" : "pc";
    const dest = this.sockets[to];
    if (dest) {
      dest.send(data);
      return;
    }
    // Eş henüz yok: ≤ 1 MiB tampon.
    if (this.bufferBytes + len > LIMITS.preBufferBytes) {
      this.closeRoom(1008, "buffer limit", "buffer_overflow");
      return;
    }
    this.buffer.push(data);
    this.bufferBytes += len;
  }

  private onPeerClosed(role: Role, code: number): void {
    if (this.dead) return;
    // Bir taraf kapandı: oda biter, diğer taraf da kapatılır (Noise akışı sürdürülemez).
    const sendable = code === 1000 || (code >= 3000 && code <= 4999) ? code : 1001;
    this.sockets[role] = undefined;
    this.closeRoom(sendable, "peer closed", "peer_closed");
  }

  private closeRoom(code: number, reason: string, counter: string): void {
    if (this.dead) return;
    this.dead = true;
    for (const r of ["pc", "phone"] as const) {
      const s = this.sockets[r];
      this.sockets[r] = undefined;
      if (!s) continue;
      try {
        s.close(code, reason);
      } catch {
        /* zaten kapalı */
      }
    }
    this.buffer = [];
    this.bufferBytes = 0;
    console.log(
      JSON.stringify({ event: "room_closed", reason: counter, bytes: this.totalBytes, messages: this.messages }),
    );
    this.ctx.waitUntil(
      (async () => {
        await this.ctx.storage.put("dead", true);
        await this.ctx.storage.deleteAlarm();
      })(),
    );
  }

  async alarm(): Promise<void> {
    if (this.dead) return;
    const now = this.now();
    const paired = !!(this.sockets.pc && this.sockets.phone);
    const lifetimeEnd = this.createdAt + LIMITS.roomLifetimeMs;
    const peerEnd = this.createdAt + LIMITS.peerWaitMs;
    const idleEnd = this.lastActivity + LIMITS.idleMs;
    if (now >= lifetimeEnd) return this.closeRoom(1008, "lifetime", "lifetime");
    if (!paired && now >= peerEnd) return this.closeRoom(1008, "peer timeout", "peer_timeout");
    if (now >= idleEnd) return this.closeRoom(1008, "idle", "idle");
    const next = Math.min(lifetimeEnd, idleEnd, paired ? Infinity : peerEnd);
    // Gerçek zamana çevir (sahte zaman farkı çıkarılır).
    await this.ctx.storage.setAlarm(Math.max(Date.now() + 1000, next - this.clockSkewMs));
  }
}

// ---------------------------------------------------------------- IP sınırlayıcı
/**
 * IP (karma) başına kayan pencere: son 60 sn'de ≤ 10 oda. Ücretsiz planda çalışan SQLite tabanlı
 * Durable Object; Workers Rate Limiting binding'i yerine seçildi (README'de gerekçe).
 */
export class IpLimiter extends DurableObject<Env> {
  private hits: number[] | null = null;

  async hit(): Promise<boolean> {
    const now = Date.now();
    if (this.hits === null) this.hits = (await this.ctx.storage.get<number[]>("hits")) ?? [];
    this.hits = this.hits.filter((t) => now - t < LIMITS.ipWindowMs);
    if (this.hits.length >= LIMITS.roomsPerIpPerWindow) return false;
    this.hits.push(now);
    await this.ctx.storage.put("hits", this.hits);
    await this.ctx.storage.setAlarm(now + LIMITS.ipWindowMs + 1000);
    return true;
  }

  async alarm(): Promise<void> {
    const now = Date.now();
    this.hits = (this.hits ?? (await this.ctx.storage.get<number[]>("hits")) ?? []).filter(
      (t) => now - t < LIMITS.ipWindowMs,
    );
    if (this.hits.length === 0) {
      this.hits = null;
      await this.ctx.storage.deleteAll();
    } else {
      await this.ctx.storage.put("hits", this.hits);
      await this.ctx.storage.setAlarm(this.hits[0] + LIMITS.ipWindowMs + 1000);
    }
  }
}
