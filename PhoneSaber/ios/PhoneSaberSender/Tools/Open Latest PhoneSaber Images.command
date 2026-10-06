#!/bin/bash
set -u

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
status=0
/usr/bin/env python3 "$tools_dir/phone_saber_open_images.py" "$@" || status=$?

# Keep a double-clicked Terminal window open long enough to read the selected
# session and any actionable error. Piped/test invocations remain noninteractive.
if [ -t 0 ]; then
    printf '\nPress Return to close this window. '
    IFS= read -r _
fi

exit "$status"
