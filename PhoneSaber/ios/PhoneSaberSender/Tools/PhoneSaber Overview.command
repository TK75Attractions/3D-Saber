#!/bin/bash
# Build the cross-session overview of every received Debug Recording bundle and
# open it in the browser: <inbox>/phone_saber_sessions_overview.html (+ .md).
# Bundles are only read. A one-page .report.md is written beside any bundle that
# has none, so every row links to its summary. See Tools/README.md.
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
/usr/bin/env python3 -B "$tools_dir/phone_saber_sessions_overview.py" --write-missing-reports --open "$@"
status=$?
if [ -t 0 ] && [ "$status" -ne 0 ]; then
    read -r -p 'Return で閉じる '
fi
exit "$status"
