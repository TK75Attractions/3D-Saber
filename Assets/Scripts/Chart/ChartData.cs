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
}

[Serializable]
public class BullChartData
{
    public List<BullChargeData> charges = new List<BullChargeData>();
    public List<BullShotData> shots = new List<BullShotData>();

    public bool HasContent => (charges != null && charges.Count > 0) || (shots != null && shots.Count > 0);
}

// 突進。time〜endTime(ミリ秒、ノーツと同じく offsetMs を足して使う)に、高さ y の道を判定面の端から端まで横切る。
// dir は 1 で左から右、-1 で右から左。warn ミリ秒前から道を赤く示す。
[Serializable]
public class BullChargeData
{
    public float time;
    public float endTime;
    public float y;
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
    // 空はふつう、big は大玉、spear は槍、split は分裂。
    public string kind = "";
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
    // 見えにくさ(0〜1、覚える裏譜面「M」の「まね」の側)。0 はふつう、1 は見えない。判定は変わらない。
    public float veil = 0f;
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
