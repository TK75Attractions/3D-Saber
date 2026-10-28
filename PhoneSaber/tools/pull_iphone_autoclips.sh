#!/bin/bash
# iPhone に残っている AutoClip を USB で直接取り出し、Mac の受信箱へ足す（既にある clip は上書きしない）。
# 転送が止まっている・Mac に届かない時の取り出し用。iPhone はロック解除して USB で接続しておく。
#   bash PhoneSaber/tools/pull_iphone_autoclips.sh [UDID]
set -euo pipefail
BUNDLE_ID="${PHONESABER_BUNDLE_ID:-com.satoshi.PhoneSaberSender2}"
INBOX="${PHONESABER_AUTOCLIP_INBOX:-$HOME/Library/Application Support/PhoneSaber/diagnostics-inbox/autoclips}"
udid="${1:-}"
if [[ -z "$udid" ]]; then
    udid=$(xcrun devicectl list devices 2>/dev/null | awk '/physical/ && /connected/ && /iPhone/ {for (i=1;i<=NF;i++) if ($i ~ /^[0-9A-F]{8}-[0-9A-F]{16}$/) print $i}' | head -1)
fi
[[ -n "$udid" ]] || { echo "接続中の iPhone が見つかりません（ロック解除して USB をつなぐ）"; exit 1; }
work=$(mktemp -d "${TMPDIR:-/tmp}/phonesaber-pull.XXXXXX")
trap 'rm -rf "$work"' EXIT
xcrun devicectl device copy from --device "$udid" --domain-type appDataContainer \
    --domain-identifier "$BUNDLE_ID" --source Documents/AutoClips --destination "$work/AutoClips" >/dev/null
mkdir -p "$INBOX"
added=0; skipped=0
for clip in "$work"/AutoClips/phone_saber_triage_autoclip_*; do
    [[ -d "$clip" && -f "$clip/summary.json" ]] || continue
    name=$(basename "$clip")
    if [[ -e "$INBOX/$name" ]]; then skipped=$((skipped + 1)); continue; fi
    cp -R "$clip" "$INBOX/$name"
    added=$((added + 1))
done
echo "取り出し: 追加 $added 件 / 既にあり $skipped 件 → $INBOX"
