#!/bin/sh
# 生成済みInfo.plistだけを更新する。gitが使えない配布ソースでもビルドを続ける。
set -eu
repo=${1:-${SRCROOT:-.}}
plist=${2:-${TARGET_BUILD_DIR:?}/${INFOPLIST_PATH:?}}
commit=unknown
dirty=unknown
if command -v git >/dev/null 2>&1; then
    if sha=$(git -C "$repo" rev-parse --short=12 HEAD 2>/dev/null); then
        commit=$sha
        # LFSのclean変換を維持し、展開済みLFSファイルを変更ありと誤認しない。
        if changes=$(GIT_OPTIONAL_LOCKS=0 git -C "$repo" status --porcelain --untracked-files=normal 2>/dev/null); then
            if [ -n "$changes" ]; then dirty=true; else dirty=false; fi
        # LFSキャッシュ等の権限制限時も、未追跡ファイルがあればdirtyは確定できる。
        elif untracked=$(git -C "$repo" ls-files --others --exclude-standard 2>/dev/null) && [ -n "$untracked" ]; then
            dirty=true
        fi
    fi
fi
build_time=$(/bin/date -u '+%Y-%m-%dT%H:%M:%SZ')
write_key() {
    /usr/libexec/PlistBuddy -c "Delete :$1" "$plist" >/dev/null 2>&1 || :
    /usr/libexec/PlistBuddy -c "Add :$1 string $2" "$plist"
}
write_key PhoneSaberGitCommit "$commit"
write_key PhoneSaberGitDirty "$dirty"
write_key PhoneSaberBuildTime "$build_time"
if [ -n "${SCRIPT_OUTPUT_FILE_0:-}" ]; then /usr/bin/touch "$SCRIPT_OUTPUT_FILE_0"; fi
