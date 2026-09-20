# Saber Seven Segment

選曲画面の難易度数字に使う、本プロジェクト用に作成したオリジナルの7セグメント書体。
字形は0〜9、L・V、ダッシュ、空白。先端を斜めに切った独立の六角形で構成する。
参照画像に合わせ、7には左上のセグメントがあり、9には下の横セグメントがない。
第三者フォントの字形や、参照画像の画素を取り込んでいない。

難易度カードの数字はEASY=緑、NORMAL=青、MASTER=赤。
曲リストの数字は選択した難易度の色に連動する。
`LV`も数字と同じ7セグメント書体。Lは左上下と底、Vは下側の左右と底のセグメントを使う。
その他の未収録文字はOxanium ExtraBoldへフォールバック。
譜面がない場合はダッシュを表示し、従来と同じ無効色・透明度にする。
ゲームプレイのスコアや他の画面のフォントは変更しない。

## 再生成

フォント制作時のみ `fonttools==4.60.1` が必要。Unityでの実行時依存はない。

```text
python -m pip install fonttools==4.60.1
python Tools/Fonts/create_seven_segment.py
```

出力は `Assets/Resources/Fonts/SaberSevenSegment-Regular.ttf`。
生成コードと輪郭・寸法をこのリポジトリで管理する。
数字は等幅で、既存のTextMesh Proのレベル文字列、クリック判定、ゼロ埋めを維持する。
