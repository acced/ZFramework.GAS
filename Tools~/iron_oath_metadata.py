#!/usr/bin/env python3
"""Create missing deterministic Unity metadata for the optional game sample.

Existing metadata is never rewritten (especially scene MonoBehaviour GUIDs).
Use --check in CI to detect omissions without writing files.
"""
import argparse
import re
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SAMPLE = ROOT / "Samples~" / "IronOath"


def metadata(path):
    guid = uuid.uuid5(uuid.NAMESPACE_URL, "https://github.com/acced/ZFramework.GAS/" + path.relative_to(ROOT).as_posix()).hex
    head = "fileFormatVersion: 2\nguid: " + guid + "\n"
    if path.is_dir():
        return head + "folderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"
    if path.suffix == ".cs":
        return head + "MonoImporter:\n  externalObjects: {}\n  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n  icon: {instanceID: 0}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"
    if path.suffix == ".asmdef":
        return head + "AssemblyDefinitionImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"
    if path.suffix == ".png":
        return head + """TextureImporter:
  internalIDToNameTable: []
  externalObjects: {}
  serializedVersion: 12
  mipmaps:
    mipMapMode: 0
    enableMipMap: 0
    sRGBTexture: 1
  isReadable: 1
  streamingMipmaps: 0
  textureFormat: 1
  maxTextureSize: 2048
  textureSettings:
    serializedVersion: 2
    filterMode: 1
    aniso: 1
    mipBias: 0
    wrapU: 1
    wrapV: 1
    wrapW: 1
  nPOTScale: 0
  lightmap: 0
  compressionQuality: 70
  spriteMode: 0
  spritePixelsToUnits: 100
  alphaIsTransparency: 0
  alphaSource: 0
  textureType: 0
  textureShape: 1
  singleChannelComponent: 0
  platformSettings:
  - serializedVersion: 3
    buildTarget: DefaultTexturePlatform
    maxTextureSize: 2048
    resizeAlgorithm: 0
    textureFormat: -1
    textureCompression: 1
    compressionQuality: 70
    crunchedCompression: 0
    allowsAlphaSplitting: 0
    overridden: 0
  userData: Original generated environment plate; no characters or interface.
  assetBundleName:
  assetBundleVariant:
"""
    if path.suffix == ".otf":
        return head + """TrueTypeFontImporter:
  externalObjects: {}
  serializedVersion: 4
  fontSize: 24
  forceTextureCase: -2
  characterSpacing: 0
  characterPadding: 1
  includeFontData: 1
  fontName: Iron Oath Serif
  fontNames:
  - Iron Oath Serif
  fallbackFontReferences: []
  customCharacters:
  fontRenderingMode: 0
  ascentCalculationMode: 1
  useLegacyBoundsCalculation: 0
  shouldRoundAdvanceValue: 1
  userData: OFL font subset; see adjacent OFL.txt.
  assetBundleName:
  assetBundleVariant:
"""
    return head + "DefaultImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    missing = []
    for path in [SAMPLE, *sorted(SAMPLE.rglob("*"))]:
        if path.suffix == ".meta":
            continue
        meta = Path(str(path) + ".meta")
        if meta.exists():
            continue
        missing.append(str(path.relative_to(ROOT)))
        if not args.check:
            meta.write_text("\n".join(line.rstrip() for line in metadata(path).splitlines()) + "\n", encoding="utf-8")
    if args.check and missing:
        raise SystemExit("Missing Unity metadata:\n" + "\n".join(missing))
    guids = {}
    for meta in SAMPLE.rglob("*.meta"):
        match = re.search(r"^guid: ([0-9a-f]{32})$", meta.read_text(), re.M)
        if not match:
            raise SystemExit("Invalid GUID: " + str(meta))
        guid = match.group(1)
        if guid in guids:
            raise SystemExit("Duplicate GUID: " + str(meta) + " and " + str(guids[guid]))
        guids[guid] = meta
    for scene in SAMPLE.rglob("*.unity"):
        for guid in re.findall(r"m_Script:.*?guid: ([0-9a-f]{32})", scene.read_text()):
            if guid not in guids or not str(guids[guid]).endswith(".cs.meta"):
                raise SystemExit("Scene script reference is missing: " + str(scene))
    print("PASS: Iron Oath metadata complete." if args.check else f"Created {len(missing)} missing metadata files.")


if __name__ == "__main__":
    main()
