import { env, runInDurableObject } from "cloudflare:test";
import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import worker, { type Env } from "../src/index";
import { PUSH_LIMITS, type PushRegistry, buildApnsJwt, getApnsJwt, resetApnsJwtCache, sanitizeFrom } from "../src/push";

const TOKEN_A = "ab".repeat(32); // 64 hex
const TOKEN_B = "cd".repeat(32);

let ipCounter = 0;
function newIp(): string {
  ipCounter++;
  return `203.0.113.${ipCounter % 250}-p${ipCounter}`;
}

let keyP8 = "";
let publicKey: CryptoKey;

function pemWrap(der: ArrayBuffer): string {
  const b64 = btoa(String.fromCharCode(...new Uint8Array(der)));
  return `-----BEGIN PRIVATE KEY-----\n${b64.match(/.{1,64}/g)!.join("\n")}\n-----END PRIVATE KEY-----\n`;
}

beforeAll(async () => {
  const kp = (await crypto.subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, true, [
    "sign",
    "verify",
  ])) as CryptoKeyPair;
  keyP8 = pemWrap((await crypto.subtle.exportKey("pkcs8", kp.privateKey)) as ArrayBuffer);
  publicKey = kp.publicKey;
});

function pushEnv(over: Partial<Env> = {}): Env {
  return {
    ...(env as unknown as Env),
    APNS_KEY_P8: keyP8,
    APNS_KEY_ID: "KEYID12345",
    APNS_TEAM_ID: "TEAMID1234",
    APNS_TOPIC: "com.example.windrop",
    ...over,
  };
}

function post(path: string, body: unknown, ip = newIp()): Request {
  return new Request(`https://relay.test${path}`, {
    method: "POST",
    headers: { "CF-Connecting-IP": ip, "content-type": "application/json" },
    body: typeof body === "string" ? body : JSON.stringify(body),
  });
}

async function register(token = TOKEN_A, apnsEnv = "sandbox", e: Env = pushEnv()): Promise<string> {
  const res = await worker.fetch(post("/v1/push/register", { deviceToken: token, env: apnsEnv }), e);
  expect(res.status).toBe(200);
  const { pushId } = (await res.json()) as { pushId: string };
  expect(pushId).toMatch(/^[0-9a-f]{32}$/);
  return pushId;
}

function registryStub() {
  return env.PUSH_REGISTRY.get(env.PUSH_REGISTRY.idFromName("registry"));
}

function b64urlToBytes(s: string): Uint8Array<ArrayBuffer> {
  const bin = atob(s.replaceAll("-", "+").replaceAll("_", "/"));
  return Uint8Array.from(bin, (c) => c.charCodeAt(0));
}

afterEach(() => {
  vi.restoreAllMocks();
  resetApnsJwtCache();
});

describe("kayıt", () => {
  it("geçerli kayıt 200 {pushId}; her seferinde farklı pushId", async () => {
    const a = await register(TOKEN_A);
    const b = await register(TOKEN_B);
    expect(a).not.toBe(b);
  });

  it("yeniden kayıt: aynı belirteç için eski pushId silinir", async () => {
    const e = pushEnv();
    const fetchMock = vi.spyOn(globalThis, "fetch").mockImplementation(async () => new Response("", { status: 200 }));
    const old = await register("ef".repeat(32), "sandbox", e);
    const fresh = await register("ef".repeat(32), "production", e);
    expect(fresh).not.toBe(old);
    expect((await worker.fetch(post(`/v1/push/${old}`, { from: "PC" }), e)).status).toBe(404);
    expect((await worker.fetch(post(`/v1/push/${fresh}`, { from: "PC" }), e)).status).toBe(200);
    // büyük/küçük harf aynı belirteç sayılır
    const upper = await register("EF".repeat(32), "production", e);
    expect((await worker.fetch(post(`/v1/push/${fresh}`, { from: "PC" }), e)).status).toBe(404);
    expect((await worker.fetch(post(`/v1/push/${upper}`, { from: "PC" }), e)).status).toBe(200);
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it("geçersiz girdi 400", async () => {
    const bad: unknown[] = [
      "not json",
      "[]",
      "null",
      {},
      { deviceToken: TOKEN_A },
      { env: "sandbox" },
      { deviceToken: "ab".repeat(31), env: "sandbox" }, // 62 hex: kısa
      { deviceToken: "zz".repeat(32), env: "sandbox" }, // hex değil
      { deviceToken: TOKEN_A + "a", env: "sandbox" }, // tek uzunluk
      { deviceToken: "ab".repeat(101), env: "sandbox" }, // çok uzun
      { deviceToken: 12345, env: "sandbox" },
      { deviceToken: TOKEN_A, env: "prod" },
      { deviceToken: TOKEN_A, env: 1 },
      JSON.stringify({ deviceToken: TOKEN_A, env: "sandbox", pad: "x".repeat(PUSH_LIMITS.maxBodyBytes) }),
    ];
    for (const b of bad) {
      const res = await worker.fetch(post("/v1/push/register", b), pushEnv());
      expect(res.status, JSON.stringify(b)?.slice(0, 80)).toBe(400);
    }
  });

  it("GET 405; bilinmeyen push yolu 404; geçersiz pushId yolu 404", async () => {
    const get = new Request("https://relay.test/v1/push/register", { headers: { "CF-Connecting-IP": newIp() } });
    expect((await worker.fetch(get, pushEnv())).status).toBe(405);
    expect((await worker.fetch(post("/v1/push/xyz", {}), pushEnv())).status).toBe(404);
    expect((await worker.fetch(post(`/v1/push/${"A".repeat(32)}`, { from: "x" }), pushEnv())).status).toBe(404);
  });

  it("bilinmeyen pushId 404", async () => {
    const res = await worker.fetch(post(`/v1/push/${"0".repeat(32)}`, { from: "PC" }), pushEnv());
    expect(res.status).toBe(404);
  });
});

describe("sırlar eksikse 501", () => {
  it("her sır tek tek eksik olunca register ve gönderim 501", async () => {
    for (const k of ["APNS_KEY_P8", "APNS_KEY_ID", "APNS_TEAM_ID", "APNS_TOPIC"] as const) {
      const e = pushEnv({ [k]: undefined });
      expect((await worker.fetch(post("/v1/push/register", { deviceToken: TOKEN_A, env: "sandbox" }), e)).status).toBe(501);
      expect((await worker.fetch(post(`/v1/push/${"1".repeat(32)}`, { from: "PC" }), e)).status).toBe(501);
    }
  });

  it("varsayılan test ortamında (sır yok) 501", async () => {
    const res = await worker.fetch(post("/v1/push/register", { deviceToken: TOKEN_A, env: "sandbox" }), env as unknown as Env);
    expect(res.status).toBe(501);
  });
});

describe("from temizliği", () => {
  it("denetim karakterleri atılır, 40 karakterle sınırlanır, boş/yanlış tip reddedilir", () => {
    expect(sanitizeFrom("Efe'nin\nPC\u0000'si\t‮")).toBe("Efe'ninPC'si");
    expect(sanitizeFrom("a".repeat(100))).toBe("a".repeat(40));
    expect(Array.from(sanitizeFrom("😀".repeat(50))!).length).toBe(40);
    expect(sanitizeFrom("  \n\t ")).toBeNull();
    expect(sanitizeFrom("")).toBeNull();
    expect(sanitizeFrom(5)).toBeNull();
    expect(sanitizeFrom(undefined)).toBeNull();
  });

  it("temizlenince boş kalan from 400", async () => {
    const id = await register("11".repeat(32));
    expect((await worker.fetch(post(`/v1/push/${id}`, { from: "\u0001\u0002" }), pushEnv())).status).toBe(400);
    expect((await worker.fetch(post(`/v1/push/${id}`, {}), pushEnv())).status).toBe(400);
  });
});

describe("APNs çağrısı", () => {
  it("doğru URL, başlıklar ve gövde (sandbox)", async () => {
    const e = pushEnv();
    const id = await register("22".repeat(32), "sandbox", e);
    const fetchMock = vi.spyOn(globalThis, "fetch").mockImplementation(async () => new Response("", { status: 200 }));
    const res = await worker.fetch(post(`/v1/push/${id}`, { from: "Efe\nPC" }), e);
    expect(res.status).toBe(200);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe(`https://api.sandbox.push.apple.com/3/device/${"22".repeat(32)}`);
    expect(init.method).toBe("POST");
    const h = new Headers(init.headers);
    expect(h.get("authorization")).toMatch(/^bearer [\w-]+\.[\w-]+\.[\w-]+$/);
    expect(h.get("apns-topic")).toBe("com.example.windrop");
    expect(h.get("apns-push-type")).toBe("alert");
    expect(h.get("apns-priority")).toBe("10");
    expect(init.body).toBe(
      '{"aps":{"alert":{"title":"WinDrop","body":"EfePC dosya göndermek istiyor"},"sound":"default"}}',
    );
  });

  it("production belirteci api.push.apple.com'a gider", async () => {
    const e = pushEnv();
    const id = await register("33".repeat(32), "production", e);
    const fetchMock = vi.spyOn(globalThis, "fetch").mockImplementation(async () => new Response("", { status: 200 }));
    expect((await worker.fetch(post(`/v1/push/${id}`, { from: "PC" }), e)).status).toBe(200);
    expect(fetchMock.mock.calls[0][0]).toBe(`https://api.push.apple.com/3/device/${"33".repeat(32)}`);
  });

  it("410 gelince kayıt silinir; sonraki istek 404", async () => {
    const e = pushEnv();
    const id = await register("44".repeat(32), "sandbox", e);
    const fetchMock = vi
      .spyOn(globalThis, "fetch")
      .mockImplementation(async () => new Response('{"reason":"Unregistered"}', { status: 410 }));
    expect((await worker.fetch(post(`/v1/push/${id}`, { from: "PC" }), e)).status).toBe(410);
    expect((await worker.fetch(post(`/v1/push/${id}`, { from: "PC" }), e)).status).toBe(404);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("400 BadDeviceToken kaydı siler; başka 400 nedeni silmez", async () => {
    const e = pushEnv();
    const id = await register("55".repeat(32), "sandbox", e);
    const fetchMock = vi
      .spyOn(globalThis, "fetch")
      .mockImplementationOnce(async () => new Response('{"reason":"PayloadEmpty"}', { status: 400 }))
      .mockImplementationOnce(async () => new Response('{"reason":"BadDeviceToken"}', { status: 400 }));
    expect((await worker.fetch(post(`/v1/push/${id}`, { from: "PC" }), e)).status).toBe(502);
    expect((await worker.fetch(post(`/v1/push/${id}`, { from: "PC" }), e)).status).toBe(410);
    expect((await worker.fetch(post(`/v1/push/${id}`, { from: "PC" }), e)).status).toBe(404);
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it("ağ hatası ve 5xx 502; kayıt korunur", async () => {
    const e = pushEnv();
    const id = await register("66".repeat(32), "sandbox", e);
    vi.spyOn(globalThis, "fetch")
      .mockImplementationOnce(async () => {
        throw new Error("boom");
      })
      .mockImplementationOnce(async () => new Response("", { status: 503 }))
      .mockImplementation(async () => new Response("", { status: 200 }));
    expect((await worker.fetch(post(`/v1/push/${id}`, { from: "PC" }), e)).status).toBe(502);
    expect((await worker.fetch(post(`/v1/push/${id}`, { from: "PC" }), e)).status).toBe(502);
    expect((await worker.fetch(post(`/v1/push/${id}`, { from: "PC" }), e)).status).toBe(200);
  });

  it("belirteç ve pushId loglanmaz", async () => {
    const e = pushEnv();
    const logSpy = vi.spyOn(console, "log");
    const errSpy = vi.spyOn(console, "error");
    vi.spyOn(globalThis, "fetch").mockImplementation(async () => new Response("", { status: 410 }));
    const id = await register("77".repeat(32), "sandbox", e);
    await worker.fetch(post(`/v1/push/${id}`, { from: "PC" }), e);
    const all = JSON.stringify([...logSpy.mock.calls, ...errSpy.mock.calls]);
    expect(all).not.toContain("77".repeat(32));
    expect(all).not.toContain(id);
  });
});

describe("hız sınırları", () => {
  it("pushId başına saatte 10; 11. 429; saat geçince açılır; başka pushId etkilenmez", async () => {
    const e = pushEnv();
    const id = await register("88".repeat(32), "sandbox", e);
    const other = await register("99".repeat(32), "sandbox", e);
    vi.spyOn(globalThis, "fetch").mockImplementation(async () => new Response("", { status: 200 }));
    for (let i = 0; i < PUSH_LIMITS.perPushIdPerHour; i++) {
      expect((await worker.fetch(post(`/v1/push/${id}`, { from: "PC" }), e)).status, `#${i}`).toBe(200);
    }
    const limited = await worker.fetch(post(`/v1/push/${id}`, { from: "PC" }), e);
    expect(limited.status).toBe(429);
    expect(Number(limited.headers.get("retry-after"))).toBeGreaterThan(0);
    expect((await worker.fetch(post(`/v1/push/${other}`, { from: "PC" }), e)).status).toBe(200);
    await runInDurableObject(registryStub(), (inst: PushRegistry) => {
      inst.clockSkewMs = PUSH_LIMITS.hourMs + 1000;
    });
    expect((await worker.fetch(post(`/v1/push/${id}`, { from: "PC" }), e)).status).toBe(200);
    await runInDurableObject(registryStub(), (inst: PushRegistry) => {
      inst.clockSkewMs = 0;
    });
  });

  it("IP başına dakikada 10; 11. 429; başka IP etkilenmez", async () => {
    const ip = newIp();
    for (let i = 0; i < 10; i++) {
      const res = await worker.fetch(post("/v1/push/register", { deviceToken: TOKEN_A, env: "sandbox" }, ip), pushEnv());
      expect(res.status, `#${i}`).toBe(200);
    }
    expect((await worker.fetch(post("/v1/push/register", { deviceToken: TOKEN_A, env: "sandbox" }, ip), pushEnv())).status).toBe(429);
    expect((await worker.fetch(post("/v1/push/register", { deviceToken: TOKEN_A, env: "sandbox" }), pushEnv())).status).toBe(200);
  });
});

describe("JWT (ES256)", () => {
  it("doğru başlık/yük yapısı ve WebCrypto ile doğrulanan imza", async () => {
    const jwt = await buildApnsJwt(keyP8, "KEYID12345", "TEAMID1234", 1_700_000_000);
    const parts = jwt.split(".");
    expect(parts.length).toBe(3);
    for (const p of parts) expect(p).toMatch(/^[A-Za-z0-9_-]+$/); // dolgusuz base64url
    const dec = (s: string) => JSON.parse(new TextDecoder().decode(b64urlToBytes(s)));
    expect(dec(parts[0])).toEqual({ alg: "ES256", kid: "KEYID12345" });
    expect(dec(parts[1])).toEqual({ iss: "TEAMID1234", iat: 1_700_000_000 });
    const sig = b64urlToBytes(parts[2]);
    expect(sig.byteLength).toBe(64); // ham r||s
    const ok = await crypto.subtle.verify(
      { name: "ECDSA", hash: "SHA-256" },
      publicKey,
      sig,
      new TextEncoder().encode(`${parts[0]}.${parts[1]}`),
    );
    expect(ok).toBe(true);
    // değiştirilmiş yük doğrulanmaz
    const tampered = await crypto.subtle.verify(
      { name: "ECDSA", hash: "SHA-256" },
      publicKey,
      sig,
      new TextEncoder().encode(`${parts[0]}.${parts[1]}x`),
    );
    expect(tampered).toBe(false);
  });

  it("literal \\n içeren (wrangler'a tek satır yapıştırılmış) PEM de kabul edilir", async () => {
    const oneLine = keyP8.trim().replaceAll("\n", "\\n");
    const jwt = await buildApnsJwt(oneLine, "K", "T", 1);
    expect(jwt.split(".").length).toBe(3);
  });

  it("50 dk önbelleklenir, sonra yenilenir; sır değişince yeniden üretilir", async () => {
    const t0 = 1_700_000_000_000;
    const a = await getApnsJwt(keyP8, "K1", "T1", t0);
    expect(await getApnsJwt(keyP8, "K1", "T1", t0 + PUSH_LIMITS.jwtCacheMs - 1000)).toBe(a);
    const b = await getApnsJwt(keyP8, "K1", "T1", t0 + PUSH_LIMITS.jwtCacheMs + 1000);
    expect(b).not.toBe(a);
    const payload = JSON.parse(new TextDecoder().decode(b64urlToBytes(b.split(".")[1])));
    expect(payload.iat).toBe(Math.floor((t0 + PUSH_LIMITS.jwtCacheMs + 1000) / 1000));
    const c = await getApnsJwt(keyP8, "K2", "T1", t0 + PUSH_LIMITS.jwtCacheMs + 2000);
    expect(c).not.toBe(b);
  });

  it("bozuk anahtarda gönderim 502 (çökmez)", async () => {
    const e = pushEnv();
    const id = await register("aa".repeat(32), "sandbox", e);
    const fetchMock = vi.spyOn(globalThis, "fetch");
    const res = await worker.fetch(post(`/v1/push/${id}`, { from: "PC" }), pushEnv({ APNS_KEY_P8: "garbage" }));
    expect(res.status).toBe(502);
    expect(fetchMock).not.toHaveBeenCalled();
  });
});
