#!/usr/bin/env python3
"""macOS/arm64 計測 executable の PC を atos で復号する（外部 attach 不要）。"""
import argparse
from collections import Counter
import json
from pathlib import Path
import subprocess


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    parser.add_argument("png", nargs="+")
    parser.add_argument("--iterations", type=int, default=2000)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.iterations < 1:
        parser.error("iterations must be positive")
    args.output.mkdir(parents=True, exist_ok=True)
    for index, path in enumerate(args.png):
        result = subprocess.run([str(args.binary.resolve()), str(args.iterations), path],
                                capture_output=True, text=True, check=True)
        (args.output / f"{index}.csv").write_text(result.stdout)
        (args.output / f"{index}.pcs").write_text(result.stderr)
        lines = result.stderr.splitlines()
        marker = next(i for i, line in enumerate(lines) if line.startswith("sampling_base="))
        base = lines[marker].split()[0].split("=")[1]
        pcs = lines[marker+1:]
        symbols = []
        for start in range(0, len(pcs), 500):
            resolved = subprocess.run(["atos", "-o", str(args.binary.resolve()), "-l", base, *pcs[start:start+500]],
                                      capture_output=True, text=True, check=True)
            symbols.extend(resolved.stdout.splitlines())
        (args.output / f"{index}.symbols").write_text("\n".join(symbols))
        functions = Counter(symbol.split(" (in ")[0] for symbol in symbols)
        # inlining された core_lines/dominant_body は呼び出し元名で集計される場合がある。
        summary = {"png": path, "samples": len(pcs), "functions": functions.most_common(15),
                   "source_lines": Counter(symbol.rsplit("(", 1)[-1] for symbol in symbols
                                           if ".cpp:" in symbol).most_common(20)}
        (args.output / f"{index}.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2))
        print(json.dumps(summary, ensure_ascii=False), flush=True)


if __name__ == "__main__":
    main()
