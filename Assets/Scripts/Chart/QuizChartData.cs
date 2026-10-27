using System;
using System.Collections.Generic;

// 勉強(Remaster)の裏譜面「Q」(2026-10-27 ユーザー「勉強のクイズ？システムを…この動画でやりかたを学んで作る」、
// Milthm のエイプリルフール譜面「Algebra」を見て作った)。曲の途中で画面が黒い試験用紙に切り替わり、上に問題、
// その下に答えの候補が出る。見た目(問題・楽譜・数式の画像、流れる玉、的、行列の小さなレーン)は QuizMode が描き、
// 切る相手は見えないノーツ(NoteData.hidden)。画像は曲フォルダの quiz/ の下の PNG。
// ふつうの譜面では空のまま(JsonUtility は無い項目を空の入れ物にする)。
[Serializable]
public class QuizChartData
{
    // 試験用紙の時間帯。この間はステージ・床・小節線・ゲートの枠を消して黒い紙にする。
    public List<QuizPaperData> papers = new List<QuizPaperData>();
    public List<QuizItemData> items = new List<QuizItemData>();

    public bool HasContent => (papers != null && papers.Count > 0) || (items != null && items.Count > 0);
}

[Serializable]
public class QuizPaperData
{
    public float startMs, endMs;
}

[Serializable]
public class QuizItemData
{
    // "rhythm"(答えの楽譜のリズムで叩く)/ "matrix"(置換行列で的を入れ替える)/ "count"(金の玉の数が答え)/
    // "formula"(最後の大問。数式の上にノーツが来る)。
    public string kind = "";
    // 問題が出る・玉が届き始める・消える時刻(ミリ秒、ノーツの time と同じ時計)。
    public float showMs, playMs, endMs;
    // 問題の画像(quiz/ の下のファイル名)。
    public string question = "";
    // rhythm: 答えの候補の行の画像(札+楽譜)。上から順。
    public List<string> rows = new List<string>();
    // rhythm: 正しい行の番号(0 から)。
    public int answer;
    // rhythm: 練習の問題。飾りの玉を薄くし、正しい行を最初から緑にする。
    public bool hint;
    // rhythm・count: 線の上を流れる玉。real は切る相手(同じ時刻に hidden のノーツがある)、unit は数える金の玉。
    public List<QuizDotData> dots = new List<QuizDotData>();
    // matrix: 左の小さなレーン(0〜3 = A〜D)に落ちてくる印。届く時刻に、行列で決まる的を切る。
    public List<QuizGuideData> guides = new List<QuizGuideData>();
    // matrix: 4つの的の上の文字(左から)。空の文字は隠す(「= ?」の問題)。
    public List<string> labels = new List<string>();
    // count: 左上の凡例「● = 1」の画像。空なら quiz/legend.png を探す(QuizRules.DefaultLegendImage)。
    public string legend = "";
    // count・formula: 「?」の所に出す答えの画像。revealMs に出す。
    public string answerImage = "";
    public float revealMs;
    // formula: ゲートの外へ輪を広げる時刻。
    public List<float> ringsMs = new List<float>();
}

[Serializable]
public class QuizDotData
{
    public float ms;
    public bool real;
    public bool unit;
}

[Serializable]
public class QuizGuideData
{
    public float ms;
    public int lane;
}
