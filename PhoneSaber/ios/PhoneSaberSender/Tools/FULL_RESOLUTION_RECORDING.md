# 原寸・全frame AutoClip（実験用）

iPhone のデバッグ設定 → 自動クリップ → **原寸・全frame記録（実験用）** を ON にする（既定 OFF、AutoClip も同時に ON）。送信中に既存 AutoClip の gap（赤/青の出力消失）・jump・confusion が採用されたとき、発火時刻の前2秒〜後2秒を保存する。PC の手動 clip も対象。既存の cooldown・同位置の競合抑止は継続する。

`Documents/AutoClips/phone_saber_triage_autoclip_...` 内の既存画像・summaryに次を追加する。

- `fullres.bgra`: カメラの原寸 BGRA8 を row padding なしで、時刻順に連結した無圧縮バイト列。alphaもそのまま保存。
- `fullres.jsonl`: 1行が原寸1枚。行順が BGRA の frame 順。`f` はカメラsequence、`t` はsampleのpresentation timestamp秒。`appliedISO` と `exposureDurationSeconds` はフレームExif優先、実機状態のfallbackは `cameraMetadataSource` と `cameraMetadataAgeSeconds` で区別。不明はnull。設定ISOを実測値として補わない。
- 各色の `c` は全候補をrank順に保持（eligible/ineligibleとも件数制限・間引き・数値丸めなし）。列は `fullResolution.candidateColumns` と同じ。source列だけは数値indexでなく文字列。
- `summary.json.fullResolution`: 寸法、packed stride、1枚のbyte数、frame数、時刻窓、欠番数、前後のcoverageと `complete`。OFFならフィールドなし、ONでも上限・中断等なら `state=notCaptured`。

全**処理済み**frameをコピーキューへ渡す。既存の認識mailboxは維持し、カメラが落としたframe・処理前に置換されたframeを復元しない。コピーキューはretain最大2枚で待たずに古い窓を捨て、保存中は新たな原寸窓を収集しない。欠番を補間しない。`complete=false` の窓を全frameの証拠として扱わない。起動直後・形式変更・高解像度/高fpsでメモリ上限を超えた場合も前窓が不足し得る。

制限: 原寸payloadリング320 MiB（別途retain最大2枚、アプリ既存バッファ等）、metadataは1frame 256 KiB超ならframeごと欠落、原寸1枚8 MiBまで。直近1時間4件（手動も含め、再起動・ON/OFFでも履歴維持、途中で失敗した収集も予算消費）。AutoClip全体で40件かつ完成bundle合計1 GiB、古い順に削除。保存前と稼働中に空き2 GiB以上を確認。電池20%以下/不明、thermal serious/critical、熱によるfps低下、Debug Recording中は原寸収集を停止する。空き容量取得不可も停止。

480×640・60fpsで4秒は約240〜241枚、BGRA **294.9〜296.1 MB（281.3〜282.4 MiB）/件**。全候補metadata・既存PNGは別途。4件/時でBGRA **約1.18 GB/時**。30fpsは約148 MB/件、約0.59 GB/時。無制限の60fps連続記録なら265 GB/時になるため採用していない。端末にはおおむね直近3件の原寸窓が残り、その他AutoClipとの合計で1 GiBまで。転送の作業用PSBTは最大512 MiB、書き出し中のpartialも一時的に必要（異常終了後の残存PSBTは保持bundleと同程度まで増え得る）。上限はiPhone 13の最小容量構成でも保存を使い切らないためのアプリ内制限で、実機での速度・熱・電池消費は別途確認する。

転送は既存outbox/LAN/P2P/SHA256/重複receiptを使用。自動転送はプレイ中に待機し、5秒の座標停止で開始。f313e18f の「未転送の録画を今すぐ送る」による手動送信例外は維持する。USB pull も同じAutoClips以下をコピーできる。Mac receiverもこの変更版へ更新すること（旧版は追加ファイルを拒否する）。

```bash
# schema・coverageを確認する（画素は読み込まない）
python3 PhoneSaber/ios/PhoneSaberSender/Tools/phone_saber_fullres.py /path/to/clip
# 全枚を原寸RGB PNGとmetadataに取り出す（元BGRAは変更しない）
python3 PhoneSaber/ios/PhoneSaberSender/Tools/phone_saber_fullres.py /path/to/clip --output /tmp/fullres-pngs
# 既存のday_summary / stationary_replay / clip_reportは原寸全候補を優先する
python3 PhoneSaber/ios/PhoneSaberSender/Tools/phone_saber_day_summary.py --date 2026-10-29 --since 12:00 --images /tmp/day-fullres
```

`complete` は保存された窓内のsequence連続性と要求fpsからの前後coverageで判定する。センサーの全露光の保証ではない。Exif欠落時のdevice fallbackもフレーム厳密の露出証拠と区別する。
