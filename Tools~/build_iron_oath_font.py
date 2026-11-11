#!/usr/bin/env python3
"""Rebuild the committed, renamed OFL font subset after adding Chinese UI text.

Requires fonttools and NotoSerifCJK-Regular.ttc (Debian fonts-noto-cjk or upstream).
The game does not need Python or the source font installed.
"""
import argparse
from pathlib import Path
from fontTools import subset
from fontTools.ttLib import TTCollection

ROOT = Path(__file__).resolve().parents[1]
SAMPLE = ROOT / "Samples~" / "IronOath"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=Path("/usr/share/fonts/opentype/noto/NotoSerifCJK-Regular.ttc"))
    args = parser.parse_args()
    collection = TTCollection(args.source)
    font = next(f for f in collection.fonts if f["name"].getDebugName(1) == "Noto Serif CJK SC")
    text = "".join(chr(i) for i in range(32, 127)) + "\u3000\u00a0…—–·×÷≤≥→←↑↓◆◇●○★☆✓✕"
    for path in SAMPLE.rglob("*.cs"):
        text += path.read_text(encoding="utf-8")
    options = subset.Options()
    options.name_IDs = [0, 1, 2, 3, 4, 5, 6, 13, 14, 16, 17]
    options.name_legacy = True
    options.name_languages = [0x409]
    sub = subset.Subsetter(options=options)
    sub.populate(text=text)
    sub.subset(font)
    names = {1: "Iron Oath Serif", 2: "Regular", 3: "IronOathSerif-Regular-1.0",
             4: "Iron Oath Serif Regular", 6: "IronOathSerif-Regular", 16: "Iron Oath Serif", 17: "Regular"}
    for name in font["name"].names:
        if name.nameID in names:
            name.string = names[name.nameID].encode(name.getEncoding())
    if "CFF " in font:
        cff = font["CFF "].cff
        cff.fontNames = ["IronOathSerif-Regular"]
        cff.topDictIndex[0].FamilyName = "Iron Oath Serif"
        cff.topDictIndex[0].FullName = "Iron Oath Serif Regular"
    target = SAMPLE / "Resources" / "IronOath" / "Fonts" / "IronOathSerif.otf"
    target.parent.mkdir(parents=True, exist_ok=True)
    font.save(target)
    cmap = font.getBestCmap()
    missing = sorted({ch for ch in text if ord(ch) >= 0x3000 and ch not in "\ufeff" and ord(ch) not in cmap})
    print(f"Saved {target.relative_to(ROOT)}: {len(cmap)} codepoints, {target.stat().st_size} bytes")
    if missing:
        raise SystemExit("Missing authored glyphs: " + "".join(missing))


if __name__ == "__main__":
    main()
