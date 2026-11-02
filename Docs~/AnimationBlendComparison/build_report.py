"""Build the standalone report from real Unity camera frames; requires Pillow."""
import base64
import hashlib
import io
import json
import math
from pathlib import Path
from PIL import Image, ImageChops, ImageStat

HERE = Path(__file__).resolve().parent
RAW = HERE.parents[1] / ".build~" / "blend-capture" / "raw"
report = json.loads((RAW / "capture.json").read_text(encoding="utf-8"))
width, height = report["width"], report["height"]

def distance(a, b):
    return math.sqrt(sum((a[k] - b[k]) ** 2 for k in "xyz")) * 100

report["columns"] = 8
for sequence in report["sequences"]:
    sequence["differences"] = []
    for off, on in zip(sequence["off"], sequence["on"]):
        a = Image.open(RAW / off["image"]).convert("RGB")
        b = Image.open(RAW / on["image"]).convert("RGB")
        diff = ImageChops.difference(a, b)
        channels = diff.split()
        max_channel = ImageChops.lighter(ImageChops.lighter(channels[0], channels[1]), channels[2])
        changed = sum(max_channel.histogram()[9:])
        sequence["differences"].append({
            "tipCm": round(distance(off["swordTip"], on["swordTip"]), 3),
            "handCm": round(distance(off["rightHand"], on["rightHand"]), 3),
            "headCm": round(distance(off["head"], on["head"]), 3),
            "pixelPercent": round(changed / (width * height) * 100, 3),
            "identical": diff.getbbox() is None,
        })
    sequence["maxFrame"] = max(range(len(sequence["differences"])), key=lambda i: sequence["differences"][i]["tipCm"])
    for variant in ("off", "on"):
        frames = sequence[variant]
        sheet = Image.new("RGB", (width * 8, height * math.ceil(len(frames) / 8)), (29, 32, 38))
        for index, frame in enumerate(frames):
            image = Image.open(RAW / frame["image"]).convert("RGB")
            sheet.paste(image, ((index % 8) * width, (index // 8) * height))
        stream = io.BytesIO()
        sheet.save(stream, format="WEBP", quality=94, method=5)
        # Lossless PNGs remain under .build~/blend-capture/raw for exact reproduction.
        sequence[variant + "Sheet"] = "data:image/webp;base64," + base64.b64encode(stream.getvalue()).decode("ascii")

report["previewIdentical"] = all(d["identical"] for s in report["sequences"] if s["preview"] for d in s["differences"])
compact = json.dumps(report, ensure_ascii=False, separators=(",", ":")).replace("</", "<\\/")
template = (HERE / "comparison.template.html").read_text(encoding="utf-8")
output = HERE.parent / "AnimationBlendComparison.html"
output.write_text(template.replace("__CAPTURE_DATA__", compact), encoding="utf-8")
summary = {"frames": sum(len(s["on"]) * 2 for s in report["sequences"]), "previewIdentical": report["previewIdentical"], "sequences": []}
for s in report["sequences"]:
    entry = {k:s[k] for k in ("id", "label", "blendIn", "blendOut", "duration", "cancelAt", "maxFrame")}
    entry["maxDifference"] = s["differences"][s["maxFrame"]]
    entry["lastStates"] = [s[v][-1]["state"] for v in ("off", "on")]
    entry["transitionFrames"] = [f["frame"] for f in s["on"] if f["transition"]]
    summary["sequences"].append(entry)
(HERE / "measurements.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
print(json.dumps(summary, ensure_ascii=False, indent=2))
print(f"Wrote {output} ({output.stat().st_size / 1024 / 1024:.2f} MiB)")
