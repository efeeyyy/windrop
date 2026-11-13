import { SELF } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { rateLimitIp } from "../src/ip";

describe("rateLimitIp", () => {
  it("IPv4 aynen kalır", () => {
    expect(rateLimitIp("203.0.113.7")).toBe("203.0.113.7");
  });
  it("aynı /64 içindeki IPv6 adresleri aynı anahtarı verir", () => {
    const a = rateLimitIp("2001:db8:1:2:aaaa:bbbb:cccc:dddd");
    expect(rateLimitIp("2001:db8:1:2::1")).toBe(a);
    expect(rateLimitIp("2001:0DB8:0001:0002:0:0:0:ffff")).toBe(a);
    expect(rateLimitIp("2001:db8:1:3::1")).not.toBe(a);
  });
  it("sıkıştırılmış biçimler ve IPv4-eşlemeli adres", () => {
    expect(rateLimitIp("::1")).toBe(rateLimitIp("0:0:0:0:1:2:3:4"));
    expect(rateLimitIp("::ffff:192.0.2.9")).toBe("192.0.2.9");
  });
  it("geçersiz değer aynen kalır", () => {
    expect(rateLimitIp("a:b:::c")).toBe("a:b:::c");
    expect(rateLimitIp("unknown")).toBe("unknown");
  });
});

describe("oda hız sınırı IPv6 /64", () => {
  it("aynı /64'ten 11. oda 429; başka /64 etkilenmez", async () => {
    const open = (room: number, ip: string) =>
      SELF.fetch(`https://relay.test/v1/room/${room.toString(16).padStart(32, "a")}?role=pc`, {
        headers: { Upgrade: "websocket", "CF-Connecting-IP": ip },
      });
    const sockets: WebSocket[] = [];
    for (let i = 0; i < 10; i++) {
      const res = await open(i, `2001:db8:77:9:${i}::${i + 1}`);
      expect(res.status, `#${i}`).toBe(101);
      sockets.push(res.webSocket!);
    }
    expect((await open(100, "2001:db8:77:9:ffff::1")).status).toBe(429);
    const other = await open(101, "2001:db8:77:a::1");
    expect(other.status).toBe(101);
    sockets.push(other.webSocket!);
    for (const s of sockets) {
      s.accept();
      s.close(1000);
    }
  });
});
