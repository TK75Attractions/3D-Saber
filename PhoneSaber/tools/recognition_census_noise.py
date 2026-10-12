#!/usr/bin/env python3
"""屋内2記録の同座標・壁領域の時間差分。sensor noiseの測定ではない。"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import tempfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    tmp = Path(os.environ.get("TMPDIR", tempfile.gettempdir())).resolve()
    if not args.output.resolve().is_relative_to(tmp):
        parser.error("derived output must stay under TMPDIR")
    from PIL import Image
    import numpy as np
    root = Path.home() / "Library/Application Support/PhoneSaber/diagnostics-inbox"
    result = []
    # ORIGINALで壁を確認したROI。画角が少し異なるので厳密なpaired比較ではない。
    roi = (20, 380, 60, 420)
    for suffix, ids in (("004003_239", range(2522, 2530)), ("005910_095", range(2138, 2146))):
        bundles = list(root.glob("*" + suffix))
        if len(bundles) != 1:
            raise ValueError("noise cohort missing or ambiguous")
        bundle = bundles[0]
        summary = json.loads((bundle / "summary.json").read_text())
        patches, hashes = [], []
        for frame in ids:
            candidates = [i for i in summary["images"] if i["frameID"] == frame and "annotated" not in i["path"]]
            if len(candidates) != 1:
                raise ValueError("noise frame missing or ambiguous")
            path = (bundle / candidates[0]["path"]).resolve()
            if not path.is_relative_to(bundle.resolve()):
                raise ValueError("image escaped read-only bundle")
            hashes.append(hashlib.sha256(path.read_bytes()).hexdigest())
            with Image.open(path) as image:
                if image.size != (480, 640):
                    raise ValueError("noise ROI requires 480x640 original")
                patches.append(np.array(image.convert("RGB").crop(roi), dtype=float))
        stack = np.array(patches)
        luma = stack[..., 0]*.2126 + stack[..., 1]*.7152 + stack[..., 2]*.0722
        delta = np.diff(luma, axis=0)
        delta -= delta.mean(axis=(1, 2), keepdims=True)
        result.append({"session": summary["sessionID"], "frames": list(ids), "image_sha256": hashes,
                       "roi": roi, "mean_luma": float(luma.mean()),
                       "temporal_difference_sd_over_sqrt2": float(np.median(np.std(delta, axis=(1, 2)))/np.sqrt(2)),
                       "definition": "8 processed PNGs, 7 adjacent differences; mean offset removed. Includes camera movement, lighting and ISP."})
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2))
    print(json.dumps([{k: v for k, v in r.items() if k != "image_sha256"} for r in result]))


if __name__ == "__main__":
    main()
