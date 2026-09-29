#!/bin/bash
set -euo pipefail

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
exec /usr/bin/env python3 "$tools_dir/phone_saber_receiver_launcher.py" "$@"
