#!/usr/bin/env python3
"""候補棄却診断のoffline比較。原画像・fieldセットは読取専用、出力は指定した外部ディレクトリのみ。"""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict
import hashlib
import json
import os
from pathlib import Path
import statistics
import subprocess

TOOLS = Path(__file__).resolve().parent
PHONE = TOOLS.parents[2]
GIT = PHONE.parent
SOURCE = PHONE / "ios/PhoneSaberSender/PhoneSaberSender"
TESTS = PHONE / "ios/PhoneSaberSenderTests"
CLIPS = ["185641_219", "185751_917", "185819_969", "185918_288", "190035_098"]


def command(args, **kwargs):
    return subprocess.check_output([str(v) for v in args], **kwargs)


def build(output, revision):
    baseline = output / "baseline"
    baseline.mkdir(exist_ok=True)
    for name in ("DetectionCore.swift", "BGRADetection.swift"):
        relative = (SOURCE / name).relative_to(GIT)
        (baseline / name).write_bytes(command(["git", "show", f"{revision}:{relative}"], cwd=GIT))
    harness = (TESTS / "VideoDetectionDiagnostic.swift").read_text()
    before = harness.replace("                    collectComponentDiagnostics: components,\n", "")
    start = before.index("            if components {")
    end = before.index("            let encoded", start)
    before = before[:start] + before[end:]
    (baseline / "VideoDetectionDiagnostic.swift").write_text(before)
    for name, sources in (("before", [baseline / f for f in ("DetectionCore.swift", "BGRADetection.swift", "VideoDetectionDiagnostic.swift")]),
                          ("after", [SOURCE / "DetectionCore.swift", SOURCE / "BGRADetection.swift", TESTS / "VideoDetectionDiagnostic.swift"])):
        command(["xcrun", "swiftc", "-O", *sources, "-module-cache-path", output / "cache", "-o", output / name])


def measure_storage(output):
    # 本番のschema定義とFoundationのserializerを使用する。原資料は読み取りだけ。
    source = (SOURCE / "AutoClip.swift").read_text()
    schema = source.split("let componentMetadata: [String: Any] = ", 1)[1].split("\n        let autoClip:", 1)[0]
    schema = schema.split(" ? ", 1)[1].rsplit(" : ", 1)[0]
    harness = r'''import Foundation
func read(_ path: String) throws -> [String: Any] {
    try JSONSerialization.jsonObject(with: Data(contentsOf: URL(fileURLWithPath: path))) as! [String: Any]
}
func encode(_ value: Any) throws -> Data {
    try JSONSerialization.data(withJSONObject: value, options: [.sortedKeys])
}
@main enum Storage {
    static func main() throws {
        let output = URL(fileURLWithPath: CommandLine.arguments[1])
        let records = try JSONSerialization.jsonObject(with: Data(contentsOf: output.appendingPathComponent("evaluations.json"))) as! [[String: Any]]
        let clips = Dictionary(grouping: records.filter { $0["clip"] is String }, by: { $0["clip"] as! String })
        let schema: [String: Any] = SCHEMA
        var result: [String: Any] = [:]
        for (clip, rows) in clips.sorted(by: { $0.key < $1.key }) {
            let folder = URL(fileURLWithPath: rows[0]["frameContextPath"] as! String).deletingLastPathComponent().deletingLastPathComponent()
            let original = try read(folder.appendingPathComponent("summary.json").path)
            let byID = Dictionary(uniqueKeysWithValues: rows.map { ($0["frameID"] as! Int, $0["components"] as! [String: Any]) })
            var sizes: [String: Int] = [:], contexts: [String: [Int]] = [:], summaryFramesBytes: [String: [Int]] = [:]
            for variant in ["before", "off", "on"] {
                let target = output.appendingPathComponent("storage/\(clip)/\(variant)")
                try FileManager.default.createDirectory(at: target, withIntermediateDirectories: true)
                var summary = original
                if variant != "before" {
                    var auto = summary["autoClip"] as! [String: Any]
                    auto["componentDiagnostics"] = variant == "on" ? schema : ["enabled": false]
                    var frames = auto["frames"] as! [[String: Any]]
                    for i in frames.indices {
                        for color in ["red", "blue"] {
                            var entry = frames[i][color] as! [String: Any]
                            var component = variant == "on" ? byID[frames[i]["f"] as! Int]?[color] as? [String: Any] : nil
                            for key in ["rows", "columns", "sampleStep"] { component?.removeValue(forKey: key) }
                            if variant == "on" { entry["components"] = component ?? ["state": "notCollected"] }
                            frames[i][color] = entry
                        }
                    }
                    auto["frames"] = frames; summary["autoClip"] = auto
                }
                var contextSizes: [Int] = []
                for row in rows {
                    var context = try read(row["frameContextPath"] as! String)
                    if variant != "before" {
                        var frames = context["frames"] as! [[String: Any]]
                        for color in ["red", "blue"] {
                            var entry = frames[0][color] as! [String: Any]
                            var component = (row["components"] as! [String: Any])[color] as! [String: Any]
                            // context PNGは既に半解像度。原frameContextに戻すときの単位は元カメラ基準。
                            component["sampleStep"] = row["sourceSampleStep"]
                            if variant == "on" { entry["components"] = component }
                            frames[0][color] = entry
                        }
                        context["frames"] = frames
                    }
                    let data = try encode(context)
                    let saved = target.appendingPathComponent("frame_\(row["frameID"]!).json")
                    try data.write(to: saved)
                    guard NSDictionary(dictionary: try read(saved.path)).isEqual(to: context) else { fatalError("readback mismatch") }
                    guard data.count <= 32 * 1024 else { fatalError("32 KiB context exceeded") }
                    contextSizes.append(data.count)
                }
                let summaryData = try encode(summary)
                let summaryFrames = (summary["autoClip"] as! [String: Any])["frames"] as! [[String: Any]]
                summaryFramesBytes[variant] = try rows.map { row in
                    let frame = summaryFrames.first { ($0["f"] as! Int) == (row["frameID"] as! Int) }!
                    return try encode(frame).count
                }
                try summaryData.write(to: target.appendingPathComponent("summary.json"))
                guard summaryData.count <= 480 * 1024 else { fatalError("summary limit exceeded") }
                sizes[variant] = summaryData.count + contextSizes.reduce(0, +)
                contexts[variant] = contextSizes
            }
            guard contexts["off"]! == contexts["before"]!,
                  (1..<64).contains(sizes["off"]! - sizes["before"]!) else {
                fatalError("OFF diagnostics changed per-frame storage")
            }
            let deltas = zip(contexts["on"]!, contexts["off"]!).map { $0 - $1 }
            let frameDeltas = rows.indices.map { deltas[$0] + summaryFramesBytes["on"]![$0] - summaryFramesBytes["off"]![$0] }
            result[clip] = ["images": rows.count, "jsonBytes": sizes,
                "onMinusOffClipBytes": sizes["on"]! - sizes["off"]!,
                "offMinusBeforeClipBytes": sizes["off"]! - sizes["before"]!,
                "onMinusOffContextBytes": deltas,
                "onMinusOffFrameBytes": frameDeltas,
                "maximumContextBytes": contexts["on"]!.max()!]
        }
        try JSONSerialization.data(withJSONObject: result, options: [.sortedKeys, .prettyPrinted]).write(to: output.appendingPathComponent("storage.json"))
    }
}
'''.replace("SCHEMA", schema)
    path = output / "Storage.swift"
    path.write_text(harness)
    command(["xcrun", "swiftc", "-O", SOURCE / "DetectionCore.swift", SOURCE / "BGRADetection.swift", path,
             "-module-cache-path", output / "cache", "-o", output / "storage-measure"])
    command([output / "storage-measure", output])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--repeats", type=int, default=20)
    parser.add_argument("--rounds", type=int, default=3)
    parser.add_argument("--baseline", default="HEAD", help="比較元commit（再現時はhashを指定）")
    parser.add_argument("--timing-control", action="store_true", help="同じ比較元バイナリを二重に測定する対照")
    args = parser.parse_args()
    output = args.output.resolve()
    if output.is_relative_to(GIT) or args.repeats < 1 or args.rounds < 1:
        parser.error("出力はGitの外、repeats/roundsは正数を指定する")
    if os.environ.get("PHONESABER_CORELINE_GUARD", "1") != "1":
        parser.error("gateと同じCoreLineGuard有効条件で測定する")
    output.mkdir(parents=True, exist_ok=True)
    revision = command(["git", "rev-parse", "--verify", args.baseline + "^{commit}"], cwd=GIT, text=True).strip()
    build(output, revision)
    manifest = json.loads((TOOLS / "lossless_regression_manifest.json").read_text())
    inputs = [{"name": f["name"], "path": TESTS / "Fixtures" / f["path"], "settings": f.get("shutterEmitter"), "clip": None, "sampleStep": 2}
              for f in manifest["fixtures"]]
    inbox = Path.home() / "Library/Application Support/PhoneSaber/diagnostics-inbox/autoclips"
    for clip in CLIPS:
        folder = inbox / f"phone_saber_triage_autoclip_20261027_{clip}_gap_red"
        summary = json.loads((folder / "summary.json").read_text())
        camera = summary["autoClip"]["camera"]
        for image in summary["images"]:
            context = json.loads((folder / image["frameContextPath"]).read_text())
            frame = context["frames"][0]
            assert image["frameID"] == frame["frameID"] == context["selectedFrameID"]
            assert image["timestamp"] == frame["timestamp"]
            inputs.append({"name": f"{clip}_f{image['frameID']}", "path": folder / image["path"], "clip": clip,
                           "role": image["role"], "sampleStep": 2 if image["role"] == "event" else 1,
                           "sourceSampleStep": round((2 if image["role"] == "event" else 1) / image["scale"]),
                           "frameContextPath": folder / image["frameContextPath"],
                           "frameID": frame["frameID"], "timestamp": frame["timestamp"],
                           "settings": {"shutter": camera["shutter"], "iso": camera["shutterISO"], "enabled": camera["shutterEmitter"]}})
    timings = defaultdict(list)
    timing_samples = []
    records = []
    for input_index, item in enumerate(inputs):
        dimensions = command(["ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=p=0", item["path"]], text=True).strip().split(",")
        raw = command(["ffmpeg", "-v", "error", "-i", item["path"], "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "bgra", "-"])
        suffix = [*dimensions, str(item["sampleStep"])]
        if item["settings"]:
            mode = item["settings"]
            suffix += [str(mode["shutter"]), str(mode["iso"]), str(int(mode["enabled"]))]
        results = {}
        for round_index in range(args.rounds):
            if args.timing_control:
                # 4ラウンドで位置と直前のvariantを均等にする。
                variants = ["before", "off", "on", "control"]
                plan = [(0, 1, 3, 2), (1, 2, 0, 3), (2, 3, 1, 0), (3, 0, 2, 1)]
                order = [variants[i] for i in plan[round_index % 4]]
            else:
                order = ["before", "off", "on"] if round_index % 2 == 0 else ["on", "off", "before"]
            for variant in order:
                env = {**os.environ, "PHONESABER_COMPONENT_DIAGNOSTICS": str(int(variant == "on")),
                       "PHONESABER_LIGHTWEIGHT": "1", "PHONESABER_EMITTER_METRICS": "1", "PHONESABER_BIT_PATTERNS": "1"}
                data = command([output / ("before" if variant in ("before", "control") else "after"), *suffix], input=raw * (args.repeats + 3), env=env)
                rows = [json.loads(line) for line in data.splitlines()]
                results[variant] = rows[-1]
                for row in rows:
                    assert row["colors"] == rows[-1]["colors"], item["name"]
                samples = [row["profile"]["total_ms"] for row in rows[3:]]
                timings[variant].extend(samples)
                timing_samples.append({"input": item["name"], "round": round_index, "variant": variant, "ms": samples})
            assert all(v["colors"] == results["before"]["colors"] for v in results.values()), item["name"]
        entry = {k: str(v) if isinstance(v, Path) else v for k, v in item.items()}
        entry["sha256"] = hashlib.sha256(item["path"].read_bytes()).hexdigest()
        entry["components"] = results["on"]["components"]
        entry["colors"] = results["on"]["colors"]
        records.append(entry)
        if (input_index + 1) % 10 == 0:
            print(f"compared {input_index + 1}/{len(inputs)} inputs", flush=True)
    def percentile(values, p):
        return sorted(values)[min(len(values)-1, int(len(values)*p))]
    report = {"scope": "offline re-evaluation; no historical rejection trace or recognition improvement is implied",
              "storageScope": "JSON field additions reconstructed on original metadata with Foundation serialization; PNG bytes unchanged; not a device recorder run",
              "baselineCommit": revision,
              "sourceSHA256": {name: hashlib.sha256((SOURCE / name).read_bytes()).hexdigest()
                               for name in ("DetectionCore.swift", "BGRADetection.swift", "AutoClip.swift", "FrameProcessor.swift")},
              "inputs": len(inputs), "formalImages": len(manifest["fixtures"]),
              "eventImages": sum(r.get("role") == "event" for r in records),
              "contextImages": sum(r.get("role") == "context" for r in records),
              "bitIdenticalColors": True, "repeats": args.repeats, "rounds": args.rounds,
              "timingControl": args.timing_control,
              "timingsMs": {k: {"n": len(v), "p50": statistics.median(v), "p95": percentile(v, .95)} for k, v in timings.items()},
              "clips": {clip: {"eventImages": sum(r["clip"] == clip and r.get("role") == "event" for r in records),
                                "contextImages": sum(r["clip"] == clip and r.get("role") == "context" for r in records),
                                "redComponentReasons": dict(Counter(row[2] for r in records if r["clip"] == clip for row in r["components"]["red"]["rows"]))} for clip in CLIPS}}
    (output / "evaluations.json").write_text(json.dumps(records, indent=2) + "\n")
    (output / "timing-samples.json").write_text(json.dumps(timing_samples) + "\n")
    measure_storage(output)
    report["storage"] = json.loads((output / "storage.json").read_text())
    (output / "measurement.json").write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
