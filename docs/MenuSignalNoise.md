# タイトルとHARD選択時のノイズ

タイトルのBEAT / TRACE / SLASHの文字表面に動く粒と走査線を加える。約2.8秒に一度、約0.24秒だけ一部の横帯に色の乱れが入る。背景・字形・登場と退出の動きは既存のまま。

選曲画面で内部難易度 `Hard`（画面表示は `MASTER`）を選んでいる間は、画面全体に強い白黒の砂嵐、流れる横帯、約1.6秒ごとに約0.34秒の色付き横線を重ねる。選択後0.3秒でなじみ、EASY / NORMALへ戻すとすぐ消える。プレイ画面やリザルトへ持ち越さない。LOW設定ではどちらも無効。

初版が弱すぎて見えないというフィードバックを受け、粒を1080pで約2.2pxに拡大し、全画面の粒の強さを3倍に調整した。ロゴ表面の粒と横線も強め、同じフレームのON/OFF画像と両画面の動画で比較する。

さらにHARDを大幅に強くする要望を受け、全画面側だけを独立調整した。1080p換算で3.2pxと7pxの粒を混ぜ、粒の係数を0.018から0.22へ、走査線を0.0018から0.014へ変更。横線は最大0.18、流れる横帯は最大0.065のアルファを加える。タイトルの表現は変更しない。ゲーム画面を再撮影してスマホ用プレビューにも反映する。

`MenuSignalNoise` は描画専用のマテリアルを生成し、画面を離れると破棄する。全画面レイヤーはCanvasに追従し、1920×1080以外の画面比率にも対応する。Raycastを受け取らず、クリック・セイバー操作・譜面・判定へ介入しない。時刻と強度だけをシェーダーへ渡すため、毎フレームのノイズ画像生成や画面コピーは不要。

- 実装：`Assets/Scripts/UI/MenuSignalNoise.cs`
- 表現と強さ：`Assets/Resources/UI/MenuSignalNoise.shader`
- 接続：`TitleConceptA.Word` と `SongSelectSkin.DifficultyChanged`

## 調査した表現

- [Unity 6.3 Film Grain](https://docs.unity3d.com/6000.3/Documentation/Manual/urp/Post-Processing-Film-Grain.html)：粒の質感と強度の考え方を参考にした。
- [Keijiro Takahashi / KinoGlitch](https://github.com/keijiro/KinoGlitch)：走査線の乱れと色ずれを独立させる考え方を参考にした。コードや素材は取り込んでいない。
- [Unity uGUI Canvas](https://docs.unity3d.com/Packages/com.unity.ugui@2.0/manual/UICanvas.html)：描画順と画面への追従を確認した。

今回は既存のUIに直接かける専用シェーダーを使用し、カメラ全体のポストプロセス設定は変更しない。画面の大きな変位や連続点滅は加えない。
