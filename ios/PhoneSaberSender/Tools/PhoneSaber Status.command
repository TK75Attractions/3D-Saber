#!/bin/bash
# Read-only one-shot health check of the PhoneSaber Mac side (receiver, Unity
# UDP 5005/5006, P2P bridge, Unity Console stats, diagnostics inbox, git, Codex).
# It never starts, stops or changes anything. See docs/claude/EVENT_DAY_RUNBOOK.md.
set -uo pipefail

source_path="${BASH_SOURCE[0]}"
while [ -L "$source_path" ]; do
    source_dir="$(CDPATH='' cd -P -- "$(dirname -- "$source_path")" && pwd)"
    link_target="$(readlink "$source_path")"
    if [[ "$link_target" = /* ]]; then
        source_path="$link_target"
    else
        source_path="$source_dir/$link_target"
    fi
done

tools_dir="$(CDPATH='' cd -P -- "$(dirname -- "$source_path")" && pwd)"
/usr/bin/env python3 -B "$tools_dir/phone_saber_status.py" "$@"
status=$?
if [ -t 0 ]; then
    read -r -p 'Return で閉じる(もう一度確認するときはダブルクリックし直す) '
fi
exit "$status"
