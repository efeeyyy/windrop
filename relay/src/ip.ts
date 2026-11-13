// IP normalleştirme: hız sınırı anahtarı için. IPv6'da tek abone genelde bir /64 bloğu alır;
// bu yüzden ilk 64 bit kullanılır. IPv4 (ve IPv4-eşlemeli IPv6) ve tanınmayan değerler aynen kalır.

/** IPv6 metnini 8 gruba açar; geçersizse null. */
function expandIpv6(ip: string): number[] | null {
  let s = ip.split("%")[0].toLowerCase(); // zone id at
  let tail: number[] = [];
  const v4 = /(\d+)\.(\d+)\.(\d+)\.(\d+)$/.exec(s);
  if (v4) {
    const o = v4.slice(1).map(Number);
    if (o.some((n) => n > 255)) return null;
    tail = [(o[0] << 8) | o[1], (o[2] << 8) | o[3]];
    s = s.slice(0, v4.index) + "0:0";
  }
  const halves = s.split("::");
  if (halves.length > 2) return null;
  const parse = (p: string): number[] | null => {
    if (p === "") return [];
    const out: number[] = [];
    for (const g of p.split(":")) {
      if (!/^[0-9a-f]{1,4}$/.test(g)) return null;
      out.push(parseInt(g, 16));
    }
    return out;
  };
  const head = parse(halves[0]);
  const rest = halves.length === 2 ? parse(halves[1]) : [];
  if (!head || !rest) return null;
  let groups: number[];
  if (halves.length === 2) {
    const fill = 8 - head.length - rest.length;
    if (fill < 1) return null;
    groups = [...head, ...new Array<number>(fill).fill(0), ...rest];
  } else {
    groups = head;
  }
  if (groups.length !== 8) return null;
  if (tail.length) groups.splice(6, 2, ...tail);
  return groups;
}

/** Hız sınırı için IP anahtarı: IPv6 -> "v6/64:xxxx:xxxx:xxxx:xxxx", IPv4 -> aynen. */
export function rateLimitIp(ip: string): string {
  if (!ip.includes(":")) return ip;
  const g = expandIpv6(ip);
  if (!g) return ip;
  // ::ffff:a.b.c.d (IPv4-eşlemeli) -> IPv4
  if (g.slice(0, 5).every((x) => x === 0) && g[5] === 0xffff) {
    return `${g[6] >> 8}.${g[6] & 255}.${g[7] >> 8}.${g[7] & 255}`;
  }
  return "v6/64:" + g.slice(0, 4).map((x) => x.toString(16)).join(":");
}
