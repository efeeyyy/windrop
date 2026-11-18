// APNs push bildirimi (docs/PROTOCOL.md §9). Cihaz belirteci YALNIZ PushRegistry deposunda durur;
// belirteç ve pushId asla loglanmaz (yalnız toplu sayaçlar).
import { DurableObject } from "cloudflare:workers";
import type { Env } from "./index";

export const PUSH_LIMITS = {
  /** pushId başına saatte en çok bu kadar bildirim. */
  perPushIdPerHour: 10,
  hourMs: 60 * 60 * 1000,
  maxFromChars: 40,
  maxBodyBytes: 2048,
  /** APNs sağlayıcı JWT'si en çok 60 dk geçerli sayılır; 50 dk sonra yenilenir. */
  jwtCacheMs: 50 * 60 * 1000,
  apnsTimeoutMs: 8000,
};

const TOKEN_RE = /^[0-9a-fA-F]{64,200}$/;
const PUSH_ID_PATH = /^\/v1\/push\/([0-9a-f]{32})$/;

type PushEnv = "sandbox" | "production";

export type ClaimResult =
  | { status: "ok"; token: string; env: PushEnv }
  | { status: "unknown" }
  | { status: "limited"; retryAfterSec: number };

// ---------------------------------------------------------------- Kayıt defteri
/**
 * pushId -> deviceToken eşlemesi ve pushId başına saatlik sayaç. Tek, adlandırılmış örnek
 * (SQLite tabanlı); eşzamanlı çağrılar sıralıdır, `storage.sql` işlemleri eşzamanlıdır.
 */
export class PushRegistry extends DurableObject<Env> {
  /** Yalnız test için sahte zaman: Date.now() değerine eklenir. */
  public clockSkewMs = 0;

  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    ctx.storage.sql.exec(
      "CREATE TABLE IF NOT EXISTS push (push_id TEXT PRIMARY KEY, token TEXT NOT NULL UNIQUE, env TEXT NOT NULL)",
    );
    ctx.storage.sql.exec("CREATE TABLE IF NOT EXISTS hits (push_id TEXT NOT NULL, ts INTEGER NOT NULL)");
    ctx.storage.sql.exec("CREATE INDEX IF NOT EXISTS hits_push ON hits (push_id)");
  }

  private now(): number {
    return Date.now() + this.clockSkewMs;
  }

  /** Kaydı ekler; aynı belirteç için eski pushId (ve sayacı) silinir. */
  register(pushId: string, token: string, env: PushEnv): void {
    const sql = this.ctx.storage.sql;
    sql.exec("DELETE FROM hits WHERE push_id IN (SELECT push_id FROM push WHERE token = ?)", token);
    sql.exec("DELETE FROM push WHERE token = ?", token);
    sql.exec("INSERT INTO push (push_id, token, env) VALUES (?, ?, ?)", pushId, token, env);
  }

  /** Gönderim hakkı ister: kayıt varsa ve saatlik sınır aşılmadıysa sayacı artırıp belirteci verir. */
  claim(pushId: string): ClaimResult {
    const sql = this.ctx.storage.sql;
    const rows = sql.exec("SELECT token, env FROM push WHERE push_id = ?", pushId).toArray();
    if (rows.length === 0) return { status: "unknown" };
    const now = this.now();
    sql.exec("DELETE FROM hits WHERE ts <= ?", now - PUSH_LIMITS.hourMs);
    const hits = sql.exec("SELECT ts FROM hits WHERE push_id = ? ORDER BY ts", pushId).toArray();
    if (hits.length >= PUSH_LIMITS.perPushIdPerHour) {
      const oldest = Number(hits[0].ts);
      const retryAfterSec = Math.max(1, Math.ceil((oldest + PUSH_LIMITS.hourMs - now) / 1000));
      return { status: "limited", retryAfterSec };
    }
    sql.exec("INSERT INTO hits (push_id, ts) VALUES (?, ?)", pushId, now);
    return { status: "ok", token: String(rows[0].token), env: rows[0].env as PushEnv };
  }

  remove(pushId: string): void {
    const sql = this.ctx.storage.sql;
    sql.exec("DELETE FROM hits WHERE push_id = ?", pushId);
    sql.exec("DELETE FROM push WHERE push_id = ?", pushId);
  }
}

// ---------------------------------------------------------------- JWT (ES256)
function b64url(data: ArrayBuffer | Uint8Array | string): string {
  const bytes = typeof data === "string" ? new TextEncoder().encode(data) : new Uint8Array(data);
  let s = "";
  for (const b of bytes) s += String.fromCharCode(b);
  return btoa(s).replaceAll("+", "-").replaceAll("/", "_").replaceAll("=", "");
}

/** .p8 PEM (PKCS#8) metninden gövdeyi çıkarır; wrangler'a "\n" biçiminde yapıştırılmış olabilir. */
function pemToDer(pem: string): Uint8Array<ArrayBuffer> {
  const body = pem
    .replaceAll("\\n", "\n")
    .replace(/-----[^-]+-----/g, "")
    .replace(/\s+/g, "");
  const bin = atob(body);
  const der = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) der[i] = bin.charCodeAt(i);
  return der;
}

/** Sağlayıcı JWT'sini üretir (WebCrypto ECDSA P-256/SHA-256; imza ham r||s, JWT'nin istediği biçim). */
export async function buildApnsJwt(
  keyP8: string,
  keyId: string,
  teamId: string,
  iatSec: number,
): Promise<string> {
  const key = await crypto.subtle.importKey(
    "pkcs8",
    pemToDer(keyP8),
    { name: "ECDSA", namedCurve: "P-256" },
    false,
    ["sign"],
  );
  const header = b64url(JSON.stringify({ alg: "ES256", kid: keyId }));
  const payload = b64url(JSON.stringify({ iss: teamId, iat: iatSec }));
  const input = `${header}.${payload}`;
  const sig = await crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, key, new TextEncoder().encode(input));
  return `${input}.${b64url(sig)}`;
}

let jwtCache: { id: string; jwt: string; at: number } | null = null;

/** 50 dk önbellekli JWT. Önbellek anahtarı sırların kendisidir (değişirse yeniden üretilir). */
export async function getApnsJwt(
  keyP8: string,
  keyId: string,
  teamId: string,
  nowMs = Date.now(),
): Promise<string> {
  const id = `${keyId}|${teamId}|${keyP8}`;
  if (jwtCache && jwtCache.id === id && nowMs - jwtCache.at < PUSH_LIMITS.jwtCacheMs && nowMs >= jwtCache.at) {
    return jwtCache.jwt;
  }
  const jwt = await buildApnsJwt(keyP8, keyId, teamId, Math.floor(nowMs / 1000));
  jwtCache = { id, jwt, at: nowMs };
  return jwt;
}

/** Yalnız test için. */
export function resetApnsJwtCache(): void {
  jwtCache = null;
}

// ---------------------------------------------------------------- HTTP
function text(status: number, body: string, extra?: Record<string, string>): Response {
  return new Response(body, { status, headers: { "cache-control": "no-store", ...extra } });
}

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json", "cache-control": "no-store" },
  });
}

/** "from" alanını temizler: denetim/biçim karakterleri atılır, ≤ 40 karakter. Boş kalırsa null. */
export function sanitizeFrom(raw: unknown): string | null {
  if (typeof raw !== "string") return null;
  const cleaned = raw.replace(/[\p{Cc}\p{Cf}\p{Zl}\p{Zp}]/gu, "").trim();
  const clipped = Array.from(cleaned).slice(0, PUSH_LIMITS.maxFromChars).join("").trim();
  return clipped.length > 0 ? clipped : null;
}

/** Gövde sınırı aşıldı (413). */
const TOO_LARGE = Symbol("too-large");

/** Gövdeyi en çok `maxBodyBytes` bayt okur: önce content-length, sonra akış okuma sınırı. */
async function readBodyLimited(request: Request): Promise<string | typeof TOO_LARGE> {
  const max = PUSH_LIMITS.maxBodyBytes;
  const cl = request.headers.get("content-length");
  if (cl !== null && (!/^[0-9]+$/.test(cl) || Number(cl) > max)) return TOO_LARGE;
  if (!request.body) return "";
  const reader = request.body.getReader();
  const chunks: Uint8Array[] = [];
  let total = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    total += value.byteLength;
    if (total > max) {
      await reader.cancel().catch(() => undefined);
      return TOO_LARGE;
    }
    chunks.push(value);
  }
  const all = new Uint8Array(total);
  let off = 0;
  for (const c of chunks) {
    all.set(c, off);
    off += c.byteLength;
  }
  return new TextDecoder().decode(all);
}

async function readJson(request: Request): Promise<Record<string, unknown> | null | typeof TOO_LARGE> {
  try {
    const raw = await readBodyLimited(request);
    if (raw === TOO_LARGE) return TOO_LARGE;
    const v: unknown = JSON.parse(raw);
    return v !== null && typeof v === "object" && !Array.isArray(v) ? (v as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}

function randomHex(nBytes: number): string {
  const b = crypto.getRandomValues(new Uint8Array(nBytes));
  return [...b].map((x) => x.toString(16).padStart(2, "0")).join("");
}

/**
 * /v1/push/* işleyicisi. `ipKey` çağıranın hesapladığı IP karmasıdır (IP asla saklanmaz/loglanmaz).
 * Sırlardan biri eksikse 501.
 */
export async function handlePush(request: Request, env: Env, url: URL, ipKey: string): Promise<Response> {
  const { APNS_KEY_P8: keyP8, APNS_KEY_ID: keyId, APNS_TEAM_ID: teamId, APNS_TOPIC: topic } = env;
  if (!keyP8 || !keyId || !teamId || !topic) return text(501, "push not configured");

  const isRegister = url.pathname === "/v1/push/register";
  const m = PUSH_ID_PATH.exec(url.pathname);
  if (!isRegister && !m) return text(404, "not found");
  if (request.method !== "POST") return text(405, "method not allowed", { allow: "POST" });

  const limiter = env.IP_LIMITER.get(env.IP_LIMITER.idFromName(ipKey));
  if (!(await limiter.hit())) {
    console.log(JSON.stringify({ event: "push_rejected", reason: "ip_rate_limit" }));
    return text(429, "too many requests", { "retry-after": "60" });
  }

  const body = await readJson(request);
  if (body === TOO_LARGE) return text(413, "payload too large");
  if (!body) return text(400, "bad request");
  const registry = env.PUSH_REGISTRY.get(env.PUSH_REGISTRY.idFromName("registry"));

  if (isRegister) {
    const { deviceToken, env: apnsEnv } = body;
    if (
      typeof deviceToken !== "string" ||
      !TOKEN_RE.test(deviceToken) ||
      deviceToken.length % 2 !== 0 ||
      (apnsEnv !== "sandbox" && apnsEnv !== "production")
    ) {
      return text(400, "bad request");
    }
    const pushId = randomHex(16);
    await registry.register(pushId, deviceToken.toLowerCase(), apnsEnv);
    console.log(JSON.stringify({ event: "push_registered" }));
    return json(200, { pushId });
  }

  const from = sanitizeFrom(body.from);
  if (from === null) return text(400, "bad request");
  const pushId = m![1];
  const claim = await registry.claim(pushId);
  if (claim.status === "unknown") return text(404, "unknown pushId");
  if (claim.status === "limited") {
    console.log(JSON.stringify({ event: "push_rejected", reason: "pushid_rate_limit" }));
    return text(429, "too many pushes", { "retry-after": String(claim.retryAfterSec) });
  }

  let jwt: string;
  try {
    jwt = await getApnsJwt(keyP8, keyId, teamId);
  } catch {
    console.log(JSON.stringify({ event: "push_failed", reason: "jwt" }));
    return text(502, "push provider error");
  }

  const host = claim.env === "sandbox" ? "api.sandbox.push.apple.com" : "api.push.apple.com";
  const payload = {
    aps: { alert: { title: "WinDrop", body: `${from} dosya göndermek istiyor` }, sound: "default" },
  };
  let res: Response;
  try {
    res = await fetch(`https://${host}/3/device/${claim.token}`, {
      method: "POST",
      headers: {
        authorization: `bearer ${jwt}`,
        "apns-topic": topic,
        "apns-push-type": "alert",
        "apns-priority": "10",
        "content-type": "application/json",
      },
      body: JSON.stringify(payload),
      signal: AbortSignal.timeout(PUSH_LIMITS.apnsTimeoutMs),
    });
  } catch {
    console.log(JSON.stringify({ event: "push_failed", reason: "network" }));
    return text(502, "push provider unreachable");
  }

  if (res.status === 200) {
    await res.body?.cancel();
    console.log(JSON.stringify({ event: "push_sent" }));
    return text(200, "ok");
  }

  let reason = "";
  try {
    const j = JSON.parse(await res.text()) as { reason?: unknown };
    if (typeof j.reason === "string") reason = j.reason;
  } catch {
    /* gövde yok/geçersiz */
  }
  if (res.status === 410 || (res.status === 400 && reason === "BadDeviceToken")) {
    await registry.remove(pushId);
    console.log(JSON.stringify({ event: "push_unregistered", status: res.status }));
    return text(410, "gone");
  }
  if (res.status === 403) jwtCache = null; // süresi dolmuş/geçersiz JWT: bir sonraki istekte yenile
  console.log(JSON.stringify({ event: "push_failed", status: res.status }));
  return text(502, "push provider error");
}
