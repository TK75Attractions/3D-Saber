# 3D-Saber

Unity 6000.3.9f1 project. Hardware-free Swing input setup and latency simulation
are documented in [Tools/README_mock_bridge.md](Tools/README_mock_bridge.md).

## PhoneSaber tools and sender apps

The iPhone/Android apps, Mac launchers, diagnostics, and their full history are
included in [PhoneSaber/](PhoneSaber/README.md). Clone this repository once with
Git LFS installed (`git lfs install`), run `git lfs pull`, then
`./PhoneSaber/setup_mac.command`. Verification: `bash PhoneSaber/tools/verify_phone_saber.sh`.
PhoneSaber is outside `Assets/`, so Unity does not import it.

## Phone Saber camera input on macOS

On macOS, entering Play Mode starts the existing UDP receivers on red port 5005
and blue port 5006, then publishes `Phone Saber Unity` as
`_phonesaber._udp.local.` on port 5005. PhoneSaberSender can therefore discover
the Mac without a manually entered IP address. The coordinate payload remains
`x1,y1,x2,y2`; Bonjour is used only for host discovery.

Do not run `run_debug.command` at the same time as Unity. Both receive on UDP
5005/5006. Stop Play Mode before starting the Python debug receiver, and stop
the debug receiver before entering Play Mode again.

## 日本語フォント

日本語表示には、ゲーム素材として同梱した **マキナス 4 Square（もじワク研究）** を使用します。フォント本体は通常のGitで保存しているため、新しいPCでも最新版を取得すれば追加ダウンロード・OSへのインストールなしで同じ書体になります。作者・配布元・利用条件は [日本語書体](docs/JapaneseTypography.md) と [フォントの注意書き](Assets/Resources/Fonts/Makinas-NOTICE.txt) を参照してください。
