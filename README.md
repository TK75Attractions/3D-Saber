# 3D-Saber

Unity 6000.3.9f1 project. Hardware-free Swing input setup and latency simulation
are documented in [Tools/README_mock_bridge.md](Tools/README_mock_bridge.md).

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

Makinas未取得のPCでは、同梱のNoto Sans JPへ自動で切り替わります。日本語表示のための追加インストールは不要です。Makinasを使用する場合の取得手順とライセンスは [日本語書体](docs/JapaneseTypography.md) を参照してください。
