using System;
using System.Collections.Generic;

// chart.json のスキーマ。
// 例: { "bpm": 188.0, "notes": [ { "beat":5.0, "time":1595.74, "x":-0.66, "y":-1.76, "type":"tap", "color":"default", "direction":"none" } ] }
[Serializable]
public class ChartData
{
    public float bpm;
    // 譜面ソフトの座標 → ワールド座標へのスケール（既定 1.0：既に world 単位）。
    // 譜面ソフトがピクセルで吐く場合は 1920 板なら 11/1920≒0.00573 など。
    public float coordScale = 1.0f;
    // 譜面全体のタイミングオフセット（ミリ秒）。
    // +値 = 全ノーツを後ろにずらす（譜面が早すぎる場合）
    // -値 = 全ノーツを前にずらす（譜面が遅すぎる場合）
    // 曲の頭の無音や DSP 遅延を吸収するために使う。
    public float offsetMs = 0f;
    // 制作者が指定する表示難易度(1..10)。0は未指定で、既存の表示設定/自動推定を使う。
    public int displayLevel = 0;
    // 四分音符グリッドの原点（ms）。offsetMs は別途一度だけ加算する。
    public float beatZeroMs;
    public List<ChartTimeSignature> timeSignatures = new List<ChartTimeSignature>();
    // エディターと本編の拍ガイドで共有する時刻アンカー。ノーツの実時刻は変更しない。
    public List<ChartTempoPoint> editorTempoMap = new List<ChartTempoPoint>();
    public List<NoteData> notes = new List<NoteData>();
    // 照準で遊ぶ裏譜面(Phobia の「避」、2026-10-24)。ノーツの代わりに、照準で狙う的とよけるトゲを置く。
    // ふつうの譜面では空のまま(JsonUtility は無い項目を空の入れ物にする)。譜面エディターは別の型なので、この欄を読み書きしない。
    public AimChartData aim = new AimChartData();
    // 覚える裏譜面(Morning の「M」、2026-10-25)。見本の2小節のあと、画面にノイズをかけて同じ2小節を切らせる。
    // ノイズの時間帯(veils)と、最初の数回だけ出す説明(captions)。ノーツ側の見えにくさは NoteData.veil。
    public MemoryChartData memory = new MemoryChartData();
    // 緩急の裏譜面(logic!!! の「S」、2026-10-26)。ノーツと小節線の流れる速さを曲の時刻ごとに変える(止まる・速く・遅く)。
    // 届く時刻と判定は変えない。空ならいつもの一定の速さ。計算は ScrollSpeedMap。
    public List<ScrollSpeedData> scroll = new List<ScrollSpeedData>();
    // 闘牛の裏譜面(アンダルシアの「T」、2026-10-26)。ノーツに加えて、刃が触れるとその剣が2秒しびれる「突進」と「弾」が来る。
    // ふつうの譜面では空のまま。決まりは BullRules。
    public BullChartData bull = new BullChartData();
    // 熱交換。null は無効。JSON に heat のオブジェクトがあるときだけ有効にする。
    public HeatChartData heat;
    // 停電(2:23 AM の「L」)。ノーツ・判定は変えず、曲時計で背景と懐中電灯の強さを変える。
    public List<DarkPointData> dark = new List<DarkPointData>();
    // 校歌の裏譜面 F。判定の根元は着地点、見た目だけを真上から等速で落とす。
    public FallChartData fall = new FallChartData();
    // 勉強の裏譜面 Q(試験用紙と問題)。決まりは QuizChartData。
    public QuizChartData quiz = new QuizChartData();
    // Manic Sonic の裏譜面 R(鏡門・天地返し)。enabled が false ならふつうの譜面。決まりは ReverseRules。
    public ReverseChartData reverse = new ReverseChartData();
}

[Serializable]
public class FallChartData
{
    // 無い項目を読む旧譜面では有効にしない。
    public bool enabled;
    public float height = 3.2f;
}

[Serializable]
public class BullChartData
{
    public List<BullChargeData> charges = new List<BullChargeData>();
    public List<BullShotData> shots = new List<BullShotData>();

    public bool HasContent => (charges != null && charges.Count > 0) || (shots != null && shots.Count > 0);
}

// 突進。time〜endTime(ミリ秒、ノーツと同じく offsetMs を足して使う)に、(x, y)を通る直線を横切る。
// angle は右向きから反時計回りの度数。dir=1 はその向き、-1 は逆向き。warn ミリ秒前から道を赤く示す。
[Serializable]
public class BullChargeData
{
    public float time;
    public float endTime;
    public float x;
    public float y;
    public float angle;
    public int dir = 1;
    public float warn = 1400f;
}

// 弾。time に判定面の (x, y) へ届く(ノーツと同じく奥から飛んでくる)。follow を red/blue にすると、
// 見えはじめた瞬間のその色の剣の真ん中へ向かう(その手が見えないときは x・y)。
[Serializable]
public class BullShotData
{
    public float time;
    public float x;
    public float y;
    public string follow = "";
    // 空はふつう、big は大玉、spear は槍、split は分裂、mine は地雷、bomb は爆弾、wave はうねり。
    public string kind = "";
    // 地雷の滞在時間(ミリ秒)、爆弾の最大半径、うねりの振幅。省略時は従来の弾にも無害な既定値。
    public float stay = 700f;
    public float blast = .9f;
    public float amp = .6f;
    // lead ミリ秒前から横へ寄り、届く0.25秒前に止まる。0なら今までどおり。
    public float fromX, fromY, lead;
    // 槍の向き(度)。0は横、90は縦。
    public float angle;
}

// 流れる速さの変わり目。time(ミリ秒、ノーツと同じく offsetMs を足して使う)から、speed 倍(0 で止まる)へ
// ramp ミリ秒かけてまっすぐ変える(0 ならその瞬間に変える)。最初の変わり目より前は 1 倍。
// ease は飛び方の曲がり具合(1 でまっすぐ同じ速さ、1 より大きいと奥から速く来て手前でブレーキ、小さいと奥でためて手前で一気に)。
// 同じ ramp で変える。書いていない・壊れた値は 1。
[Serializable]
public class ScrollSpeedData
{
    public float time;
    public float speed = 1f;
    public float ramp;
    public float ease = 1f;
}

[Serializable]
public class MemoryChartData
{
    public List<MemoryVeilData> veils = new List<MemoryVeilData>();
    public List<MemoryCaptionData> captions = new List<MemoryCaptionData>();

    public bool HasContent => (veils != null && veils.Count > 0) || (captions != null && captions.Count > 0);
}

// 画面のノイズ。time〜endTime(ミリ秒、ノーツと同じく offsetMs を足して使う)の間、強さ strength(0〜1)でかける。
[Serializable]
public class MemoryVeilData
{
    public float time;
    public float endTime;
    public float strength = 1f;
}

// 説明の一言(下の中央)。time〜endTime はミリ秒。曲が始まる前(負の時刻)からでもよい。
[Serializable]
public class MemoryCaptionData
{
    public float time;
    public float endTime;
    public string text = "";
}

[Serializable]
public class AimChartData
{
    public List<AimTargetData> targets = new List<AimTargetData>();
    public List<AimSpikeData> spikes = new List<AimSpikeData>();

    public bool HasContent => (targets != null && targets.Count > 0) || (spikes != null && spikes.Count > 0);
}

// 的(ダーツ)。time はミリ秒(ノーツと同じく offsetMs を足して使う)。color は狙う照準の色(red=右手 / blue=左手)。
[Serializable]
public class AimTargetData
{
    public float time;
    public float x;
    public float y;
    public string color = "red";
}

// トゲ。time に判定面へ届く。size は当たりと見た目の倍率。follow を red/blue にすると、
// 出てくる瞬間のその色の照準の位置へ向けて飛ぶ(追尾トゲ。x・y はその手が見えないときの置き場所)。
[Serializable]
public class AimSpikeData
{
    public float time;
    public float x;
    public float y;
    public float size = 1f;
    public string follow = "";
}

// 滑るロングの通過点。座標はノーツと同じ譜面座標、msは到着からの経過。
[Serializable]
public class SlidePathPoint
{
    public float ms;
    public float x;
    public float y;
}

[Serializable]
public class NoteData
{
    public float beat;
    // chart.json の time はミリ秒。ゲーム中は秒で扱いたいので TimeSeconds で取得する。
    public float time;
    public float x;
    public float y;
    public string type = "tap";   // "tap" / "direction" / "long"
    public string color = "default";
    public string direction = "none";
    public int count = 1;          // long 用の必要切断回数（tap は 1）
    // long 用の実長さ（ミリ秒）。0 なら従来通り (count-1) × NoteSpawner.secondsPerLongCut で自動決定。
    // 「カット回数は少ないが長く滞留する」ようなロングを作れる。
    public float lengthMs = 0f;
    // U47 流星。slide が true のロングだけ、到着から明示長の間に終点へ滑る。
    // 終点は x/y と同じ譜面座標。旧譜面は slide=false のまま動かない。
    public bool slide;
    public float slideToX, slideToY;
    // 到着からの経過ミリ秒と通過点。空なら従来の slideToX/Y、点の間は等速。
    public List<SlidePathPoint> slidePath = new List<SlidePathPoint>();
    // P/U72。0・省略は従来どおり。正の値は奥の滞在時間の倍率(1はPの基準)。
    // laneXは出現位置のワールドX。着地点はx/yのまま、到着0.73秒前までに合流する。
    public float approachScale;
    public float laneX;
    public bool HasOvertake => OvertakeRules.NormalizeScale(approachScale) > 0f;
    // F のうち上から降るノーツ。譜面側の fall.enabled も有効なときだけ使う。
    // 省略・false は元の x/y へ奥から来る通常ノーツ。
    public bool fall;
    // 見えにくさ(0〜1、覚える裏譜面「M」の「まね」の側)。0 はふつう、1 は見えない。判定は変わらない。
    public float veil = 0f;
    // C/U12。届く何秒前から本来の色を出すか。0・省略は通常表示。判定・手・配置は変えない。
    public float reveal;
    // サビの方向ノーツだけ、色が決まる時刻まで本体と床の矢印も隠す。
    public bool revealArrow;
    // 風に流されて来る(青葉騒の裏譜面「W」、2026-10-25)。出現したときの位置のずれ(windX・windY、ワールド単位)は、
    // 届く WindRules.CalmSeconds 秒前までになめらかに 0 へ戻る。windSpin はずれが戻るあいだに回る角度(ラジアン、+ は左回り)、
    // windSway は全ノーツがそろって左右に揺れる幅(曲の拍に合わせる)。全部 0 ならふつうのノーツ。判定は変わらない。
    public float windX = 0f;
    public float windY = 0f;
    public float windSpin = 0f;
    public float windSway = 0f;

    public bool HasWind => windX != 0f || windY != 0f || windSway != 0f;
    // 消える(Zero の裏譜面「V」、2026-10-26)。"fade"(本体だけ消える)・"sudden"(直前に出る)・"blink"(途中で消えてまた出る)・
    // "hide"(合図ごと消える)。空ならふつう。見え方の時刻は VanishRules。判定は変わらない。
    public string vanish = "";
    // 熱交換の特殊ノーツ。両手で切る場合は既存の color:"gold" と組み合わせる。
    public bool heat;
    // Q の見えないノーツ。本体・矢印・着地の枠・床の印を出さずに切れる(見た目は QuizMode の玉と的)。
    public bool hidden;
    // 当たりの大きさの倍率。0・省略は 1。Q の行列の的は小さくして、隣の的では切れないようにする。
    public float hitScale;
    // R の鏡門。x・矢印は本当の(門を通ったあとの)値で、門を通るまでは左右の鏡像の側に見せる。判定・着地の枠・床の印は本当の位置。
    public bool mirror;

    public double TimeSeconds => time / 1000.0;

    public bool IsLong => count > 1 || (type != null && type.ToLowerInvariant() == "long");
    public bool IsDirection => !string.IsNullOrEmpty(direction) && direction.ToLowerInvariant() != "none";

    // 切り詰め・曲終了・生成のすべてで同じ実長を使う。作者の lengthMs 指定を優先する。
    public double LingerSeconds(float secondsPerCut)
    {
        if (count <= 1) return 0;
        return lengthMs > 0 ? lengthMs / 1000.0 : (count - 1) * (double)Math.Max(0, secondsPerCut);
    }
}
