"""String Catalog denetimi: her .xcstrings geçerli JSON mu, her anahtarın tr + en değeri var mı?

Kullanım:  py -3 ios/App/Localization/check-xcstrings.py
Çıkış kodu 0 = eksik çeviri yok.
"""
import json
import pathlib
import sys

LANGS = ("tr", "en")
here = pathlib.Path(__file__).resolve().parent


def has_value(loc):
    """stringUnit değeri ya da çoğul varyasyonlarında en az `other` dolu mu?"""
    unit = loc.get("stringUnit")
    if unit is not None:
        return bool(unit.get("value", "").strip())
    plural = loc.get("variations", {}).get("plural")
    if plural:
        return bool(plural.get("other", {}).get("stringUnit", {}).get("value", "").strip())
    return False


bad = 0
for path in sorted(here.rglob("*.xcstrings")):
    doc = json.loads(path.read_text(encoding="utf-8"))
    strings = doc["strings"]
    missing = []
    for key, entry in strings.items():
        locs = entry.get("localizations", {})
        for lang in LANGS:
            if lang not in locs or not has_value(locs[lang]):
                missing.append((key, lang))
    print(f"{path.relative_to(here)}: {len(strings)} anahtar, eksik çeviri {len(missing)}")
    for key, lang in missing:
        print(f"  EKSİK [{lang}] {key[:80]}")
    bad += len(missing)
sys.exit(1 if bad else 0)
