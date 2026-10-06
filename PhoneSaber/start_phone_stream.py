"""Start a private-path RTSP receiver and the saber tracker on this Mac."""
import argparse
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.request
import json

from python_runtime import reexec_with_cv2

reexec_with_cv2()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--imu-stick', choices=['none', 'red', 'blue'], default='none')
    args = parser.parse_args()
    binary = shutil.which('mediamtx') or '/opt/homebrew/bin/mediamtx'
    if not Path(binary).exists():
        raise SystemExit('Install receiver: brew install mediamtx')
    root = Path(__file__).resolve().parent
    token = 'saber-' + secrets.token_hex(8)
    children = []
    try:
        with tempfile.TemporaryDirectory(prefix='saber-stream-') as directory:
            config = Path(directory) / 'mediamtx.yml'
            config.write_text(
                'rtspAddress: :8554\nrtspTransports: [tcp]\n'
                'rtmp: no\nhls: no\nwebrtc: no\nsrt: no\nmoq: no\n'
                'api: yes\napiAddress: 127.0.0.1:9997\n'
                f'paths:\n  {token}:\n    source: publisher\n', encoding='utf-8')
            server = subprocess.Popen([binary, str(config)])
            children.append(server)
            addresses = []
            for interface in ('en0', 'en1'):
                result = subprocess.run(['/usr/sbin/ipconfig', 'getifaddr', interface],
                                        capture_output=True, text=True)
                if result.returncode == 0:
                    addresses.append(result.stdout.strip())
            print('\nLarix connection URL (choose the Mac Wi-Fi address):', flush=True)
            for address in addresses or ['MAC_WIFI_IP']:
                print(f'rtsp://{address}:8554/{token}', flush=True)
            print('iPhone: H.264, 640x360 (or 640x480), 30 FPS, 1000 kbps, '
                  'keyframe 1s, video only, RTSP TCP.\n'
                  'Start broadcasting. Waiting for the iPhone; Ctrl+C quits.', flush=True)
            # Local-only status API avoids opening a second video decoder.
            while server.poll() is None:
                try:
                    with urllib.request.urlopen('http://127.0.0.1:9997/v3/paths/list', timeout=1) as response:
                        paths = json.load(response)['items']
                    if any(p['name'] == token and p.get('ready') for p in paths):
                        break
                except (OSError, ValueError, KeyError):
                    pass
                time.sleep(.2)
            if server.poll() is not None:
                raise RuntimeError('Receiver stopped. Check the log above (port conflict or configuration).')
            if args.imu_stick != 'none':
                bridge = root.parent / 'Tools/mac_ble_udp_bridge.py'
                children.append(subprocess.Popen([sys.executable, str(bridge)]))
            tracker = subprocess.Popen([sys.executable, str(root / 'smartphone_camera.py'),
                '--source', f'rtsp://127.0.0.1:8554/{token}', '--show',
                '--imu-stick', args.imu_stick])
            children.append(tracker)
            tracker.wait()
    except KeyboardInterrupt:
        pass
    finally:
        for child in reversed(children):
            if child.poll() is None:
                child.terminate()
                try:
                    child.wait(timeout=3)
                except subprocess.TimeoutExpired:
                    child.kill()
                    child.wait()


if __name__ == '__main__':
    main()
