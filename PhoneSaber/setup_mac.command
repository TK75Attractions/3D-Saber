#!/bin/bash
set -euo pipefail
repo_dir="$(CDPATH='' cd -P -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
if ! command -v python3 >/dev/null 2>&1; then
    printf '[MANUAL ACTION REQUIRED] Python 3 is not installed. Install Python 3, then rerun setup.\n' >&2
    exit 2
fi
# Workspace files outside Git (AGENTS.md etc., ~/.claude/CLAUDE.md); never overwrites.
workspace_check=false
for argument in "$@"; do
    if [[ "$argument" == "--check" ]]; then workspace_check=true; fi
done
if [[ "$workspace_check" == true ]]; then
    /usr/bin/env python3 "$repo_dir/tools/install_workspace_files.py" --check || true
else
    /usr/bin/env python3 "$repo_dir/tools/install_workspace_files.py" || true
fi
exec /usr/bin/env python3 "$repo_dir/tools/setup_mac.py" --repo "$repo_dir" "$@"
