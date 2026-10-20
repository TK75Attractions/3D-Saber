# チュートリアルの両手お手本とロング練習

チュートリアル右側に、選曲入場時の案内と同じ白い人体模型を小さく表示する。背面から見た青の左手・赤の右手に剣を持たせる。片手、交互、矢印の上下、金色（右手の例）、両手同時、ロングの各カットを実際の練習ノーツに合わせて見せる。

お手本は `TutorialController` が生成した譜面と、判定調整を一度だけ引いた `SongPlayer` の時計を共有する。振りの中央が各カットの予定時刻になる。0.24秒前から構え、予定時刻の前後0.08秒で切り、0.24秒後までに戻る。ロングは0.5秒間隔で上下に切り返す。プレイヤーの成功・失敗で実演時刻を変えない。お手本はUI画像のみで、カメラ・ライト・コライダー・入力イベントを作らず、操作も遮らない。通常/LOW共通。

ロングの説明は「長いノーツは、数字の回数だけ振ろう」。赤2回、青3回、赤4回を開始から0・3・6秒に流す。各ノーツは0.5秒間隔で切れ、停止時間は0.5・1・1.5秒。12秒周期で繰り返す。2・3・4回をそれぞれ最後まで切り切ると合格し、切り残し・MISS・同じ回数の重複成功では達成種類数を増やさない。表示は「あと N 種類」。説明や失敗ヒントに「3回固定」を残さない。

ロング練習の上限は30秒。時間切れは `timeout` と記録し「OK!」を出さない。いずれかの練習が時間切れになった場合、最後は「準備OK!」ではなく「曲を選びに行こう」と案内する。本編の譜面・判定幅・得点計算は変更しない。

## 素材の再生成

`Tools/SongSelectHuman/RightHandHuman.blend` と同じ模型を使用する。元資料とCC0ライセンスは `Tools/SongSelectHuman/Reference3DAssets/SongSelectHuman` に保持する。選曲画面の素材は変更しない。

```text
blender -b --python Tools/TutorialGuide/render_guide.py -- Tools/SongSelectHuman/RightHandHuman.blend /absolute/path/to/frames
python Tools/TutorialGuide/pack_atlas.py /absolute/path/to/frames Assets/Resources/UI/TutorialGuide/BothHands.png
```

16枚の512×512透過画像を4×4に並べる。カメラと胴体は固定し、模型の左右半分のコマを独立に選ぶ。Unityは2048×2048、ミップマップなし、透過、非圧縮で読み込む。生成中間画像はアトラスへまとめた後は不要。

`TutorialGuideTests` は譜面時刻・左右・同時・金色・ロングの回数・音源周回・切り残しを確認する。`TutorialGuidePlayTests` は実Gameシーンの表示、操作を遮らないこと、時計停止、実ノーツの部分達成と重複成功を確認する。実機のセンサー遅延と投影での見え方は別途確認する。
