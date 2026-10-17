#!/usr/bin/env bash
# C# <-> Swift çapraz uyum senaryoları (docs/INTEROP.md). WSL (SwiftBox) içinde, localhost üzerinde çalışır.
#   Önkoşul: swift (swiftly env), dotnet 10 (~/.dotnet). Kullanım: bash run-interop.sh [senaryo...]   (boş = hepsi)
#   Ortam: REPO (varsayılan /mnt/c/Users/Efe/Desktop/WinDrop), WORK (varsayılan /root/interop-work), NOBUILD=1 derlemeyi atlar.
set -u -o pipefail
REPO=${REPO:-/mnt/c/Users/Efe/Desktop/WinDrop}
WORK=${WORK:-/root/interop-work}
export PATH="$HOME/.dotnet:$PATH"
[ -f "$HOME/.local/share/swiftly/env.sh" ] && . "$HOME/.local/share/swiftly/env.sh"
SW=/root/swift-build/release/windrop-interop
CS="dotnet /root/artifacts/bin/WinDrop.Interop/release/WinDrop.Interop.dll"

if [ "${NOBUILD:-0}" != 1 ]; then
  (cd "$REPO/ios/WinDropKit" && swift build -c release --product windrop-interop --scratch-path /root/swift-build 2>&1 | tail -3) || exit 9
  (cd "$REPO/windows" && dotnet build tools/WinDrop.Interop -c Release --artifacts-path /root/artifacts -v q 2>&1 | tail -4) || exit 9
fi

# Sabit test anahtarları (yalnız bu deneme için; gerçek anahtar değil).
PC_KEY=$(printf '11%.0s' $(seq 32))
PH_KEY=$(printf '22%.0s' $(seq 32))
ST_KEY=$(printf '33%.0s' $(seq 32))
PC_PUB=$($CS pubkey --my-key $PC_KEY | sed 's/^PUB=//')
PH_PUB=$($SW pubkey --my-key $PH_KEY | sed 's/^PUB=//')
ST_PUB=$($SW pubkey --my-key $ST_KEY | sed 's/^PUB=//')
echo "KEYS pc_pub=$PC_PUB phone_pub=$PH_PUB"

mkdir -p "$WORK"; cd "$WORK" || exit 9
rm -rf src; mkdir src
printf 'merhaba WinDrop\nçapraz uyum\n' > src/small.txt
head -c 3000000 /dev/urandom > src/medium.bin
head -c 26214400 /dev/urandom > src/big25MB.bin
head -c 200000000 /dev/urandom > src/huge200MB.bin
FILES3="src/small.txt src/medium.bin src/big25MB.bin"
TOTAL3=$(cat $FILES3 | wc -c)

newid() { head -c16 /dev/urandom | base64 | tr '+/' '-_' | tr -d '=\n'; }
PASS=0; FAIL=0
verdict() { # ad, 0/1
  if [ "$2" = 0 ]; then echo "RESULT $1 PASS"; PASS=$((PASS+1)); else echo "RESULT $1 FAIL"; FAIL=$((FAIL+1)); fi
}
start_server() { # log, args...
  local log=$1; shift
  : > "$log"
  $CS serve "$@" > "$log" 2>&1 &
  SRV=$!
  for _ in $(seq 100); do grep -q '^LISTENING' "$log" && return 0; sleep 0.1; done
  echo "sunucu başlamadı"; cat "$log"; return 1
}
sha_of() { sha256sum "$1" | cut -d' ' -f1; }
check_files() { # log, dosyalar... ; her dosyanın SHA-256 değeri günlükteki FILE satırıyla aynı mı
  local log=$1; shift; local ok=0
  for f in "$@"; do
    local n; n=$(basename "$f"); local s; s=$(sha_of "$f")
    if grep -q "^FILE id=[0-9]* name=$n size=$(stat -c %s "$f") sha256=$s" "$log"; then echo "  SHA ok $n $s"; else echo "  SHA YOK/FARKLI $n (beklenen $s)"; ok=1; fi
  done
  return $ok
}
sas_of() { grep -o 'sas=[0-9]*' "$1" | head -1; }

sc_signals() {
  echo "== signals: JSON uyumu (pairInfo dahil) =="
  $SW signals --out sw.jsonl && $CS signals --out cs.jsonl
  echo "-- Swift satırları C# ile çözülüyor"; $CS signals-verify --file sw.jsonl; local a=$?
  echo "-- C# satırları Swift ile çözülüyor"; $SW signals-verify --file cs.jsonl; local b=$?
  verdict signals $((a+b))
}

sc_a() {
  echo "== (a) IK, Swift->C#, 3 dosya (25 MB dahil) =="
  local id; id=$(newid); local log=a-cs.log
  start_server $log --port 53601 --my-key $PC_KEY --peer-pub $PH_PUB --transfer-id $id --pattern ik --out a-out --count 3 --total $TOTAL3 --timeout 60 || { verdict a 1; return; }
  $SW send --host 127.0.0.1 --port 53601 --prefix-transfer-id $id --pattern ik --my-key $PH_KEY --peer-pub $PC_PUB $FILES3 | tee a-sw.log
  wait $SRV; local rc=$?
  cat $log | grep -v '^LOG' ; check_files $log $FILES3; local c=$?
  verdict a $((rc + c))
}

sc_b() {
  echo "== (b) XX, Swift->C#, SAS karşılaştırması =="
  local id; id=$(newid); local log=b-cs.log
  start_server $log --port 53602 --my-key $PC_KEY --peer-pub $PH_PUB --transfer-id $id --pattern xx --out b-out --count 3 --total $TOTAL3 --timeout 60 || { verdict b 1; return; }
  $SW send --host 127.0.0.1 --port 53602 --prefix-transfer-id $id --pattern xx --my-key $PH_KEY --peer-pub $PC_PUB $FILES3 | tee b-sw.log
  wait $SRV; local rc=$?
  cat $log | grep -v '^LOG'; check_files $log $FILES3; local c=$?
  local s1 s2; s1=$(sas_of b-sw.log); s2=$(sas_of $log); echo "  SAS swift: $s1  C#: $s2"
  local d=0; [ -n "$s1" ] && [ "$s1" = "$s2" ] || d=1
  local h1 h2; h1=$(grep -o 'hash=[0-9a-f]*' b-sw.log | head -1); h2=$(grep -o 'hash=[0-9a-f]*' $log | head -1); [ "$h1" = "$h2" ] || d=1
  verdict b $((rc + c + d))
}

sc_c() {
  echo "== (c) C#->Swift 2 dosya (IK ve XX) =="
  local res=0
  for pat in ik xx; do
    local id; id=$(newid); local log=c-$pat-cs.log; local two="src/small.txt src/medium.bin"; local tot; tot=$(cat $two | wc -c)
    start_server $log --port 53603 --mode send --my-key $PC_KEY --peer-pub $PH_PUB --transfer-id $id --pattern $pat --timeout 60 $two || { res=1; continue; }
    rm -rf c-$pat-out
    $SW receive-from --host 127.0.0.1 --port 53603 --prefix-transfer-id $id --pattern $pat --my-key $PH_KEY --peer-pub $PC_PUB --out c-$pat-out --count 2 --total $tot | tee c-$pat-sw.log
    wait $SRV; local rc=$?
    grep -v '^LOG' $log
    local ok=0
    for f in $two; do
      local n; n=$(basename "$f")
      if grep -q "^FILE id=[0-9]* name=$n size=$(stat -c %s "$f") sha256=$(sha_of "$f")" c-$pat-sw.log; then echo "  SHA ok ($pat) $n"; else echo "  SHA FARKLI ($pat) $n"; ok=1; fi
    done
    [ "$pat" = xx ] && { [ "$(sas_of c-xx-sw.log)" = "$(sas_of $log)" ] && echo "  SAS eşit $(sas_of $log)" || { echo "  SAS FARKLI"; ok=1; }; }
    res=$((res + rc + ok))
  done
  verdict c $res
}

sc_d() {
  echo "== (d) yanlış transferId reddi (+ yanlış anahtar, desen uyuşmazlığı) =="
  local id wrong; id=$(newid); wrong=$(newid); local res=0
  echo "-- d1: yanlış transferId"
  start_server d1-cs.log --port 53604 --my-key $PC_KEY --peer-pub $PH_PUB --transfer-id $id --pattern ik --out d-out --count 1 --total 100 --expect-completed 0 --timeout 5 || { verdict d 1; return; }
  $SW send --host 127.0.0.1 --port 53604 --prefix-transfer-id $wrong --pattern ik --my-key $PH_KEY --peer-pub $PC_PUB src/small.txt | tee d1-sw.log; local sr=${PIPESTATUS[0]}
  wait $SRV; local rc=$?; grep -E '^(LOG|SERVER_DONE)' d1-cs.log
  [ $sr -ne 0 ] && grep -q 'tanınmayan transferId' d1-cs.log && [ $rc = 0 ] && echo "  d1 ok" || { echo "  d1 BAŞARISIZ (swift rc=$sr, server rc=$rc)"; res=1; }
  echo "-- d2: doğru transferId, yabancı anahtar"
  start_server d2-cs.log --port 53605 --my-key $PC_KEY --peer-pub $PH_PUB --transfer-id $id --pattern ik --out d-out --count 1 --total 100 --expect-completed 0 --timeout 5 || { verdict d 1; return; }
  $SW send --host 127.0.0.1 --port 53605 --prefix-transfer-id $id --pattern ik --my-key $ST_KEY --peer-pub $PC_PUB src/small.txt | tee d2-sw.log; sr=${PIPESTATUS[0]}
  wait $SRV; rc=$?; grep -E '^(LOG|SERVER_DONE)' d2-cs.log
  [ $sr -ne 0 ] && grep -q 'güvenilmeyen' d2-cs.log && [ $rc = 0 ] && echo "  d2 ok" || { echo "  d2 BAŞARISIZ (swift rc=$sr, server rc=$rc)"; res=1; }
  echo "-- d3: BLE oturumu IK iken TCP'de XX"
  start_server d3-cs.log --port 53606 --my-key $PC_KEY --peer-pub $PH_PUB --transfer-id $id --pattern ik --out d-out --count 1 --total 100 --expect-completed 0 --timeout 5 || { verdict d 1; return; }
  $SW send --host 127.0.0.1 --port 53606 --prefix-transfer-id $id --pattern xx --my-key $PH_KEY --peer-pub $PC_PUB src/small.txt | tee d3-sw.log; sr=${PIPESTATUS[0]}
  wait $SRV; rc=$?; grep -E '^(LOG|SERVER_DONE)' d3-cs.log
  [ $sr -ne 0 ] && grep -q 'el sıkışma deseni' d3-cs.log && [ $rc = 0 ] && echo "  d3 ok" || { echo "  d3 BAŞARISIZ (swift rc=$sr, server rc=$rc)"; res=1; }
  verdict d $res
}

sc_e() {
  echo "== (e) Swift gönderen ortada SIGKILL, yeniden başlat, C# devam ettirir =="
  local id; id=$(newid); local log=e-cs.log; local big=src/huge200MB.bin; local size; size=$(stat -c %s $big)
  start_server $log --port 53607 --my-key $PC_KEY --peer-pub $PH_PUB --transfer-id $id --pattern ik --out e-out --count 1 --total $size --timeout 120 || { verdict e 1; return; }
  echo "-- 1. çalıştırma (60 MB'ta SIGKILL)"
  $SW send --host 127.0.0.1 --port 53607 --prefix-transfer-id $id --pattern ik --my-key $PH_KEY --peer-pub $PC_PUB --kill-at-bytes 60000000 $big | tee e-sw1.log
  echo "  swift çıkış kodu: ${PIPESTATUS[0]} (137 = SIGKILL)"
  sleep 1
  echo "-- 2. çalıştırma (devam)"
  $SW send --host 127.0.0.1 --port 53607 --prefix-transfer-id $id --pattern ik --my-key $PH_KEY --peer-pub $PC_PUB $big | tee e-sw2.log
  wait $SRV; local rc=$?
  grep -v '^LOG' $log
  check_files $log $big; local c=$?
  local off; off=$(grep -o 'RESUME file=1 offset=[0-9]*' e-sw2.log | grep -o '[0-9]*$' | head -1)
  local d=1; [ -n "${off:-}" ] && [ "$off" -gt 0 ] && d=0
  echo "  devam ofseti (2. çalıştırma): ${off:-yok}"
  verdict e $((rc + c + d))
}

ALL="signals a b c d e"
for s in ${@:-$ALL}; do sc_$s; done
echo "SUMMARY pass=$PASS fail=$FAIL"
[ $FAIL = 0 ]
