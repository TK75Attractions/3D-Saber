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
    // 覚える裏譜面(勉強の「M」、2026-10-25)。見本の2小節のあと、画面にノイズをかけて同じ2小節を切らせる。
    // ノイズの時間帯(veils)と、最初の数回だけ出す説明(captions)。ノーツ側の見えにくさは NoteData.veil。
    public MemoryChartData memory = new MemoryChartData();
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
