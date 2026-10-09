#!/usr/bin/env python3
"""同じ fixture の前後を交互に測り、各回の frame 中央値を集計する。"""
import argparse
import csv
import io
import statistics
import subprocess
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("baseline")
    parser.add_argument("current")
    parser.add_argument("png", nargs="+")
    parser.add_argument("--iterations", type=int, default=200)
    parser.add_argument("--rounds", type=int, default=5)
    args = parser.parse_args()
    if args.iterations < 1 or args.rounds < 1:
        parser.error("iterations and rounds must be positive")
    writer = csv.writer(sys.stdout)
    writer.writerow(("png", "before_ms", "after_ms", "reduction_percent", "before_trials_ms", "after_trials_ms"))
    for path in args.png:
        trials = [[], []]
        for round_index in range(args.rounds):
            for index in ((0, 1) if round_index % 2 == 0 else (1, 0)):
                result = subprocess.run([(args.baseline, args.current)[index], str(args.iterations), path],
                                        capture_output=True, text=True, check=True)
                rows = list(csv.DictReader(io.StringIO(result.stdout)))
                if len(rows) != 1 or rows[0]["png"] != path:
                    raise RuntimeError("unexpected benchmark CSV")
                trials[index].append(float(rows[0]["median_ms"]))
        before, after = map(statistics.median, trials)
        writer.writerow((path, f"{before:.6f}", f"{after:.6f}", f"{100*(1-after/before):.1f}",
                         *[";".join(f"{value:.6f}" for value in values) for values in trials]))
        sys.stdout.flush()


if __name__ == "__main__":
    main()
