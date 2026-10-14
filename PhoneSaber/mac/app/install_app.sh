#!/bin/bash
# /Applications に起動用アプリ「Beat Trace Slash.app」を作る（作り直しても設定は残らない）。
# 中身は台Aの起動スクリプト（PhoneSaber/mac/saber-watchdog.sh）を呼ぶだけ。ゲーム本体は repo の
# Builds/Mac/3D-Saber.app を開くので、unity_copy.sh build-mac でゲームを作り直せば次の起動から新しい版になる。
#   PhoneSaber/mac/app/install_app.sh [台 A|B] [置き場所（既定 /Applications）]
set -u
HERE="$(CDPATH='' cd "$(dirname "$0")" && pwd -P)" || exit 1
REPO="$(CDPATH='' cd "$HERE/../../.." && pwd -P)" || exit 1
STATION="${1:-A}"
DEST_DIR="${2:-/Applications}"
case "$STATION" in A|B) ;; *) echo "台は A か B"; exit 2 ;; esac
NAME="Beat Trace Slash"
[[ "$STATION" == "B" ]] && NAME="Beat Trace Slash B"
APP="$DEST_DIR/$NAME.app"
[[ -f "$HERE/icon.icns" ]] || python3 "$HERE/make_icon.py" >/dev/null || exit 1
staged="$(mktemp -d)/$NAME.app"
mkdir -p "$staged/Contents/MacOS" "$staged/Contents/Resources" || exit 1
cp "$HERE/icon.icns" "$staged/Contents/Resources/icon.icns" || exit 1
cat > "$staged/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleName</key><string>$NAME</string>
<key>CFBundleDisplayName</key><string>$NAME</string>
<key>CFBundleIdentifier</key><string>jp.phonesaber.launcher.$(echo "$STATION" | tr AB ab)</string>
<key>CFBundleExecutable</key><string>launcher</string>
<key>CFBundleIconFile</key><string>icon</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleShortVersionString</key><string>1.0</string>
<key>LSUIElement</key><true/>
<key>LSApplicationCategoryType</key><string>public.app-category.games</string>
</dict></plist>
PLIST
# 起動側は Dock に出さない（ゲーム本体が出る）。ゲームが終わるまで監視を続け、ログは ~/Library/Logs/PhoneSaber。
cat > "$staged/Contents/MacOS/launcher" <<SCRIPT
#!/bin/bash
REPO="$REPO"
LOG="\$HOME/Library/Logs/PhoneSaber/launcher.log"
mkdir -p "\$(dirname "\$LOG")"
if [[ ! -x "\$REPO/PhoneSaber/mac/saber-watchdog.sh" && ! -f "\$REPO/PhoneSaber/mac/saber-watchdog.sh" ]] || [[ ! -d "\$REPO/Builds/Mac/3D-Saber.app" ]]; then
    /usr/bin/osascript -e 'display alert "Beat Trace Slash" message "ゲーム本体が見つかりません: '"\$REPO"'/Builds/Mac/3D-Saber.app"' >/dev/null 2>&1
    exit 1
fi
printf '\n=== %s 起動（台 $STATION）\n' "\$(date '+%Y-%m-%d %H:%M:%S')" >> "\$LOG"
exec /bin/bash "\$REPO/PhoneSaber/mac/saber-watchdog.sh" $STATION "\$REPO/Builds/Mac/3D-Saber.app" < /dev/null >> "\$LOG" 2>&1
SCRIPT
chmod +x "$staged/Contents/MacOS/launcher" || exit 1
codesign --force --sign - "$staged" >/dev/null 2>&1 || true
rm -rf "$APP" && mv "$staged" "$APP" || exit 1
touch "$APP"
/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f "$APP" >/dev/null 2>&1 || true
echo "作成: $APP"
