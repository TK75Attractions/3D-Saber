#!/usr/bin/env python3
"""Android の USB 接続・debug APK・自動送信を一括確認する（保存設定は変更しない）。

  python3 PhoneSaber/tools/android_ready.py

初回の USB デバッグ承認とカメラ権限、同じ Wi-Fi への接続は端末で済ませておく。
ゲーム停止中は既存の探索要求へ応答する。保存済みの台に合わせて --station A/B を指定する。
"""
import argparse
from contextlib import contextmanager
from datetime import datetime
import math
import os
from pathlib import Path
import re
import select
import socket
import subprocess
import sys
import time

from player_probe import data_folder, event_cursor, new_events

ROOT = Path(__file__).resolve().parents[2]
APK = ROOT / 'PhoneSaber/android/app/build/outputs/apk/debug/app-debug.apk'
PACKAGE = 'jp.phonesaber.sender'


def adb_path():
    home = os.environ.get('ANDROID_HOME')
    candidate = Path(home) / 'platform-tools/adb' if home else None
    return str(candidate) if candidate and candidate.is_file() else 'adb'


def device_id(adb):
    result = subprocess.run([adb, 'devices'], capture_output=True, text=True)
    devices = [parts[0] for line in result.stdout.splitlines()
               if len(parts := line.split()) == 2 and parts[1] == 'device']
    return devices[0] if result.returncode == 0 and len(devices) == 1 else None


def phone_addresses(adb, device):
    result = subprocess.run([adb, '-s', device, 'shell', 'ip', '-o', '-4', 'addr', 'show', 'wlan0'],
                            capture_output=True, text=True)
    return set(re.findall(r'\binet (\d+\.\d+\.\d+\.\d+)/', result.stdout)) if result.returncode == 0 else set()


def launch(adb, device, fps=60, low_latency=False):
    prefix = [adb, '-s', device, 'shell', 'am']
    result = subprocess.run([*prefix, 'force-stop', PACKAGE], capture_output=True, text=True)
    if result.returncode:
        return False
    command = [*prefix, 'start', '-W', '-n', PACKAGE + '/.MainActivity',
               '--ez', 'autoStartSending', 'true', '--ei', 'cameraFPS', str(fps)]
    if low_latency:
        command += ['--ez', 'lowLatencyCamera', 'true']
    result = subprocess.run(command, capture_output=True, text=True)
    return result.returncode == 0 and not re.search(r'\b(?:Error|Exception)\b', result.stdout + result.stderr)


def ports_in_use():
    # Editor / 別名の Player も含め、既存の受信機と競合させない。
    game = subprocess.run(['pgrep', '-f', '/3D-Saber.app/Contents/MacOS/|/Unity.app/Contents/MacOS/Unity'],
                          capture_output=True, text=True)
    if game.returncode not in (0, 1):
        raise OSError('ゲームの起動状況を確認できません: ' + game.stderr.strip())
    if game.stdout.strip():
        return True
    result = subprocess.run(['lsof', '-nP', '-iUDP:5005', '-iUDP:5006', '-iUDP:5007'],
                            capture_output=True, text=True)
    if result.returncode not in (0, 1):
        raise OSError('UDP 使用状況を確認できません: ' + result.stderr.strip())
    return bool(result.stdout.strip())


@contextmanager
def udp_receivers():
    receivers = {}
    try:
        for port in (5005, 5006, 5007):
            receiver = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            receivers[port] = receiver
            receiver.bind(('', port))  # 排他。確認後にゲームが起動しても受信を共有しない。
        yield receivers
    finally:
        for receiver in receivers.values():
            receiver.close()


def field(line, key):
    match = re.search(r'(?:^| )' + re.escape(key) + r'=(\S+)', line)
    return match.group(1) if match else None


def fresh(value, recorded_at, started_at):
    try:
        age = float(value)
        # age は小数1桁、UTC はミリ秒。丸め分を差し引いても今回の起動後の受信だけを認める。
        return 0 <= age <= 5 and recorded_at - age - .101 > started_at
    except (TypeError, ValueError):
        return False


def coordinate(data):
    try:
        values = data.decode('ascii').split(',')
        return len(values) == 4 and all(math.isfinite(float(value)) for value in values)
    except (ValueError, UnicodeError):
        return False


def wait_udp(receivers, addresses, seconds, station):
    seen = set()
    deadline = time.monotonic() + seconds
    ports = {receiver: port for port, receiver in receivers.items()}
    name = re.sub(r'[^A-Za-z0-9_.-]', '_', socket.gethostname())[:64]
    reply = f'PHONESABER_UNITY 1 red=5005 blue=5006 name={name} station={station}'.encode('ascii')
    while time.monotonic() < deadline and len(seen) < 3:
        readable, _, _ = select.select(list(ports), [], [], min(.5, max(0, deadline - time.monotonic())))
        for receiver in readable:
            data, sender = receiver.recvfrom(4096)
            if sender[0] not in addresses:
                continue
            port = ports[receiver]
            if port == 5007:
                if data.strip() == b'PHONESABER_DISCOVER 1':
                    receiver.sendto(reply, sender)
                elif data.startswith(b'PHONESABER_METRICS 1 ') and field(data.decode('ascii', 'replace'), 'platform') == 'android':
                    seen.add(port)
            elif coordinate(data):
                seen.add(port)
    return seen


def wait_events(folder, cursor, addresses, seconds):
    seen = set()
    started_at = time.time()  # launch 成功後。ログの書込時刻だけではキャッシュ受信を排除できない。
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        for line in new_events(folder, cursor):
            if field(line, 'event') != 'latency-estimate' or field(line, 'sender') not in addresses:
                continue
            try:
                stamp = line.split(' ', 1)[0]
                if not stamp.endswith('Z'):
                    continue
                recorded_at = datetime.fromisoformat(stamp.replace('Z', '+00:00')).timestamp()
            except ValueError:
                continue
            if field(line, 'platform') == 'android' and fresh(field(line, 'phone-age-s'), recorded_at, started_at):
                seen.add(5007)
                if fresh(field(line, 'pc-coordinate-age-s'), recorded_at, started_at):
                    colour = field(line, 'colour')
                    if colour in ('RED', 'BLUE'):
                        seen.add(5005 if colour == 'RED' else 5006)
        if len(seen) == 3:
            break
        time.sleep(.25)
    return seen


def print_logcat(adb, device):
    # 端末時計で確認開始後の行だけを読む。秒単位の境界内にある古い行も使わない。
    clock = subprocess.run([adb, '-s', device, 'shell', 'date', '+%s'], capture_output=True, text=True)
    try:
        since = f'{int(clock.stdout.strip()) + 1}.000'
    except ValueError:
        print('NG ログ: 端末の現在時刻を取得できません')
        return False
    if clock.returncode:
        print('NG ログ: 端末の現在時刻を取得できません')
        return False
    keys = ('capture_fps', 'analysis_fps', 'send_red_fps', 'send_blue_fps', 'capture_to_send_median_ms',
            'sensor_to_analyzer_median_ms', 'analyzer_to_jni_end_median_ms', 'jni_end_to_send_median_ms', 'jni_median_ms')
    # 計測は10秒周期。UDPが即座に届いても、次の完全な計測を待つ。
    deadline = time.monotonic() + 12
    while time.monotonic() < deadline:
        result = subprocess.run([adb, '-s', device, 'logcat', '-d', '-v', 'raw', '-T', since,
                                 'PhoneSaberLatency:I', '*:S'], capture_output=True, text=True)
        if result.returncode:
            print('NG ログ: ' + result.stderr.strip())
            return False
        for line in reversed(result.stdout.splitlines()):
            try:
                # UNKNOWN 時計源では撮影時刻だけ比較不能。送信・処理の計測は必須のまま。
                values = [None if key in ('capture_to_send_median_ms', 'sensor_to_analyzer_median_ms')
                          and field(line, key) == 'UNKNOWN' else float(field(line, key)) for key in keys]
            except (TypeError, ValueError):
                continue
            if all(value is None or math.isfinite(value) and value >= 0 for value in values) and all(value > 0 for value in values[:4]):
                print('ログ: ' + ' / '.join(f'{key}={field(line, key)}' for key in keys))
                return True
        time.sleep(.5)
    print('NG ログ: 確認開始後12秒以内に完全な PhoneSaberLatency 計測がありません')
    return False


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--apk', type=Path, default=APK)
    parser.add_argument('--station', choices=('A', 'B'), default='A')
    args = parser.parse_args()
    try:
        adb = adb_path()
        device = device_id(adb)
        if not device:
            print('NG USB: 承認済み Android を1台だけ接続してください')
            return 1
        print(f'OK USB: {device}')
        if not args.apk.is_file():
            print(f'NG APK: ビルド済み debug APK がありません: {args.apk}')
            return 1
        result = subprocess.run([adb, '-s', device, 'install', '-r', str(args.apk)], capture_output=True, text=True)
        if result.returncode or 'Success' not in result.stdout:
            print('NG インストール: ' + (result.stdout + result.stderr).strip())
            return 1
        print(f'OK インストール: {args.apk}')
        addresses = phone_addresses(adb, device)
        if not addresses:
            print('NG Wi-Fi: Android の wlan0 IPv4 がありません')
            return 1
        print('OK Wi-Fi: ' + ', '.join(sorted(addresses)))
        folder = data_folder()
        using_events = ports_in_use()
        cursor = event_cursor(folder) if using_events else {}
        print('受信確認: ' + ('既存受信機の events.log を使用' if using_events else 'UDP を一時受信'))
        if not launch(adb, device):
            print('NG 起動: 自動送信で起動できません（ロック・カメラ権限を確認）')
            return 1
        print('OK 起動: 今回だけ自動送信・60fps要求')
        if using_events:
            seen = wait_events(folder, cursor, addresses, 20)
        else:
            # 再起動前のアプリが送った datagram を受信バッファに残さない。
            with udp_receivers() as receivers:
                seen = wait_udp(receivers, addresses, 20, args.station)
        for port, title in ((5005, '赤座標'), (5006, '青座標'), (5007, 'PHONESABER_METRICS')):
            print(f'{"OK" if port in seen else "NG"} {title}: UDP {port}' +
                  ('' if port in seen else ' が20秒以内に届きません'))
        logs_ok = print_logcat(adb, device)
        return 0 if len(seen) == 3 and logs_ok else 1
    except (OSError, subprocess.SubprocessError) as error:
        print(f'NG 準備確認: {error}')
        return 1


if __name__ == '__main__':
    sys.exit(main())
