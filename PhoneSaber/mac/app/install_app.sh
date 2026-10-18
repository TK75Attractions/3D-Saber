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
# 配置先と同じファイルシステムで作り、配置失敗時は既存の app を復元する。
staging="$(mktemp -d "$DEST_DIR/.phonesaber-launcher.XXXXXX")" || exit 1
staged="$staging/$NAME.app"
previous="$staging/previous.app"
cleanup() {
    rm -rf "$staged"
    if [[ -e "$previous" || -L "$previous" ]]; then
        if [[ ! -e "$APP" && ! -L "$APP" ]]; then
            mv "$previous" "$APP" || return 1
        fi
    fi
    # 復元できなかった旧 app は削除せず、退避先に残す。
    rmdir "$staging" 2>/dev/null || true
}
trap cleanup EXIT
mkdir -p "$staged/Contents/MacOS" "$staged/Contents/Resources" || exit 1
cp "$HERE/icon.icns" "$staged/Contents/Resources/icon.icns" || exit 1
cat > "$staged/Contents/Info.plist" <<PLIST || exit 1
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
# パス中の $・引用符・バッククォートを、起動時にシェルのコードとして解釈させない。
# bash 3.2 の %q は UTF-8 locale で文字を壊すため、この変換だけ C locale にする。
REPO_LITERAL="$(
    LC_ALL=C
    printf '%q' "$REPO"
)"
cat > "$staged/Contents/MacOS/launcher" <<SCRIPT || exit 1
#!/bin/bash
REPO=$REPO_LITERAL
LOG="\$HOME/Library/Logs/PhoneSaber/launcher.log"
mkdir -p "\$(dirname "\$LOG")"
if [[ ! -x "\$REPO/PhoneSaber/mac/saber-watchdog.sh" && ! -f "\$REPO/PhoneSaber/mac/saber-watchdog.sh" ]] || [[ ! -d "\$REPO/Builds/Mac/3D-Saber.app" ]]; then
    # パスを AppleScript のコードに埋め込まず、引用符や改行も文字列として渡す。
    /usr/bin/osascript -e 'on run argv
        display alert "Beat Trace Slash" message ("ゲーム本体が見つかりません: " & (item 1 of argv))
    end run' "\$REPO/Builds/Mac/3D-Saber.app" >/dev/null 2>&1
    exit 1
fi
printf '\n=== %s 起動（台 ${STATION}）\n' "\$(date '+%Y-%m-%d %H:%M:%S')" >> "\$LOG"
exec /bin/bash "\$REPO/PhoneSaber/mac/saber-watchdog.sh" $STATION "\$REPO/Builds/Mac/3D-Saber.app" < /dev/null >> "\$LOG" 2>&1
SCRIPT
chmod +x "$staged/Contents/MacOS/launcher" || exit 1
codesign --force --sign - "$staged" >/dev/null 2>&1 || true
if [[ -e "$APP" || -L "$APP" ]]; then
    mv "$APP" "$previous" || exit 1
fi
mv "$staged" "$APP" || exit 1
rm -rf "$previous"
touch "$APP"
/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f "$APP" >/dev/null 2>&1 || true
echo "作成: $APP"
