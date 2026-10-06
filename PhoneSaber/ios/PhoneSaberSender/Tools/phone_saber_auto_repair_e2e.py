#!/usr/bin/env python3
"""Run one isolated, measured PhoneSaber repair E2E against a local bare origin.

The real checkout is read-only. Recognition changes, commits, and pushes happen
only in a newly created /tmp clone. This intentionally uses the production
triage and repair entry points without changing their gates.
"""

import argparse
import importlib.util
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path


REPO = Path(__file__).resolve().parents[3]
TARGETS = (
    "device_normal_blue_1249",
    "production_blue_frame_0220",
    "production_blue_frame_0600",
)
CONTROLS = (
    "no_blade_blue_103",
    "background_blue_131",
    "blue_broad_coreless_64",
)
FAILURE_TYPES = {
    TARGETS[0]: "blue-dropout-device",
    TARGETS[1]: "blue-dropout-production",
    TARGETS[2]: "blue-dropout-production",
    CONTROLS[0]: "blue-control-no-blade",
    CONTROLS[1]: "blue-control-background",
    CONTROLS[2]: "blue-control-broad-coreless",
}
DETECTOR = Path("ios/PhoneSaberSender/PhoneSaberSender/BGRADetection.swift")
CORE = Path("ios/PhoneSaberSender/PhoneSaberSender/DetectionCore.swift")
DIAGNOSTIC = Path("ios/PhoneSaberSenderTests/VideoDetectionDiagnostic.swift")
RUNNER = Path("ios/PhoneSaberSender/Tools/run_lossless_regression.py")
TRIAGE = Path("ios/PhoneSaberSender/Tools/phone_saber_triage_codex.py")
REPAIR = Path("ios/PhoneSaberSender/Tools/phone_saber_auto_repair.py")
MANIFEST = Path("ios/PhoneSaberSender/Tools/lossless_regression_manifest.json")


class E2EError(RuntimeError):
    pass


def command(args, *, cwd=None, env=None, timeout=None, allowed=(0,), live=False):
    result = subprocess.run(args, cwd=cwd, env=env, text=True,
                            stdout=None if live else subprocess.PIPE,
                            stderr=None if live else subprocess.PIPE,
                            timeout=timeout, check=False)
    if result.returncode not in allowed:
        raise E2EError("command failed (exit {}): {}\n{}".format(
            result.returncode, " ".join(map(str, args)),
            ((result.stdout or "") + (result.stderr or ""))[-3000:]))
    return result


def git(repo, *args):
    return command(["git", "-C", str(repo), *args]).stdout.strip()


def require_temp(path):
    resolved = Path(path).resolve()
    if not resolved.is_relative_to(Path("/tmp").resolve()) or resolved == Path("/tmp").resolve():
        raise E2EError("temporary checkout must stay inside /tmp")
    return resolved


def replace_once(source, before, after):
    if source.count(before) != 1:
        raise E2EError("recognition source layout changed; refusing mutation")
    return source.replace(before, after, 1)


def real_snapshot():
    if git(REPO, "rev-parse", "--show-toplevel") != str(REPO.parent.resolve()):
        raise E2EError("script is outside its expected Git repository")
    snapshot = {
        "head": git(REPO, "rev-parse", "HEAD"),
        "originMain": git(REPO, "rev-parse", "origin/main"),
        "status": command(["git", "--no-optional-locks", "-C", str(REPO),
                           "status", "--porcelain"]).stdout.strip(),
        "aheadBehind": git(REPO, "rev-list", "--left-right", "--count", "HEAD...origin/main"),
        "branch": git(REPO, "branch", "--show-current"),
    }
    if snapshot["branch"] != "main" or snapshot["status"] or \
            snapshot["head"] != snapshot["originMain"] or \
            snapshot["aheadBehind"].split() != ["0", "0"]:
        raise E2EError("real main must be clean and synchronized before E2E")
    return snapshot


def assert_local_remote(clone, bare):
    require_temp(clone)
    require_temp(bare)
    remotes = git(clone, "remote", "-v")
    if "github.com" in remotes.lower():
        raise E2EError("GitHub remote found in temporary clone")
    lines = remotes.splitlines()
    expected = str(Path(bare).resolve())
    if len(lines) != 2 or any(line.split()[0] != "origin" or
                              line.split()[1] != expected for line in lines):
        raise E2EError("temporary clone must have only the local bare origin")


def assert_synced(clone, bare):
    assert_local_remote(clone, bare)
    if git(clone, "status", "--porcelain") or \
            git(clone, "rev-parse", "HEAD") != git(clone, "rev-parse", "origin/main") or \
            git(clone, "rev-list", "--left-right", "--count", "HEAD...origin/main").split() != ["0", "0"]:
        raise E2EError("temporary main is not clean and synchronized")


def push_local(clone, bare):
    assert_local_remote(clone, bare)
    command(["git", "-C", str(clone), "push", "origin", "main"])
    git(clone, "fetch", "origin")
    assert_synced(clone, bare)


def create_clone(root):
    root = require_temp(root)
    clone, bare = root / "clone", root / "origin.git"
    command(["git", "clone", "--no-local", "--branch", "main", "--single-branch",
             str(REPO.parent), str(clone)])
    if "github.com" in git(clone, "remote", "-v").lower():
        raise E2EError("GitHub remote found immediately after cloning")
    command(["git", "init", "--bare", str(bare)])
    git(clone, "remote", "remove", "origin")
    git(clone, "remote", "add", "origin", str(bare))
    assert_local_remote(clone, bare)
    push_local(clone, bare)
    return clone / "PhoneSaber", bare


def instrument_source(normal, cutoff=0):
    """Add read-only per-component observations to a copy of normal Swift."""
    text = replace_once(normal, "    let connectedComponentCount: Int\n}\n\nstruct SaberDetectionProfile",
        "    let connectedComponentCount: Int\n"
        "    let cleanedComponentSizes: [Int]\n"
        "    let additionalComponentSizes: [Int]\n"
        "    let minimumCandidateArea: Int\n"
        "    let minimumDominantComponentArea: Int\n}\n\nstruct SaberDetectionProfile")
    text = replace_once(text,
        "connectedComponentCount: 0\n                )",
        "connectedComponentCount: 0, cleanedComponentSizes: [],\n"
        "                    additionalComponentSizes: [], minimumCandidateArea: 0,\n"
        "                    minimumDominantComponentArea: 0\n                )")
    text = replace_once(text,
        """        var connectedComponentCount = 0
        let componentObserver: (Int) -> Void = { count in
            morphologyPixelCount += count
            if collectPipelineDiagnostics { connectedComponentCount += 1 }
            if collectProfile { profile.connectedComponentCount += 1 }
        }
        let additionalComponentObserver: ((Int) -> Void)? = collectProfile
            ? { _ in profile.connectedComponentCount += 1 } : nil
""",
        """        var connectedComponentCount = 0
        var cleanedComponentSizes: [Int] = []
        var additionalComponentSizes: [Int] = []
        var largestComponentArea = 0
        let componentObserver: (Int) -> Void = { count in
            morphologyPixelCount += count
            largestComponentArea = max(largestComponentArea, count)
            if collectPipelineDiagnostics {
                connectedComponentCount += 1
                cleanedComponentSizes.append(count)
            }
            if collectProfile { profile.connectedComponentCount += 1 }
        }
        let additionalComponentObserver: ((Int) -> Void)? = { count in
            largestComponentArea = max(largestComponentArea, count)
            if collectProfile { profile.connectedComponentCount += 1 }
            if collectPipelineDiagnostics { additionalComponentSizes.append(count) }
        }
""")
    text = replace_once(text,
        """                connectedComponentCount: connectedComponentCount
            )""",
        """                connectedComponentCount: connectedComponentCount,
                cleanedComponentSizes: cleanedComponentSizes,
                additionalComponentSizes: additionalComponentSizes,
                minimumCandidateArea: color == .red ? min(standardMinimumArea, 20)
                    : standardMinimumArea,
                minimumDominantComponentArea: color == .blue ? CUTOFF : 0
            )""".replace("CUTOFF", str(cutoff)))
    if cutoff:
        text = replace_once(text,
            """            profile.candidateCount += candidates.count
        }
        let selectionStart""",
            """            profile.candidateCount += candidates.count
        }
        if color == .blue && largestComponentArea < CUTOFF {
            candidates.removeAll()
        }
        let selectionStart""".replace("CUTOFF", str(cutoff)))
    return text


def instrument_diagnostic(normal):
    text = replace_once(normal,
        '        "point_count": candidate.pointCount,\n',
        '        "point_count": candidate.pointCount,\n'
        '        "component_area": candidate.componentArea,\n')
    text = replace_once(text,
        '                        "components": value?.connectedComponentCount ?? 0,\n',
        '                        "components": value?.connectedComponentCount ?? 0,\n'
        '                        "cleaned_component_sizes": value?.cleanedComponentSizes ?? [],\n'
        '                        "additional_component_sizes": value?.additionalComponentSizes ?? [],\n'
        '                        "minimum_candidate_area": value?.minimumCandidateArea ?? 0,\n'
        '                        "minimum_dominant_component_area": value?.minimumDominantComponentArea ?? 0,\n')
    return text


def mutate_clone_source(normal, cutoff):
    text = replace_once(normal,
        """        var connectedComponentCount = 0
        let componentObserver: (Int) -> Void = { count in
            morphologyPixelCount += count
            if collectPipelineDiagnostics { connectedComponentCount += 1 }
            if collectProfile { profile.connectedComponentCount += 1 }
        }
        let additionalComponentObserver: ((Int) -> Void)? = collectProfile
            ? { _ in profile.connectedComponentCount += 1 } : nil
""",
        """        var connectedComponentCount = 0
        var largestComponentArea = 0
        let componentObserver: (Int) -> Void = { count in
            morphologyPixelCount += count
            largestComponentArea = max(largestComponentArea, count)
            if collectPipelineDiagnostics { connectedComponentCount += 1 }
            if collectProfile { profile.connectedComponentCount += 1 }
        }
        let additionalComponentObserver: ((Int) -> Void)? = { count in
            largestComponentArea = max(largestComponentArea, count)
            if collectProfile { profile.connectedComponentCount += 1 }
        }
""")
    return replace_once(text,
        """            profile.candidateCount += candidates.count
        }
        let selectionStart""",
        """            profile.candidateCount += candidates.count
        }
        if color == .blue && largestComponentArea < CUTOFF {
            candidates.removeAll()
        }
        let selectionStart""".replace("CUTOFF", str(cutoff)))


def compile_diagnostic(clone, root, original_detector, cutoff=0):
    source_dir = root / ("broken-diagnostic-src" if cutoff else "normal-diagnostic-src")
    source_dir.mkdir()
    shutil.copyfile(clone / CORE, source_dir / CORE.name)
    (source_dir / DETECTOR.name).write_text(instrument_source(original_detector, cutoff))
    (source_dir / DIAGNOSTIC.name).write_text(
        instrument_diagnostic((clone / DIAGNOSTIC).read_text()))
    binary = root / ("broken-diagnostic" if cutoff else "normal-diagnostic")
    command(["xcrun", "swiftc", "-O", *(str(source_dir / name) for name in
             (CORE.name, DETECTOR.name, DIAGNOSTIC.name)), "-o", str(binary)],
            timeout=180)
    return binary


def load_runner(clone):
    spec = importlib.util.spec_from_file_location("phonesaber_e2e_runner", clone / RUNNER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def measure_corpus(clone, binary, output):
    runner = load_runner(clone)
    _, fixtures = runner.load_fixtures(clone / MANIFEST)
    analyses, rows = {}, []
    for fixture in fixtures:
        source = fixture["resolved_path"]
        if source not in analyses:
            analyses[source] = runner.analyze_png(binary, source)
        analysis = analyses[source]
        color = fixture["color"].lower()
        data, pipeline = analysis["colors"][color], analysis["pipeline"][color]
        candidates = data["candidates"]
        eligible = [item for item in candidates if item["eligible"]]
        rows.append({
            "name": fixture["name"], "path": fixture["path"],
            "color": fixture["color"], "truth": fixture["truth"],
            "passed": runner.evaluate_fixture(fixture, analysis)["passed"],
            "detected": data["selected"] is not None,
            "maskPixelCount": pipeline["mask_pixels"],
            "morphologyPixelCount": pipeline["morphology_pixels"],
            "connectedComponentCount": pipeline["components"],
            "cleanedComponentSizes": pipeline["cleaned_component_sizes"],
            "additionalComponentSizes": pipeline["additional_component_sizes"],
            "minimumCandidateArea": pipeline["minimum_candidate_area"],
            "minimumDominantComponentArea": pipeline["minimum_dominant_component_area"],
            "candidateCount": len(candidates), "eligibleCount": len(eligible),
            "candidateAreas": [item["component_area"] for item in candidates],
            "selectedCandidate": eligible[0]["source"] if eligible else None,
            "selectedCandidateArea": eligible[0]["component_area"] if eligible else None,
            "selectedEndpoints": data["selected"],
        })
    document = {"summary": {"fixtureCount": len(rows),
                            "passed": sum(row["passed"] for row in rows),
                            "positive": sum(row["truth"] == "positive" for row in rows),
                            "negative": sum(row["truth"] == "negative" for row in rows)},
                "fixtures": rows}
    output.write_text(json.dumps(document, indent=2) + "\n")
    return document


def dominant_area(row):
    return max(row["cleanedComponentSizes"] + row["additionalComponentSizes"], default=0)


def select_cutoff(measured):
    rows = {row["name"]: row for row in measured["fixtures"]}
    if len(rows) != 40 or measured["summary"]["passed"] != 40:
        raise E2EError("normal formal corpus is not 40/40")
    for name in TARGETS:
        if rows[name]["color"] != "BLUE" or rows[name]["truth"] != "positive":
            raise E2EError("target fixture contract changed")
    sizes = {name: dominant_area(rows[name]) for name in TARGETS}
    cutoff = max(sizes.values()) + 1
    others = [dominant_area(row) for row in rows.values()
              if row["color"] == "BLUE" and row["truth"] == "positive"
              and row["name"] not in TARGETS]
    if min(sizes.values()) <= 0 or not others or cutoff >= min(others):
        raise E2EError("no isolated measured blue component-area gap")
    return cutoff, sizes, min(others)


def run_formal(clone, output):
    result = command([sys.executable, str(clone / RUNNER), "--json", str(output)],
                     cwd=clone, timeout=240, allowed=(0, 1))
    output.with_suffix(".log").write_text(result.stdout + result.stderr)
    if not output.is_file():
        raise E2EError("formal regression produced no JSON")
    return json.loads(output.read_text())


def check_trial(normal, broken, measured):
    before = {row["name"]: row for row in normal["fixtures"]}
    after = {row["name"]: row for row in broken["fixtures"]}
    observed = {name for name, row in after.items() if not row["passed"]}
    if observed != set(TARGETS) or broken["summary"]["passed"] != 37:
        raise E2EError("mutation did not isolate exactly the three target failures")
    if any(after[name]["detected"] or after[name]["candidateCount"] != 0
           for name in TARGETS):
        raise E2EError("target failures are not candidate dropouts")
    if any(row["truth"] == "negative" and not after[row["name"]]["passed"]
           for row in measured["fixtures"]):
        raise E2EError("mutation regressed a negative control")
    if any(before[name]["passed"] and not after[name]["passed"]
           for name in before if name not in TARGETS):
        raise E2EError("mutation regressed an unrelated formal fixture")


def compact_color(raw, color):
    data, pipeline = raw["colors"][color], raw["pipeline"][color]
    candidates = data["candidates"]
    eligible = [candidate for candidate in candidates if candidate["eligible"]]
    chosen = eligible[0] if eligible else None
    endpoints = data["selected"]
    compact = {
        "detected": endpoints is not None,
        "detectionSucceeded": endpoints is not None,
        "maskPixelCount": pipeline["mask_pixels"],
        "morphologyPixelCount": pipeline["morphology_pixels"],
        "connectedComponentCount": pipeline["components"],
        "candidateCount": len(candidates),
        "eligibleCandidateCount": len(eligible),
    }
    if chosen:
        compact.update({"selectedCandidateType": chosen["source"],
                        "score": chosen["score"],
                        "rawPCASpan": chosen["raw_pca_span"],
                        "robustMainIntervalLength": chosen["robust_body_length"],
                        "continuity": chosen["longitudinal_continuity"],
                        "density": chosen["axial_density"],
                        "colorPurity": chosen["color_purity"],
                        "coreSupport": chosen["core_support"],
                        "highBrightnessCoverage": chosen["high_value_ratio"]})
    if endpoints:
        compact["endpoint"] = [endpoints[0]["x"], endpoints[0]["y"],
                               endpoints[1]["x"], endpoints[1]["y"]]
    return compact


def make_bundle(clone, root, binary, session):
    runner = load_runner(clone)
    bundle = root / ("phone_saber_triage_" + session)
    (bundle / "images").mkdir(parents=True)
    (bundle / "frames").mkdir()
    manifest = json.loads((clone / MANIFEST).read_text())
    by_name = {fixture["name"]: fixture for fixture in manifest["fixtures"]}
    selected = []
    for index, name in enumerate((*TARGETS, *CONTROLS), 1):
        fixture = by_name[name]
        if fixture["color"] != "BLUE" or fixture["expectedDetected"] != (name in TARGETS):
            raise E2EError("selected fixture contract changed")
        source = clone / "ios/PhoneSaberSenderTests/Fixtures" / fixture["path"]
        image_path = "images/" + name + ".png"
        context_path = "frames/frame_{}_{}.json".format(index, index)
        shutil.copyfile(source, bundle / image_path)
        raw = runner.analyze_png(binary, source)
        stamp = time.time()
        frame = {"frameID": index, "timestamp": stamp,
                 "red": compact_color(raw, "red"),
                 "blue": compact_color(raw, "blue")}
        blue, pipeline = frame["blue"], raw["pipeline"]["blue"]
        areas = pipeline["cleaned_component_sizes"] + pipeline["additional_component_sizes"]
        reason = ("blue fixture={}, expectedDetected={}, detected={}, maskPixelCount={}, "
                  "largestComponentArea={}, runtimeMinimumDominantComponentArea={}, "
                  "candidateCount={}, eligibleCandidateCount={}").format(
            name, str(fixture["expectedDetected"]).lower(), str(blue["detected"]).lower(),
            blue["maskPixelCount"], max(areas, default=0),
            pipeline["minimum_dominant_component_area"], blue["candidateCount"],
            blue["eligibleCandidateCount"])
        area_reason = ("blue cleanedComponentAreasLargest5={}, "
                       "additionalComponentAreasLargest5={}, runtimeMinimumCandidateArea={}").format(
            sorted(pipeline["cleaned_component_sizes"], reverse=True)[:5],
            sorted(pipeline["additional_component_sizes"], reverse=True)[:5],
            pipeline["minimum_candidate_area"])
        candidate_reasons = [
            ("blue candidate {}: source={}, eligible={}, score={:.3f}, "
             "peakValue={}, highValueRatio={:.3f}, colorPurity={:.3f}, "
             "coreSupport={:.3f}, continuity={:.3f}, endpoints={}, boundingBox={}").format(
                 position, candidate["source"], str(candidate["eligible"]).lower(),
                 candidate["score"], candidate["peak_value"], candidate["high_value_ratio"],
                 candidate["color_purity"], candidate["core_support"],
                 candidate["longitudinal_continuity"], candidate["endpoints"],
                 candidate["bounding_box"])
            for position, candidate in enumerate(raw["colors"]["blue"]["candidates"][:2], 1)
        ]
        failure_type = FAILURE_TYPES[name]
        context = {"sessionID": session, "selectedFrameID": index,
                   "selectedColor": "blue", "selectedFailureType": failure_type,
                   "selectedReasons": [reason, area_reason, *candidate_reasons],
                   "contextRadiusFrames": 2, "frames": [frame]}
        (bundle / context_path).write_text(json.dumps(context, indent=2) + "\n")
        selected.append({"path": image_path, "frameContextPath": context_path,
                         "frameID": index, "timestamp": stamp, "color": "blue",
                         "failureType": failure_type, "reason": reason})
    summary = {
        "formatVersion": 1, "sessionID": session,
        "recordedFrameCount": len(selected),
        "summaryScope": "retained incident candidates and nearby context",
        "retainedIncidentContextFrames": len(selected),
        "redBlueDetectionSummary": {color: {
            "detected": sum(json.loads((bundle / item["frameContextPath"]).read_text())
                            ["frames"][0][color]["detected"] for item in selected),
            "total": len(selected)} for color in ("red", "blue")},
        "dropoutSummary": {color: {
            "undetected": sum(not json.loads((bundle / item["frameContextPath"]).read_text())
                              ["frames"][0][color]["detected"] for item in selected)}
            for color in ("red", "blue")},
        "selectedImageCount": len(selected), "incidentCount": len(selected),
        "incidents": [], "images": selected,
    }
    (bundle / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    (bundle / "prompt.md").write_text("Analyze the selected lossless PhoneSaber frames.\n")
    return bundle


def run_e2e(root, args):
    clone, bare = create_clone(root)
    original_detector = (clone / DETECTOR).read_text()
    normal_formal = run_formal(clone, root / "normal-formal.json")
    normal_binary = compile_diagnostic(clone, root, original_detector)
    normal = measure_corpus(clone, normal_binary, root / "normal-measurement.json")
    if normal_formal["summary"]["passed"] != 40 or normal["summary"] != {
            "fixtureCount": 40, "passed": 40, "positive": 23, "negative": 17}:
        raise E2EError("normal corpus is not 40/40")
    cutoff, areas, other_minimum = select_cutoff(normal)
    print("[E2E] measured target dominant areas={} cutoff={} other-positive-min={}".format(
        areas, cutoff, other_minimum), flush=True)
    (clone / DETECTOR).write_text(mutate_clone_source(original_detector, cutoff))
    broken_formal = run_formal(clone, root / "broken-formal.json")
    check_trial(normal_formal, broken_formal, normal)
    broken_binary = compile_diagnostic(clone, root, original_detector, cutoff)
    broken = measure_corpus(clone, broken_binary, root / "broken-measurement.json")
    by_name = {row["name"]: row for row in broken["fixtures"]}
    formal_by_name = {row["name"]: row for row in broken_formal["fixtures"]}
    for name, row in by_name.items():
        formal = formal_by_name[name]
        if any(row[key] != formal[key] for key in ("passed", "detected", "candidateCount")):
            raise E2EError("instrumented diagnostics differ from committed detector")
    git(clone, "diff", "--check")
    git(clone, "add", str(DETECTOR))
    git(clone, "-c", "user.name=PhoneSaber E2E", "-c", "user.email=e2e@local.invalid",
        "commit", "-m", "E2E fixture: measured blue component regression")
    push_local(clone, bare)
    session = "e2e_blue_component_" + time.strftime("%Y%m%d_%H%M%S")
    bundle = make_bundle(clone, root, broken_binary, session)
    command([sys.executable, str(clone / TRIAGE), str(bundle), "--max-images", "6",
             "--dry-run"], cwd=clone)
    print("[E2E] Luna/max analysis starting", flush=True)
    command([sys.executable, str(clone / TRIAGE), str(bundle), "--max-images", "6",
             "--timeout", "900"], cwd=clone, timeout=960, live=True)
    analysis = json.loads((bundle / "analysis_report.json").read_text())
    assessment = analysis["analysis"]["repair_assessment"]
    if analysis.get("analysisModel") != "gpt-6-luna" or \
            analysis.get("analysisReasoningEffort") != "max" or \
            analysis.get("analysisExecuted") is not True or \
            assessment["decision"] != "actionable":
        raise E2EError("Luna/max analysis did not produce an actionable result")
    env = dict(os.environ)
    env["UNITY_PROJECT_PATH"] = str(args.unity_project.resolve())
    if args.simulator_id:
        env["PHONESABER_IOS_SIMULATOR_ID"] = args.simulator_id
    print("[E2E] Sol/high repair, verification, and review starting", flush=True)
    command([sys.executable, str(clone / REPAIR), str(bundle), "--max-images", "6"],
            cwd=clone, env=env, timeout=3600, live=True)
    report = json.loads((bundle / "final_report.json").read_text())
    verification = report.get("verification", {})
    comparison = verification.get("formalComparison", {})
    required = ("iOS XCTest", "Detection", "Lossless", "Tools", "iOS Release", "Diff Check")
    if report.get("status") != "repair_pushed" or report.get("review") != "APPROVED" or \
            report.get("repairModel") != "gpt-6-sol" or \
            report.get("repairReasoningEffort") != "high" or \
            report.get("repairExecuted") is not True or \
            report.get("reviewModel") != "gpt-6-sol" or \
            report.get("reviewReasoningEffort") != "high" or \
            report.get("reviewExecuted") is not True or \
            not verification.get("passed") or any(verification.get("stages", {}).get(stage) != "PASS"
                                                    for stage in required) or \
            any(comparison.get(key) != value for key, value in {
                "beforePassed": 37, "afterPassed": 40, "preservedPasses": 37,
                "targetFailuresRepaired": 3}.items()):
        raise E2EError("repair, verification, or independent review did not pass")
    assert_synced(clone, bare)
    return {"cutoff": cutoff, "normalPassed": 40, "brokenPassed": 37,
            "repairedPassed": 40, "negativeFailures": 0,
            "analysisDecision": assessment["decision"], "review": report["review"],
            "tempCommit": report["commit"], "tempOriginMain": report["originMain"],
            "bundle": str(bundle)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--allow-real-codex", action="store_true",
                        help="explicitly allow the paid Luna/max and Sol/high CLI calls")
    parser.add_argument("--unity-project", type=Path, default=REPO.parent)
    parser.add_argument("--simulator-id", default="")
    args = parser.parse_args()
    if not args.allow_real_codex:
        parser.error("--allow-real-codex is required")
    try:
        before = real_snapshot()
        if not (args.unity_project / "ProjectSettings/ProjectVersion.txt").is_file():
            raise E2EError("Unity project path is unavailable")
    except E2EError as exc:
        print(json.dumps({"status": "AUTO_REPAIR_E2E_INCOMPLETE",
                          "reason": str(exc)}, ensure_ascii=False), flush=True)
        return 1
    root = require_temp(Path(tempfile.mkdtemp(prefix="phonesaber-auto-repair-e2e-", dir="/tmp")))
    result, failure, after = None, None, None
    try:
        result = run_e2e(root, args)
    except Exception as exc:
        failure = str(exc)
    try:
        after = real_snapshot()
        if after != before:
            failure = "real repository changed during E2E"
    except E2EError as exc:
        failure = str(exc)
    status = "AUTO_REPAIR_E2E_SUCCESS" if result is not None and failure is None \
        else "AUTO_REPAIR_E2E_INCOMPLETE"
    summary = {"status": status, "reason": failure, "result": result,
               "temporaryRoot": str(root), "realBefore": before, "realAfter": after}
    (root / "e2e-summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(json.dumps(summary, ensure_ascii=False, indent=2), flush=True)
    return 0 if status == "AUTO_REPAIR_E2E_SUCCESS" else 1


if __name__ == "__main__":
    sys.exit(main())
