#!/usr/bin/env python3
"""タイムアウト時、今回のlogFileを持つsoak Playerだけを停止する。"""
import os
import re
import signal
import subprocess
import sys
import time


def matches(command, log):
    # psは空白を含むargvを再quoteしない。完全なlogFileの境界まで照合し、別runを止めない。
    path = re.escape(log)
    value = '(?:"' + path + '"|\'' + path + '\'|' + path + ')'
    return bool(re.search(r'(?:^|\s)-phonesaberSoak(?:\s|$)', command) and
                re.search(r'(?:^|\s)-logFile\s+' + value + r'(?=$|\s+-[A-Za-z])', command))


def player_pids(log):
    result = subprocess.run(['ps', '-axo', 'pid=,command='], text=True, capture_output=True)
    if result.returncode:
        return None
    targets = []
    for line in result.stdout.splitlines():
        fields = line.strip().split(None, 1)
        if len(fields) == 2 and matches(fields[1], log) and '/Contents/MacOS/' in fields[1]:
            targets.append(int(fields[0]))
    return targets


def main():
    log = sys.argv[1]
    players = player_pids(log)
    if players is None:
        print('soak: Playerのprocess一覧を取得できず、自動停止は未確認', file=sys.stderr)
        return 1
    targets = []
    for pid in players:
        try:
            os.kill(pid, signal.SIGTERM)
            targets.append(pid)
        except ProcessLookupError:
            pass
        except PermissionError:
            print('soak: Playerへの停止要求が拒否されました', file=sys.stderr)
            return 1
    # TERMを無視したPlayerを残さず、停止を確認できないときは成功を返さない。
    for sig in (None, signal.SIGKILL):
        if sig is not None:
            # 待機中にPIDが再利用されても、別アプリを強制停止しない。
            players = player_pids(log)
            if players is None:
                print('soak: Playerのprocess一覧を再取得できず、強制停止は未確認', file=sys.stderr)
                return 1
            targets = [pid for pid in targets if pid in players]
            for pid in targets:
                try:
                    os.kill(pid, sig)
                except ProcessLookupError:
                    pass
                except PermissionError:
                    print('soak: Playerの強制停止が拒否されました', file=sys.stderr)
                    return 1
        deadline = time.monotonic() + 5
        while targets and time.monotonic() < deadline:
            remaining = []
            for pid in targets:
                try:
                    os.kill(pid, 0)
                    remaining.append(pid)
                except ProcessLookupError:
                    pass
                except PermissionError:
                    print('soak: Playerの停止を確認できません', file=sys.stderr)
                    return 1
            targets = remaining
            if targets:
                time.sleep(.1)
        if not targets:
            return 0
    if targets:
        print('soak: Playerが停止しませんでした', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
