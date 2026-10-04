# 改善候補(2026-10-04、未着手・優先度順)

1. **会場用の背景マスク(要ユーザー判断・production 変更)**: カメラ固定の本番では、開始前に 20 秒「saberなし」を撮り、
   静的ホットスポット(phone_saber_hotspots.py)から背景の誤検出領域を作って、その領域の candidate を除外する。
   R7e/PF22 で消えない青や肌の誤検出にも効く唯一の案。gate(実 capture の証拠・regression・実機再試験)が必要。
2. **有線(USB)接続の選択肢(要ユーザー判断)**: AWDL の詰まり(maxGapMs 最大 約 1.8 秒)を根本的になくす。usbmuxd 経由の TCP。
3. **長時間の安定性**: 30 分以上の連続運転で、iPhone の発熱(thermalState)・fps 低下・電池を記録する診断と、長時間テストの依頼。
4. **本番用の一括起動**: Start PhoneSaber + bridge + 状態点検を1つにまとめ、受信側や bridge が落ちたら自動で再起動する見張り役。
5. **Unity 側の短い途切れの補間(Unity 変更・最小限)**: 200 ms 以下の途切れは直前の動きから外挿して、剣が止まって見えるのを防ぐ。
6. **Codex 解析のコスト**: 既定の effort を max から high に下げ、needs_capture が続くときだけ max にする。
