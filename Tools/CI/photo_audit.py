#!/usr/bin/env python3
"""Image diagnostics: do not report a blank frame as a successful map render."""
import argparse
import json
from pathlib import Path
from PIL import Image, ImageStat

def audit(folder):
    images = sorted(folder.glob("*.png"))
    if not images:
        raise RuntimeError("No PNG photographs: render NOT verified")
    frames, suspicious = [], []
    for path in images:
        with Image.open(path) as image:
            image.verify()
        with Image.open(path) as image:
            if image.size != (1920,1080):
                raise RuntimeError(f"{path.name}: expected HD 1920x1080; got {image.size}")
            thumb = image.convert("RGB").resize((160,90))
            pixels = list(thumb.getdata())
            black = sum(max(p)<18 for p in pixels)/len(pixels)
            white = sum(min(p)>242 for p in pixels)/len(pixels)
            # Reject widespread navy-blue URP failures without mistaking normal sky
            # or sea for missing terrain. This is heuristic, not a Unity render proof.
            blue_black = sum(r < 70 and g < 95 and 25 < b < 165 and
                             b > r + 18 and b > g + 12
                             for r, g, b in pixels)/len(pixels)
            # Reject an overwhelming Unity missing-shader magenta tint.
            error_magenta = sum(r > 175 and b > 175 and g < 105 and
                                r > g * 1.7 and b > g * 1.7
                                for r, g, b in pixels)/len(pixels)
            stddev = ImageStat.Stat(thumb).stddev
            frame = {"name":path.name,"bytes":path.stat().st_size,
                     "black_fraction":round(black,4),"white_fraction":round(white,4),
                     "blue_black_fraction":round(blue_black,4),
                     "error_magenta_fraction":round(error_magenta,4),
                     "rgb_stddev":[round(x,2) for x in stddev]}
            if (black>.93 or white>.93 or max(stddev)<3 or
                    blue_black>.85 or error_magenta>.18):
                suspicious.append(path.name)
            frames.append(frame)
    report={"count":len(frames),"frames":frames,"suspected_blank":suspicious,
            "result":"suspect" if suspicious else "passed"}
    (folder/"photo-audit.json").write_text(json.dumps(report,indent=2)+"\n")
    print(json.dumps(report,indent=2))
    if suspicious:
        raise RuntimeError("Potential black/white/flat frame: "+", ".join(suspicious))

if __name__=="__main__":
    parser=argparse.ArgumentParser()
    parser.add_argument("--directory",default="ci-photos")
    args=parser.parse_args()
    audit(Path(args.directory))
