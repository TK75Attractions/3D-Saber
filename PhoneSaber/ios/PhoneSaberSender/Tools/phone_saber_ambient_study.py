#!/usr/bin/env python3
"""背景下限の時間推定を保存候補で比較する調査用shadow。本番の認識は変更しない。

無視範囲・プレイ範囲・記録winnerのseedは使わない。eligible候補のpeak下限だけを
再計算するため、通常モードは画素maskの変化・未保存候補・原画素safety gateを再現しない。
--raw は保存原寸eventの画素maskも再生成するが、時系列の追跡履歴は再現しない。
出力なしは物理的な刃不在の正解ではない。clip開始前の環境推定履歴も不明。
入力JSONは [{"path": "/private/clip"}, ...]。画像・出力はGit外に保存する。
"""
from __future__ import annotations

import argparse
import json
import math
import os
from pathlib import Path
import subprocess
import tempfile

import phone_saber_stationary_replay as replay


POLICIES = ("before", "q90-2s", "q90-2s-iso-reset", "attack-decay-2s")
EMITTER_SOURCES = {"shutter-color-emitter", "shutter-magenta-emitter"}


def peak_floor(background: float) -> float:
    ramp = min(max((background - 24) / 16, 0), 1)
    return min(background * (3 + 2 * ramp), 220)


class AmbientStudyState:
    """既存EMAと実験用下限を分離する。ISOを変える操作は一切行わない。"""

    def __init__(self, policy: str):
        if policy not in POLICIES:
            raise ValueError(policy)
        self.policy = policy
        self.reset()

    def reset(self):
        self.filtered = self.stable = self.level = None
        self.history = []
        self.previous = self.previous_iso = None

    def update(self, background: int | None, time: float, iso=None):
        if not math.isfinite(time):
            raise ValueError("finite timestamp required")
        if self.previous is not None and not 0 <= time - self.previous <= .25:
            self.reset()
        if background is None:
            return None
        if type(background) is not int or not 0 <= background <= 255:
            raise ValueError("background must be an integer in 0...255")
        if self.filtered is None:
            self.filtered = self.stable = float(background)
        else:
            self.filtered += (background - self.filtered) * .25
            if abs(self.filtered - self.stable) >= 2:
                self.stable = self.filtered
        if self.policy == "q90-2s-iso-reset" and iso != self.previous_iso:
            self.history = []
        self.previous_iso = iso
        self.history = [(t, b) for t, b in self.history if time - t <= 2]
        self.history.append((time, background))
        values = sorted(b for _, b in self.history)
        quantile = values[min(int(.90 * len(values)), len(values) - 1)]
        dt = 0 if self.previous is None else time - self.previous
        if self.level is None or background > self.level:
            self.level = float(background)
        else:
            self.level = background + (self.level - background) * math.exp(-dt / 2)
        self.previous = time
        estimate = self.stable
        if self.policy.startswith("q90"):
            estimate = max(estimate, quantile)
        elif self.policy == "attack-decay-2s":
            estimate = max(estimate, self.level)
        return {"legacy": self.stable, "floorBackground": estimate,
                "peakFloor": peak_floor(estimate)}


def background_series(saved, policy):
    state = AmbientStudyState(policy)
    previous = None
    result = {}
    for frame in saved["frames"]:
        # 候補欠落による選択器resetと照度推定resetは別。実験の比較では既存replayに揃える。
        complete = all(sum(int(c[10]) & 1 != 0 for c in frame[color]["c"])
                       == frame[color]["e"] for color in ("red", "blue"))
        if not complete or (previous is not None and frame["f"] != previous + 1):
            state.reset()
        result[frame["f"]] = state.update(frame.get("backgroundMedian"), frame["t"],
                                          frame.get("appliedISO"))
        previous = frame["f"] if complete else None
    return result


def prepare_clip(path: Path, policy: str):
    saved = json.loads((path / "summary.json").read_text())["autoClip"]
    clip = replay.load_clip(path)
    clip["area"] = None
    series = background_series(saved, policy)
    for frame in clip["frames"]:
        frame["seed"] = {}
        estimate = series[frame["f"]]
        for color in ("red", "blue"):
            frame[color] = [row for row in frame["observed"][color]
                            if int(row[10]) & 1 and not (
                                estimate is not None
                                and clip["sources"][int(row[11])] in EMITTER_SOURCES
                                and row[12] < estimate["peakFloor"])]
    return clip


def floor_shadow_source(source: str) -> str:
    # 一時ビルドだけに注入する。maskの背景上限・既存赤gateには従来EMAを渡す。
    replacements = [
        ("""    let redBackground = shutterEmitter.active
        ? (backgroundState?.update(background) ?? Double(background)) : 0""",
         """    let measuredRedBackground = shutterEmitter.active
        ? (backgroundState?.update(background) ?? Double(background)) : 0
    let redBackground = Double(ProcessInfo.processInfo.environment["AMBIENT_STUDY_LEGACY"] ?? "") ?? measuredRedBackground"""),
        ("background * Double(ShutterEmitterThresholds.minimumBackgroundRatio))",
         '(Double(ProcessInfo.processInfo.environment["AMBIENT_STUDY_FLOOR"] ?? "") ?? background) * Double(ShutterEmitterThresholds.minimumBackgroundRatio))'),
        ("let peakFloor = shutterEmitterPeakFloor(background: background)",
         'let peakFloor = shutterEmitterPeakFloor(background: Double(ProcessInfo.processInfo.environment["AMBIENT_STUDY_FLOOR"] ?? "") ?? background)'),
    ]
    for old, new in replacements:
        if source.count(old) != 1:
            raise ValueError("本番コードの変更を確認し、shadow注入点を更新してください")
        source = source.replace(old, new)
    return source


def run_raw(documents):
    """保存された原寸eventだけを再認識。未保存frameの原画素は補間しない。"""
    import numpy as np
    from PIL import Image

    source = replay.SOURCE
    rows = []
    with tempfile.TemporaryDirectory(prefix="phonesaber-ambient-raw-") as directory:
        directory = Path(directory)
        shadow = directory / "BGRADetection.swift"
        shadow.write_text(floor_shadow_source((source / "BGRADetection.swift").read_text()))
        binary = directory / "detect"
        host = source.parents[1] / "PhoneSaberSenderTests/VideoDetectionDiagnostic.swift"
        subprocess.run(["xcrun", "swiftc", "-O", "-module-cache-path", str(directory / "cache"),
                        str(source / "DetectionCore.swift"), str(shadow), str(host),
                        "-o", str(binary)], check=True)
        for index, doc in enumerate(documents):
            clip = Path(doc["path"])
            summary = json.loads((clip / "summary.json").read_text())
            saved = summary["autoClip"]
            frames = {f["f"]: f for f in saved["frames"]}
            series = {p: background_series(saved, p) for p in POLICIES if p != "q90-2s-iso-reset"}
            for image in summary["images"]:
                if image.get("role") != "event":
                    continue
                fid = image["frameID"]
                frame, camera = frames[fid], saved["camera"]
                path = clip / image["path"]
                with Image.open(path) as original:
                    pixels = np.array(original.convert("RGBA"))
                if (pixels.shape[1], pixels.shape[0]) != (saved["frameSize"]["width"], saved["frameSize"]["height"]):
                    raise ValueError("原寸event以外はraw比較に使用できません")
                raw = pixels[:, :, [2, 1, 0, 3]].tobytes()
                # 記録がなければ推測せず要求ISOを使い、その区別は結果に残す。
                applied_iso = frame.get("appliedISO")
                iso = int(applied_iso or camera.get("iso", 2000))
                command = [str(binary), str(pixels.shape[1]), str(pixels.shape[0]), "2",
                           str(camera.get("shutter", 0)), str(iso),
                           "1" if camera.get("shutterEmitter") else "0"]
                results = {}
                for policy, estimates in series.items():
                    estimate = estimates[fid]
                    env = {k: v for k, v in os.environ.items()
                           if k not in ("AMBIENT_STUDY_LEGACY", "AMBIENT_STUDY_FLOOR")}
                    env["PHONESABER_EMITTER_METRICS"] = "1"
                    if estimate:
                        env.update(AMBIENT_STUDY_LEGACY=str(estimate["legacy"]),
                                   AMBIENT_STUDY_FLOOR=str(estimate["floorBackground"]))
                    results[policy] = json.loads(subprocess.check_output(command, input=raw, env=env))
                rows.append({"clipIndex": index, "clip": clip.name, "frame": fid, "path": str(path),
                             "isoSource": "appliedISO" if applied_iso else "camera.iso",
                             "estimates": {p: s[fid] for p, s in series.items()}, "analysis": results})
            print(clip.name, len(rows), flush=True)
    return rows


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("clips", type=Path, help="private clip list JSON")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--raw", action="store_true", help="原寸eventで画素maskも再生成（numpy/Pillowが必要）")
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    documents = json.loads(args.clips.read_text())
    if args.raw:
        (args.output / "raw-results.json").write_text(json.dumps(run_raw(documents)))
        return
    for policy in POLICIES:
        clips = [prepare_clip(Path(doc["path"]), policy) for doc in documents]
        result = replay.run(clips)
        (args.output / (policy + ".json")).write_text(json.dumps(result))
        print(policy, len(clips), sum(len(c["frames"]) for c in clips), flush=True)


if __name__ == "__main__":
    main()
