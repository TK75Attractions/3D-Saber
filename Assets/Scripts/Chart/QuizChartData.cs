using System;
using System.Collections.Generic;

// 勉強(Remaster)の裏譜面「Q」第3版(2026-10-28。ユーザー「パクりすぎだからノーツの流れてくるとことかにオリジナリティ出して」
// 「問題もちゃんと変えてな」「流れてくるノーツは金で」)。曲の途中で画面が黒い試験用紙に切り替わり、奥から来る金のノーツを
// 4つのやり方で切って答える: rhythm「答えの柱」(どこで切るか)・sort「斬り分け」(どの向きに切るか)・
// count「答えの形」(いくつ来るか)・pi「π を書く」(切るたびに円周率が1桁ずつ書かれる)。
// 板の絵(問題・札・箱・数・桁・赤ペン)は QuizMode が描き、画像は曲フォルダの quiz/ の下の PNG(Computer Modern の白い字)。
// 飾りの金(decoys)は本物の金ノーツと同じ見た目で飛ぶが、判定・点には入らない(NoteSpawner.Decoy)。
// ふつうの譜面では空のまま(JsonUtility は無い項目を空の入れ物にする)。
[Serializable]
public class QuizChartData
{
    // 試験用紙の時間帯。この間はステージ・床・小節線・ゲートの枠を黒い紙で覆う。
    public List<QuizPaperData> papers = new List<QuizPaperData>();
    public List<QuizItemData> items = new List<QuizItemData>();
    // 飾りの金(rhythm の正しくない柱)。chart.notes には入れない。
    public List<QuizDecoyData> decoys = new List<QuizDecoyData>();

    public bool HasContent => (papers != null && papers.Count > 0) || (items != null && items.Count > 0)
        || (decoys != null && decoys.Count > 0);
}

[Serializable]
public class QuizPaperData
{
    public float startMs, endMs;
}

// 飾りのノーツ。ms は届く時刻(ミリ秒、ノーツの time と同じ時計)、x・y は着地点(譜面の座標)。
// veil は見えにくさ(NoteData.veil と同じ。練習の問題で 0.7)。
// color("gold"・"red"・"blue")と direction(NoteData.direction と同じ)は第4版(2026-10-28 夜)から。書いていなければ金のタップ。
[Serializable]
public class QuizDecoyData
{
    public float ms, x, y;
    public float veil;
    public string color = "gold";
    public string direction = "none";
}

[Serializable]
public class QuizItemData
{
    // "rhythm"(答えの柱)/ "sort"(斬り分け)/ "count"(答えの形)/ "pi"(π を書く)。
    public string kind = "";
    // 問題が出る・金が届き始める・消える時刻(ミリ秒、ノーツの time と同じ時計)。
    public float showMs, playMs, endMs;
    // 問題の画像(quiz/ の下のファイル名)。sort は見出し(「← even | odd →」)、pi は「π =」。
    public string question = "";
    // rhythm: 札4つ(左の柱から)/ sort: 札4つ(金が来る順)。
    public List<string> options = new List<string>();
    // rhythm: 正しい柱(0〜3)。
    public int answer;
    // 練習の問題(rhythm: 正しい札を最初から赤い丸で囲む。飾りは譜面の側で veil 0.7)。
    public bool hint;
    // sort: 札ごとの答えの側("left" / "right")。
    public List<string> sides = new List<string>();
    // sort: 左の箱・右の箱に書く語の画像。
    public List<string> boxes = new List<string>();
    // count: 答え(金の数)。
    public int count;
    // count: 問題の画像の中の「?」の中心(画像の幅・高さに対する割合 0〜1。x は左から、y は上から)。
    // pi: 「π =」の画像の中の「π」の中心(花丸を置く所。0 なら QuizRules.PiGlyphX・PiGlyphY)。
    public float slotX, slotY;
    // count: 0〜12 の数の画像(添字 = 数。問題の画像と同じ字の大きさで書き出すと、画素の比で同じ大きさに出る)/ pi: 0〜9 の桁の画像。
    public List<string> numbers = new List<string>();
    // count: 最初に「?」の所に出す「?」の画像(白い字。赤ペンの色で塗って出す)。第3版の仕様に無い、任意の項目。
    // 空なら quiz/qmark.png を探す(QuizRules.DefaultMarkImage。それも無ければ「?」は出さない)。
    public string markImage = "";
    // pi: 書く桁("3.1415926535…" の点を除いた数字の列、マスの数だけ)。
    public string digits = "";
    // pi: 各行のマスの数(合計 = ノーツの数)。
    public List<int> rows = new List<int>();
    // pi: 「.」(最初のマスの「3.」)・「…」(最後の桁の右)・赤い花丸(「π」を囲む)の画像。花丸は赤で描いた画像(色を掛けない)。
    // 桁・点・「…」・「π =」は同じ字の大きさで書き出す(「0」の画素の高さを桁の高さ 0.5 として大きさを決め、下の端をそろえる)。
    public string pointImage = "", ellipsisImage = "", flowerImage = "";
    // rhythm: 最後の本物の時刻 / count: 最後の金の時刻 / pi: 最後のノーツの時刻(赤ペン・「…」・花丸を出す)。
    public float revealMs;
    // pi: 紙の縁を光らせる時刻。
    public List<float> flashesMs = new List<float>();
}
