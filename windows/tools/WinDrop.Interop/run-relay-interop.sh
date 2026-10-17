#!/usr/bin/env bash
# C# <-> Swift aktarma (relay) çapraz uyum senaryoları (docs/INTEROP.md, "Aktarma sunucusu"). macOS (CI) ve Linux'ta çalışır (bash 3.2 uyumlu).
#   Önkoşul: yerel aktarma sunucusu çalışıyor (`cd relay && npx wrangler dev --local --port 8787`), derlenmiş iki araç.
#   Ortam: RELAY_URL (varsayılan ws://127.0.0.1:8787), SW (windrop-interop yolu), CS_DLL (WinDrop.Interop.dll yolu),
#          WORK (çalışma klasörü), REPO. Kullanım: bash run-relay-interop.sh [r1 r2 r3]   (boş = hepsi)
#   Çıktı: her senaryo için `RESULT relay-<ad> PASS|FAIL`; biri FAIL ise çıkış kodu 1.
set -u -o pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
REPO=${REPO:-$(cd "$HERE/../../.." && pwd)}
RELAY_URL=${RELAY_URL:-ws://127.0.0.1:8787}
HTTP_URL=$(echo "$RELAY_URL" | sed -e 's#^wss://#https://#' -e 's#^ws://#http://#')
WORK=${WORK:-$(mktemp -d)}
SW=${SW:-$(cd "$REPO/ios/WinDropKit" && swift build --show-bin-path)/windrop-interop}
CS_DLL=${CS_DLL:-$REPO/windows/tools/WinDrop.Interop/bin/Release/net10.0/WinDrop.Interop.dll}
[ -x "$SW" ] || { echo "windrop-interop (Swift) yok: $SW"; exit 9; }
[ -f "$CS_DLL" ] || { echo "WinDrop.Interop.dll yok: $CS_DLL"; exit 9; }
CS() { dotnet "$CS_DLL" "$@"; }

# Sabit test anahtarları (yalnız bu deneme için; gerçek anahtar değil).
PC_KEY=$(printf '11%.0s' $(seq 32))
PH_KEY=$(printf '22%.0s' $(seq 32))
PC_PUB=$(CS pubkey --my-key "$PC_KEY" | sed 's/^PUB=//')
PH_PUB=$("$SW" pubkey --my-key "$PH_KEY" | sed 's/^PUB=//')
echo "KEYS pc_pub=$PC_PUB phone_pub=$PH_PUB relay=$RELAY_URL"

mkdir -p "$WORK"; cd "$WORK" || exit 9
rm -rf src out-*; mkdir src
b64url() { base64 | tr '+/' '-_' | tr -d '=\n'; }
newid() { head -c16 /dev/urandom | b64url; }       # 22 karakter
newtoken() { head -c32 /dev/urandom | b64url; }     # 43 karakter
sha_of() { if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }
size_of() { wc -c < "$1" | tr -d ' '; }
PASS=0; FAIL=0
verdict() { # ad, 0/1
  if [ "$2" = 0 ]; then echo "RESULT relay-$1 PASS"; PASS=$((PASS+1)); else echo "RESULT relay-$1 FAIL"; FAIL=$((FAIL+1)); fi
}
dump() { echo "  --- $1 ---"; sed 's/^/  | /' "$1" | head -40; }

# Sunucu hazır mı?
ready=1
for _ in $(seq 60); do
  if [ "$(curl -s -o /dev/null -w '%{http_code}' --max-time 2 "$HTTP_URL/healthz")" = 200 ]; then ready=0; break; fi
  sleep 1
done
[ $ready = 0 ] || { echo "aktarma sunucusu hazır değil: $HTTP_URL/healthz"; exit 9; }

# wait_pid PID SANİYE -> 0: bitti (çıkış kodu $WAIT_RC'de), 1: zaman aşımı (öldürüldü)
wait_pid() {
  local pid=$1 n=$(( $2 * 10 )) i=0
  while kill -0 "$pid" 2>/dev/null; do
    i=$((i+1)); [ $i -gt $n ] && { kill "$pid" 2>/dev/null; sleep 0.5; kill -9 "$pid" 2>/dev/null; wait "$pid" 2>/dev/null; WAIT_RC=124; return 1; }
    sleep 0.1
  done
  wait "$pid"; WAIT_RC=$?; return 0
}
wait_line() { # günlük, desen, saniye
  local i=0
  while [ $i -lt $(( $3 * 10 )) ]; do grep -q "$2" "$1" && return 0; sleep 0.1; i=$((i+1)); done
  return 1
}
# hash=<64 hex> değeri iki günlükte de aynı mı (aynı el sıkışma özeti), sas de aynı mı
same_handshake() {
  local a b
  a=$(grep -o '^HANDSHAKE=ok.*' "$1" | head -1 | sed 's/^HANDSHAKE=ok kind=[a-z]* //')
  b=$(grep -o '^HANDSHAKE=ok.*' "$2" | head -1 | sed 's/^HANDSHAKE=ok kind=[a-z]* //')
  [ -n "$a" ] && [ "$a" = "$b" ] && { echo "  el sıkışma özeti ve SAS aynı: ${a%% hash=*}"; return 0; }
  echo "  EL SIKIŞMA FARKLI: [$a] vs [$b]"; return 1
}

# r1: IK, Swift phone (gönderen) -> C# pc (alan), 5 MB
sc_r1() {
  local tid tok f=src/r1-5MB.bin n=5242880 rc=0
  head -c $n /dev/urandom > $f
  tid=$(newid); tok=$(newtoken)
  mkdir -p out-r1
  CS relay-pc --relay-url "$RELAY_URL" --token "$tok" --my-key "$PC_KEY" --peer-pub "$PH_PUB" --transfer-id "$tid" \
     --pattern ik --mode receive --out out-r1 --count 1 --total $n --timeout 90 > r1-pc.log 2>&1 &
  local pcpid=$!
  if ! wait_line r1-pc.log '^RELAY_READY' 30; then echo "  C# odaya girmedi"; dump r1-pc.log; kill $pcpid 2>/dev/null; return 1; fi
  "$SW" relay-phone --relay-url "$RELAY_URL" --token "$tok" --mode send --transfer-id "$tid" --pattern ik \
     --my-key "$PH_KEY" --peer-pub "$PC_PUB" $f > r1-phone.log 2>&1 &
  local phpid=$!
  wait_pid $phpid 120 || { echo "  Swift zaman aşımı"; rc=1; }; local phrc=$WAIT_RC
  wait_pid $pcpid 120 || { echo "  C# zaman aşımı"; rc=1; }; local pcrc=$WAIT_RC
  grep -q '^OUTCOME=completed' r1-phone.log || { echo "  Swift OUTCOME completed değil"; rc=1; }
  grep -q '^OUTCOME=Completed' r1-pc.log || { echo "  C# OUTCOME Completed değil"; rc=1; }
  [ "$phrc" = 0 ] && [ "$pcrc" = 0 ] || { echo "  çıkış kodları swift=$phrc c#=$pcrc"; rc=1; }
  local sha; sha=$(sha_of $f)
  if grep -q "^FILE id=[0-9]* name=r1-5MB.bin size=$(size_of $f) sha256=$sha" r1-pc.log; then echo "  SHA ok $sha"; else echo "  SHA YOK/FARKLI (beklenen $sha)"; rc=1; fi
  same_handshake r1-phone.log r1-pc.log || rc=1
  [ $rc = 0 ] || { dump r1-phone.log; dump r1-pc.log; }
  return $rc
}

# r2: XX, C# pc (gönderen) -> Swift phone (alan), 2 MB
sc_r2() {
  local tid tok f=src/r2-2MB.bin n=2097152 rc=0
  head -c $n /dev/urandom > $f
  tid=$(newid); tok=$(newtoken)
  mkdir -p out-r2
  CS relay-pc --relay-url "$RELAY_URL" --token "$tok" --my-key "$PC_KEY" --peer-pub "$PH_PUB" --transfer-id "$tid" \
     --pattern xx --mode send --timeout 90 $f > r2-pc.log 2>&1 &
  local pcpid=$!
  if ! wait_line r2-pc.log '^RELAY_READY' 30; then echo "  C# odaya girmedi"; dump r2-pc.log; kill $pcpid 2>/dev/null; return 1; fi
  "$SW" relay-phone --relay-url "$RELAY_URL" --token "$tok" --mode receive --transfer-id "$tid" --pattern xx \
     --my-key "$PH_KEY" --peer-pub "$PC_PUB" --out out-r2 --count 1 --total $n > r2-phone.log 2>&1 &
  local phpid=$!
  wait_pid $phpid 120 || { echo "  Swift zaman aşımı"; rc=1; }; local phrc=$WAIT_RC
  wait_pid $pcpid 120 || { echo "  C# zaman aşımı"; rc=1; }; local pcrc=$WAIT_RC
  grep -q '^OUTCOME=completed' r2-phone.log || { echo "  Swift OUTCOME completed değil"; rc=1; }
  grep -q '^OUTCOME=Completed' r2-pc.log || { echo "  C# OUTCOME Completed değil"; rc=1; }
  [ "$phrc" = 0 ] && [ "$pcrc" = 0 ] || { echo "  çıkış kodları swift=$phrc c#=$pcrc"; rc=1; }
  local sha; sha=$(sha_of $f)
  if grep -q "^FILE id=[0-9]* name=r2-2MB.bin size=$(size_of $f) sha256=$sha" r2-phone.log; then echo "  SHA ok $sha"; else echo "  SHA YOK/FARKLI (beklenen $sha)"; rc=1; fi
  same_handshake r2-phone.log r2-pc.log || rc=1
  [ $rc = 0 ] || { dump r2-phone.log; dump r2-pc.log; }
  return $rc
}

# r3: aynı odaya üçüncü bağlantı (ve aynı rolü ikinci kez isteyen) 409 ile reddedilir
ws_status() { # roomId rol -> HTTP durum kodu (101 = yükseltme kabul; bağlantı --max-time ile kesilir)
  curl -s -o /dev/null -w '%{http_code}' --max-time "${3:-3}" \
    -H 'Connection: Upgrade' -H 'Upgrade: websocket' -H 'Sec-WebSocket-Version: 13' \
    -H 'Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==' "$HTTP_URL/v1/room/$1?role=$2"
}
sc_r3() {
  local room rc=0 a b c d
  room=$(head -c16 /dev/urandom | od -An -tx1 | tr -d ' \n')
  ws_status "$room" pc 12 > r3-a.txt & local pa=$!
  sleep 1
  ws_status "$room" phone 12 > r3-b.txt & local pb=$!
  sleep 1
  c=$(ws_status "$room" pc); d=$(ws_status "$room" phone)
  wait $pa $pb 2>/dev/null
  a=$(cat r3-a.txt); b=$(cat r3-b.txt)
  echo "  1. bağlantı(pc)=$a 2. bağlantı(phone)=$b 3. bağlantı(pc)=$c 3. bağlantı(phone)=$d"
  [ "$a" = 101 ] && [ "$b" = 101 ] || { echo "  ilk iki bağlantı kabul edilmedi"; rc=1; }
  [ "$c" = 409 ] && [ "$d" = 409 ] || { echo "  üçüncü bağlantı 409 ile reddedilmedi"; rc=1; }
  return $rc
}

WANT=${*:-r1 r2 r3}
for s in $WANT; do
  echo "=== $s ==="
  "sc_$s"; verdict "$s" $?
done
echo "TOPLAM pass=$PASS fail=$FAIL"
[ $FAIL = 0 ]
