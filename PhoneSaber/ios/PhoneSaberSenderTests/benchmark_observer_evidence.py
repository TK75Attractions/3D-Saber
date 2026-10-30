#!/usr/bin/env python3
"""同じlossless入力で観測証拠ON/OFFを交互に計測（画像読込・JSON出力は計測外）。"""
import argparse
import json
from pathlib import Path
import subprocess
import sys
import tempfile

TESTS = Path(__file__).resolve().parent
SOURCE = TESTS.parent / 'PhoneSaberSender/PhoneSaberSender'
sys.path.insert(0, str(TESTS.parent / 'PhoneSaberSender/Tools'))
from run_lossless_regression import DEFAULT_MANIFEST, guard_build, load_fixtures
from run_latency_host_tests import block

MAIN = r'''
    static func main() throws {
        let iterations = Int(CommandLine.arguments[1])!
        var timings: [Bool: [Double]] = [false: [], true: []]
        var compared = 0
        for path in CommandLine.arguments.dropFirst(2) {
            guard let image = load(URL(fileURLWithPath: path)) else { fatalError(path) }
            func run(_ evidence: Bool) -> SaberFrameAnalysis {
                analyzeSabers(in: image.bytes, width: image.width, height: image.height,
                    bytesPerRow: image.width * 4, redThreshold: ColorThreshold(), blueThreshold: ColorThreshold(),
                    collectObserverEvidence: evidence,
                    shutterEmitter: ShutterEmitterMode(shutter: 2000, iso: 250, enabled: true))
            }
            let expected = signature(run(false))
            guard signature(run(true)) == expected else { fatalError("recognition changed: \(path)") }
            compared += 1
            for _ in 0..<3 { _ = run(false); _ = run(true) }
            for i in 0..<iterations {
                for enabled in (i.isMultiple(of: 2) ? [false, true] : [true, false]) {
                    let start = ProcessInfo.processInfo.systemUptime
                    let analysis = run(enabled)
                    let elapsed = (ProcessInfo.processInfo.systemUptime - start) * 1000
                    timings[enabled]!.append(elapsed)
                    // 最適化で結果が消されないよう、測定後に全候補/score/selectedも照合する。
                    guard signature(analysis) == expected else { fatalError("recognition changed: \(path)") }
                }
            }
        }
        func summary(_ samples: [Double]) -> [String: Any] {
            let sorted = samples.sorted(), n = sorted.count
            return ["samples": n, "median_ms": (sorted[(n-1)/2] + sorted[n/2])/2,
                    "p95_ms": sorted[Int(ceil(Double(n) * 0.95)) - 1]]
        }
        let result: [String: Any] = ["fixtures": compared, "iterations": iterations,
            "off": summary(timings[false]!), "on": summary(timings[true]!)]
        FileHandle.standardOutput.write(try JSONSerialization.data(withJSONObject: result, options: [.sortedKeys]))
    }
'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--iterations', type=int, default=30)
    parser.add_argument('--json', type=Path, required=True)
    args = parser.parse_args()
    if args.iterations < 5:
        parser.error('iterations must be >= 5')
    _, fixtures = load_fixtures(DEFAULT_MANIFEST)
    original = (TESTS / 'DiagnosticParityHarness.swift').read_text()
    harness = original.replace(block(original, 'static func main()'), MAIN)
    with tempfile.TemporaryDirectory(prefix='phonesaber-observer-benchmark-') as directory:
        tmp = Path(directory)
        (tmp / 'Harness.swift').write_text(harness)
        subprocess.run(['xcrun', 'swiftc', *guard_build.swift_flags(), '-O', str(SOURCE / 'DetectionCore.swift'),
                        str(SOURCE / 'BGRADetection.swift'), str(tmp / 'Harness.swift'),
                        '-module-cache-path', str(tmp / 'cache'), '-o', str(tmp / 'benchmark')], check=True)
        result = json.loads(subprocess.check_output([str(tmp / 'benchmark'), str(args.iterations),
                            *(str(f['resolved_path']) for f in fixtures)]))
    result['median_increase_percent'] = (result['on']['median_ms'] / result['off']['median_ms'] - 1) * 100
    result['p95_increase_ms'] = result['on']['p95_ms'] - result['off']['p95_ms']
    args.json.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
