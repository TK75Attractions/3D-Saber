#!/bin/bash
set -euo pipefail

latest_log="${HOME:?HOME is not set}/Library/Logs/PhoneSaber/latest.log"
if [ ! -e "$latest_log" ]; then
    printf 'PhoneSaber log not found: %s\n' "$latest_log" >&2
    printf 'Start the receiver first, then try again.\n' >&2
    read -r -p 'Press Return to close this window. '
    exit 1
fi

exec /usr/bin/tail -n 200 -f "$latest_log"
