#!/usr/bin/env python3
"""Windows Player の既存F9 / tracking probeを、通常起動と同じ排他・heartbeatで実行する。"""
import argparse
import os
from pathlib import Path
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]


def data_folder():
    base = Path.home() / ('AppData/LocalLow' if sys.platform == 'win32' else 'Library/Application Support')
    return base / 'DefaultCompany/ServTechSlash/PhoneSaber'


def event_cursor(folder):
    return {path.stat().st_ino: path.stat().st_size for path in folder.glob('events.log*') if path.is_file()}


def new_events(folder, cursor):
    lines = []
    for path in sorted(folder.glob('events.log*'), reverse=True):
        if not path.is_file():
            continue
        stat = path.stat()
        offset = cursor.get(stat.st_ino, 0)
        with path.open('rb') as handle:
            handle.seek(offset if stat.st_size >= offset else 0)
            lines.extend(handle.read().decode('utf-8', 'replace').splitlines())
    return lines


def run_windows_probe(app, probe, count, log, station='A'):
    if sys.platform != 'win32':
        raise RuntimeError('Windows PC で実行してください')
    if not Path(app).is_file():
        raise FileNotFoundError(app)
    kit = ROOT / 'PhoneSaber/windows'
    env = dict(os.environ, GAME_EXE=str(Path(app).resolve()), PHONESABER_STATION=station,
               LOG_FILE=str(kit / f'Start-Saber-{station}.log'), RESTART_COUNT='0',
               STOP_FILE=str(kit / f'Start-Saber-{station}.STOP'), PHONESABER_PROBE_LOG=str(Path(log).resolve()))
    subprocess.run(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                    str(kit / 'saber-watchdog.ps1'), '-Mode', 'Lock', '-Probe', probe,
                    '-ProbeCount', str(count)], env=env, check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('probe', choices=('Latency', 'Tracking'))
    parser.add_argument('--app', default=str(ROOT / 'Builds/Windows/3D-Saber.exe'))
    parser.add_argument('--station', choices=('A', 'B'), default='A')
    parser.add_argument('--count', type=int, help='Latency: 1..8 blocks / Tracking: 10..600 seconds')
    args = parser.parse_args()
    count = args.count if args.count is not None else (8 if args.probe == 'Latency' else 66)
    if not (1 <= count <= 8 if args.probe == 'Latency' else 10 <= count <= 600):
        parser.error('count が範囲外')
    folder = data_folder()
    cursor = event_cursor(folder)
    try:
        run_windows_probe(args.app, args.probe, count, Path(tempfile.gettempdir()) / 'phonesaber-probe.log', args.station)
    except (OSError, RuntimeError, subprocess.CalledProcessError) as error:
        print(error, file=sys.stderr)
        return 1
    lines = new_events(folder, cursor)
    from latency_matrix import field, number, summarise
    if args.probe == 'Latency':
        result = summarise(lines, count)
        print(result)
        return 0 if result['complete'] and result['samples'] > 0 and result['loop'] is not None else 1
    result = next((line for line in reversed(lines) if 'event=tracking-probe' in line), '')
    print(result or 'tracking-probe の完了記録がありません', flush=True)
    filename = field(result, 'file')
    complete = ((number(field(result, 'frames')) or 0) > 0 and
                (number(field(result, 'packets')) or 0) > 0 and
                filename is not None and (folder / filename).is_file())
    return 0 if complete else 1


if __name__ == '__main__':
    raise SystemExit(main())
