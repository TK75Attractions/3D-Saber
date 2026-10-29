using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

// 勉強の裏譜面「Q」第3版(2026-10-28、Opus・Fable・Opus 下請けの3案から決めた仕様 spec_v3)の決まり。
// 時計・描画・状態を持たない純関数と数値だけ(QuizMode と NoteSpawner がこれで描き、テストもここを直接たたく)。
// 座標は譜面の座標(= 判定面 z=judgeZ のワールド座標)。時刻は曲の時計の秒(NoteSpawner.TotalOffsetSeconds を足した値)。
//   試験用紙: papers の時間帯に 0.25 秒で黒い紙を出し、終わりの 0.25 秒で戻す(接する時間帯はつないで、境目で戻さない)。
//     紙の間は判定面にゲートの枠(x ±1.9、答えの柱の紙は ±3.3。y −0.6〜0.3)を白い細線で描く。字は白、正解の印だけ赤ペンの赤、ノーツは金。
//   rhythm「答えの柱」(どこで切るか): 柱は x −2.7/−0.9/0.9/2.7、y −0.15。判定面に薄い四角、真上に答えの札、上に問題。
//     本物の金は正しい柱だけ(当たり 0.8 倍で隣の柱では切れない)、ほかの3本は飾りの金(NoteSpawner.Decoy)。
//     本物を切るとその札が 0.15 秒金に光り、最後の本物が届いたら正しい札を赤い丸で囲む(練習は最初から)。
//   どの問題も、問題のノーツをすべて切れたら(正解)、答えの札・問題から虹の波動(虹色の輪3つ)を画面の外まで広げる。
//   sort「斬り分け」(どの向きに切るか): 札4つ(x ±0.7・±2.1、y 1.1)から金が発射され(NoteData.launch)、真ん中の的へ飛ぶ。
//     本当の向き(答えの側)は隠してある(sideArrow)。届く時刻に札が答えの箱(x ±3.7)へ 0.25 秒で滑り込み、
//     切れていれば金、逃した・違う向きなら灰色で積む。接近 2.0 秒では最初の金が問題の前に出るので、板を早めに出す。
//   count「答えの形」(いくつ来るか): 金の数が答え。届くたびに(切っても逃しても)「?」の所の数が 1, 2, 3 … と増え、
//     最後の金のあと答えの数を赤い丸で囲む。
//   pi「π を書く」: 金を切るたびに、そのマスに円周率の桁が白く(逃したら灰色で)書かれる。行が埋まると 0.3 秒で 0.9 上へ送る。
//     盛り上がり4から拍ごとに紙の縁を光らせ(大きな光の予算・F3 では出さない)、最後に「…」と「π」の赤い花丸。
// 第2版(Milthm「Algebra」をなぞった横線の玉・行列のレーン・数える玉・数式の輪)は、ユーザーの「パクりすぎ」で全部やめた。
public static class QuizRules
{
    public const string Rhythm = "rhythm", Sort = "sort", Count = "count", Pi = "pi";
    public const string Left = "left", Right = "right";
    // NoteData.sideArrow の隠した向き。
    public const string HiddenSide = "hidden";
    // 画像は曲フォルダ(StreamingAssets/Songs/<曲>)の下のこのフォルダ。
    public const string ImageFolder = "quiz";
    // ノーツと問題の時刻を同じとみなす幅(秒)。譜面は同じ値を書くので、浮動小数の丸めだけを許す。
    public const double MatchSeconds = .002;

    // ---- 試験用紙 ----
    public const double PaperFadeSeconds = .25;
    // これより近い時間帯はつなぐ(C と D のように接するとき、境目で黒を戻さない)。
    public const double PaperJoinSeconds = .001;

    // ---- 判定面のゲートの枠(紙の間だけ。奥行きと判定面の場所を見せる) ----
    public const float GateHalfWidth = 1.9f, GateBottomY = -.6f, GateTopY = .3f;
    // 答えの柱の問題がある紙では、外の柱の四角(2.7 + 0.25)より 0.35 外まで広げる(外の柱は本物のゲートの外にある)。
    // 斬り分けの紙は箱(x ±2.7〜±4.7)に重ねないよう 1.9 のまま。
    public const float PillarGateHalfWidth = 3.3f;
    public const float GateLineWidth = .03f, GateLineAlpha = .4f;
    public static readonly Vector2 GateCenter = new Vector2(0f, -.15f);

    // ---- 赤ペン ----
    public static readonly Color RedPen = new Color(.92f, .18f, .16f);
    // 投影で見えるよう 0.07(0.05 では細かった。Codex の点検、10/28)。
    public const float RedPenWidth = .07f;
    // 丸は一筆で描く(仕様に無い足し)。0.2 秒で描き終え、終わりは書き始めを 8% 越えて少し外へずれる。
    public const double PenDrawSeconds = .2;
    public const float PenOvershoot = .08f;
    // 札・数のまわりのあき。
    public const float PenCirclePad = .12f;

    // ---- 板の画像の大きさの上限(判定面で横に見えている幅は約 ±7) ----
    public const float BoardMaxWidth = 9f;

    // ---- rhythm「答えの柱」 ----
    // 柱の間は 1.8(第4.1版、2026-10-29 ユーザー「選択肢同士の間隔を広げて間違えて切りにくくして」。前は 1.2 で x ±0.6・±1.8)。
    public static readonly float[] PillarX = { -2.7f, -.9f, .9f, 2.7f };
    public const float PillarY = -.15f, PillarSpacing = 1.8f;
    // 本物の当たりの倍率(譜面の hitScale)。本編の当たり(刃 0.32 + 0.88 × 0.8 = 1.024)は、2つの柱のまん中(0.9)を少し越えるので
    // 正しい柱の側ならどこを振っても切れ、隣の柱の四角の内側の縁(1.8 − 0.25 = 1.55)には届かない。倍率が 1 未満なので、
    // 細い側の振りで広げる量は SaberCutJudge.ScaledThinRangeLimit(0.85)までで、ふだんの当たりより広がらない。
    // 間が 1.2 のときは 0.45(当たり 0.716、まん中 0.6 を少し越える)だった。柱の間と一緒に広げ、当たりとまん中の関係は変えない。
    public const float PillarHitScale = .8f;
    public const float PillarSquareSize = .5f, PillarSquareAlpha = .18f;
    // 札は判定面の y 2.0(画面では奥から出てくるノーツより上)。0.95 では、奥から柱へ来るノーツの通り道(画面の上下)に札が重なり、
    // 字が金のノーツに隠れた(9a、10/28 の録画)。問題はその上。
    public const float RhythmQuestionY = 3.05f, RhythmQuestionHeight = .85f;
    // 札の幅の上限は、隣の札とのあきを 0.2 残す 1.6(柱の間が 1.2 のときは 1.1 で、D1 の札4枚が縮んで字が小さかった)。
    public const float OptionY = 2f, OptionHeight = .5f, OptionMaxWidth = 1.6f;
    public const double OptionFlashSeconds = .15;
    // 答えの柱の問題の画像のふつうの高さ(画素)。積分・Σ のように背の高い問題は画素の比で大きくし(上限 1.6)、
    // 下の端(札の上)はそろえて上へ伸ばす(高さを決めうちにすると字が A の 0.4 倍ほどになった。第3.1版)。
    public const int RhythmQuestionReferencePixels = 172;
    public const float RhythmQuestionMaxHeight = 1.6f;
    public const float RhythmQuestionBottomY = RhythmQuestionY - RhythmQuestionHeight * .5f;
    // 最後の問題(flowerImage がある項目)は、答えを赤い丸で囲んだうえで、問題の右に花丸を押す(札を囲むと隣の札にかかるため)。
    public const float OptionFlowerGap = .25f;
    // 札の画像のふつうの高さ(画素)。札はこの高さを OptionHeight で出し、背の高い札(分数の 299px など)は同じ比で大きくして
    // 字の大きさをそろえる(幅が上限を超えたときだけ縮める。9a、10/28)。斬り分けの札・箱の語も同じ。
    public const int OptionReferencePixels = 143, SortLabelReferencePixels = 178, BoxWordReferencePixels = 175;
    // 試験用紙の間の、切った光(GameplayCutFeedback)の大きさの倍率。黒い紙の上では Perfect の虹の輪が問題の字まで覆い、
    // 答えの形(金を続けて切る)では問題が読めなくなったため(9a、10/28 の録画)。紙の濃さに合わせて 1 から下げる。
    public const float PaperCutFeedbackScale = .6f;

    // ---- sort「斬り分け」 ----
    public static readonly float[] SortLabelX = { -2.1f, -.7f, .7f, 2.1f };
    public const float SortHeaderY = 2.3f, SortHeaderHeight = .6f;
    // 札の幅の上限(隣の札と重ねない。札の間は 1.4)。
    public const float SortLabelY = 1.1f, SortLabelHeight = .55f, SortLabelMaxWidth = 1.25f;
    public static readonly Vector2 SortTarget = new Vector2(0f, -.15f);
    // 箱と中の字は投影で読める大きさに(Codex の点検、10/28。前は 1.5×1.0・語 0.30・札 0.24 で十数画素だった)。
    public const float BoxX = 3.7f, BoxY = -.15f, BoxWidth = 2f, BoxHeight = 1.3f;
    public const float BoxAlpha = .5f, BoxLineWidth = .035f;
    // 箱の中: 上の端に語(高さ 0.36、幅 1.8 を超えたら縮める)、その下に積んだ札を2列×2段。
    public const float BoxWordHeight = .36f, BoxWordMaxWidth = 1.8f, BoxWordTopMargin = .05f;
    public const float BoxSlotGap = .03f, BoxSlotBottomMargin = .05f;
    public const float BoxLabelHeight = .30f, BoxLabelMaxWidth = .80f;
    // 札が箱へ滑り込むのは、遅めの判定の窓(0.18 秒)が閉じたあと(届く 0.24 秒後から 0.10 秒)。届いた時刻から動かすと、
    // 答えの箱を見てから遅めに切れてしまう(Codex の点検、10/28)。
    public const double SortSlideDelaySeconds = .24, SortSlideSeconds = .10;
    // 金は play から4分(160 BPM で 0.375 秒)おきに4つ(譜面にノーツが足りないときの札の時刻)。
    public const double SortStepSeconds = .375;
    // 接近 2.0 秒では最初の金が問題(show)より前に出る。板はその金が出る 0.2 秒前から出す。
    public const double SortBoardLeadSeconds = .2;
    // 板のために紙を早める上限(CoverBoard)。
    public const double SortCoverLeadSeconds = 2.5;
    // 金が出る前の札の濃さ。出たら 0.1 秒で 1 へ(仕様の「少し明るくなる」)。
    public const float SortDimAlpha = .7f;
    public const double SortBrightenSeconds = .1;
    public static readonly Color MissGray = new Color(.5f, .5f, .5f);

    // ---- count「答えの形」 ----
    public const float CountQuestionY = 2f, CountQuestionHeight = .85f;
    // 最初に「?」の所に出す「?」の画像(QuizItemData.markImage が無いときに探す名前。白い字を赤ペンの色で塗る)。
    public const string DefaultMarkImage = "qmark.png";
    // 数の画像の高さ。問題の画像と同じ字の大きさで書き出してあれば画素の比で決まる。比が使えないときは問題の高さ × 0.62。
    public const float CountNumberScale = .62f, CountNumberMinScale = .3f, CountNumberMaxScale = 1f;

    // ---- pi「π を書く」 ----
    // マスの間隔は 0.38。11 マスの行もゲートの中に収まるよう、行の長さ n では 0.38 と 3.42 ÷ (n − 1) の狭い方(9a、10/28)。
    public const float PiLineY = -.15f, PiFirstCellX = -1.71f, PiCellSpacing = .38f, PiRowSpan = 3.42f;
    public const int PiRowCells = 10;
    // 桁の高さ(「0」の画像)。点・「…」・「π =」は同じ字の大きさで書き出した画素の比で、下の端(字の並ぶ線)をそろえる。
    public const float PiDigitHeight = .5f;
    // 「π =」の中心(1行目の左)。最初のマスとのあいだは PiLabelGap より狭めない。
    public const float PiLabelX = -2.45f, PiLabelGap = .06f;
    public const float PiRowStep = .9f;
    public const double PiRowScrollSeconds = .3;
    public const float PiMissAlpha = .35f;
    // 「π =」の画像の中の「π」の中心(slotX・slotY が無いとき。x は左から、y は上から)。
    public const float PiGlyphX = .2f, PiGlyphY = .5f;
    // 赤い花丸(渦巻き+花びら)の高さと、出るときの膨らみ。
    public const float FlowerHeight = 1.3f;
    public const double FlowerSeconds = .25;
    // 紙の縁の光(大きな光の予算に乗せる。画面の縁 8% の帯、ゲートの内側は抜く)。
    public const float EdgeFlashAlpha = .3f;
    public const double EdgeFlashSeconds = .3;

    // ---- 飾りの金 ----
    // 届いたら判定面を少し過ぎながら、この秒数で薄れて消える(MISS の暗くなる見た目は出さない)。
    public const double DecoyFadeSeconds = .3;

    // ---- 正解の虹の波動(2026-10-29 ユーザー「1問正解の時(正解ノーツをすべて切れたとき)虹の波動を出して」) ----
    // 問題のノーツをすべて切れたら、答えの札(答えの柱)・問題の画像(ほか)から虹色の輪を 0.09 秒おきに3つ、0.9 秒で画面の外まで広げる。
    // 紙の上・ノーツと字の下に描く(字・ノーツ・剣を隠さない)。面を光らせない細い輪なので、大きな光の予算(1秒3回)には乗せない。
    // 演出控えめ(F3)では1つ目の輪だけを GateAccents.LowGain の濃さで出し、にじみは出さない。
    public const double WaveSeconds = .9, WaveRingDelaySeconds = .09;
    public const int WaveRings = 3;
    // 輪の半径(判定面のワールド単位。画面の端は判定面で x ±7.3 ほど)。終わりはゆっくり。
    public const float WaveFromRadius = .4f, WaveToRadius = 12f;
    // 芯の太さ(広がるにつれ 6 割へ)と、芯のまわりのにじみ(縁へ薄れる帯。広がるにつれ倍へ)。
    public const float WaveWidth = .2f, WaveGlowWidth = .9f, WaveGlowAlpha = .35f, WaveAlpha = .95f;
    // 色相が1秒に回る量(輪の上では角度で一周する)。
    public const float WaveHueSpeed = 1.2f;

    // カメラが無いとき(EditMode など)の、判定面で見えている上の端。
    public const float DefaultTopEdgeY = 4.6f;

    // 試験用紙の時間帯(曲の時計の秒)。
    public readonly struct Span
    {
        public readonly double Start, End;
        public Span(double start, double end) { Start = start; End = end; }
        public bool Contains(double time) => time >= Start && time <= End;
    }

    public static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    // 譜面のミリ秒を曲の時計の秒へ。
    public static double Seconds(float ms, double offset) => ms / 1000.0 + offset;

    // 当たりの大きさの倍率(NoteData.hitScale)。0・省略・壊れた値は 1。
    public static float HitScale(float value) => float.IsNaN(value) || float.IsInfinity(value) || value <= 0f ? 1f : value;

    // 種類の名前(大文字小文字・前後の空白をそろえる)。
    public static string Kind(string kind) => string.IsNullOrWhiteSpace(kind) ? "" : kind.Trim().ToLowerInvariant();

    // 答えの側の名前("left" / "right"、ほかは "")。
    public static string Side(string side)
    {
        string name = Kind(side);
        return name == Left || name == Right ? name : "";
    }

    // 左は −1、右は +1、どちらでもなければ 0。
    public static float SideSign(string side) => Side(side) == Left ? -1f : Side(side) == Right ? 1f : 0f;

    // 隠した向きの名前。"hidden"(大文字小文字・前後の空白は問わない)を、左右の矢印のノーツにだけ残す。
    public static string SideArrow(string value, string direction)
    {
        if (Kind(value) != HiddenSide) return "";
        var required = CutDirectionHelper.Parse(direction);
        return required == CutDirection.Left || required == CutDirection.Right ? HiddenSide : "";
    }

    // そのノーツの本当の向きを隠すか(読み込み前の譜面でも同じ答え)。
    public static bool HidesSide(NoteData note) => note != null && SideArrow(note.sideArrow, note.direction) == HiddenSide;

    // 読み込み直後にそろえる(ChartLoader.Parse)。壊れた値・長さの無い時間帯は捨て、時刻順に並べる。
    public static QuizChartData Normalize(QuizChartData quiz)
    {
        quiz ??= new QuizChartData();
        quiz.papers = (quiz.papers ?? new List<QuizPaperData>())
            .Where(p => p != null && Finite(p.startMs) && Finite(p.endMs) && p.endMs > p.startMs)
            .OrderBy(p => p.startMs).ToList();
        quiz.items = (quiz.items ?? new List<QuizItemData>())
            .Where(i => i != null && Finite(i.showMs) && Finite(i.playMs) && Finite(i.endMs) && i.endMs > i.showMs)
            .OrderBy(i => i.showMs).ToList();
        foreach (var item in quiz.items)
        {
            item.kind = Kind(item.kind);
            item.question = File(item.question);
            item.options = Files(item.options);
            item.sides = (item.sides ?? new List<string>()).Select(Side).ToList();
            item.boxes = Files(item.boxes);
            item.numbers = Files(item.numbers);
            item.markImage = File(item.markImage);
            item.digits = Digits(item.digits);
            item.rows = (item.rows ?? new List<int>()).Where(r => r > 0).ToList();
            item.pointImage = File(item.pointImage);
            item.ellipsisImage = File(item.ellipsisImage);
            item.flowerImage = File(item.flowerImage);
            item.flashesMs = (item.flashesMs ?? new List<float>()).Where(f => Finite(f)).OrderBy(f => f).ToList();
            if (!Finite(item.revealMs)) item.revealMs = 0f;
            item.slotX = Finite(item.slotX) ? Mathf.Clamp01(item.slotX) : 0f;
            item.slotY = Finite(item.slotY) ? Mathf.Clamp01(item.slotY) : 0f;
            if (item.count < 0) item.count = 0;
            if (item.answer < 0 || item.answer >= PillarX.Length) item.answer = -1;
        }
        quiz.decoys = (quiz.decoys ?? new List<QuizDecoyData>())
            .Where(d => d != null && Finite(d.ms) && Finite(d.x) && Finite(d.y)).OrderBy(d => d.ms).ToList();
        foreach (var decoy in quiz.decoys)
        {
            decoy.veil = Finite(decoy.veil) ? Mathf.Clamp01(decoy.veil) : 0f;
            decoy.color = DecoyColor(decoy.color);
            decoy.direction = string.IsNullOrWhiteSpace(decoy.direction) ? "none" : decoy.direction.Trim().ToLowerInvariant();
        }
        return quiz;
    }

    // 飾りの色(第4版)。赤・青のほかは金(書いていない古い譜面も金)。
    public static string DecoyColor(string color)
    {
        string c = (color ?? "").Trim().ToLowerInvariant();
        return c == "red" || c == "blue" ? c : "gold";
    }

    static string File(string file) => file == null ? "" : file.Trim();
    static List<string> Files(List<string> files) => (files ?? new List<string>()).Select(File).ToList();

    // 桁の列から数字だけを残す("3.1415" → "31415")。
    public static string Digits(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var builder = new StringBuilder(value.Length);
        foreach (char c in value) if (c >= '0' && c <= '9') builder.Append(c);
        return builder.ToString();
    }

    // マス cell の桁(0〜9)。無ければ −1。
    public static int Digit(string digits, int cell)
        => digits != null && cell >= 0 && cell < digits.Length && digits[cell] >= '0' && digits[cell] <= '9' ? digits[cell] - '0' : -1;

    // 試験用紙の時間帯。時刻順に並べ、重なる・接する時間帯はつなぐ。offsetSeconds は NoteSpawner.TotalOffsetSeconds。
    public static List<Span> BuildPapers(QuizChartData quiz, double offsetSeconds)
    {
        var spans = new List<Span>();
        if (quiz?.papers == null || !Finite(offsetSeconds)) return spans;
        foreach (var paper in quiz.papers)
        {
            if (paper == null || !Finite(paper.startMs) || !Finite(paper.endMs) || paper.endMs <= paper.startMs) continue;
            spans.Add(new Span(Seconds(paper.startMs, offsetSeconds), Seconds(paper.endMs, offsetSeconds)));
        }
        return Merge(spans);
    }

    static List<Span> Merge(List<Span> spans)
    {
        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<Span>();
        foreach (var span in spans)
        {
            if (merged.Count > 0 && span.Start <= merged[merged.Count - 1].End + PaperJoinSeconds)
            {
                var last = merged[merged.Count - 1];
                merged[merged.Count - 1] = new Span(last.Start, Math.Max(last.End, span.End));
            }
            else merged.Add(span);
        }
        return merged;
    }

    // 板を紙より前に出す必要があるとき(接近 2.0 秒の斬り分けの1問目は、最初の金が問題の前に出る)、その板と一緒に紙も出す。
    // boardStart がどの紙の中でもなく、そのあと maxLead 秒以内に紙が始まるなら、その紙の始まりを boardStart まで前へ
    // (紙と板はいつもと同じく一緒に 0.25 秒で現れる)。ほかの場合はそのまま。
    public static List<Span> CoverBoard(List<Span> papers, double boardStart, double maxLead)
    {
        if (papers == null || !Finite(boardStart) || InPaper(papers, boardStart)) return papers;
        var result = new List<Span>(papers.Count);
        foreach (var paper in papers)
        {
            bool late = paper.Start > boardStart && paper.Start - boardStart <= maxLead;
            result.Add(late ? new Span(boardStart, paper.End) : paper);
        }
        return Merge(result);
    }

    // 紙 paper の間に答えの柱の問題(出てから消えるまで)が重なるか。重なる紙ではゲートの枠を柱の幅まで広げる。
    public static bool PaperHasPillars(QuizChartData quiz, Span paper, double offsetSeconds)
    {
        if (quiz?.items == null) return false;
        foreach (var item in quiz.items)
            if (item != null && Kind(item.kind) == Rhythm && Seconds(item.showMs, offsetSeconds) < paper.End
                && Seconds(item.endMs, offsetSeconds) > paper.Start) return true;
        return false;
    }

    // ゲートの枠の半分の幅(答えの柱の紙は広い枠、ほかは本物のゲートと同じ)。
    public static float GateHalfWidthFor(bool pillars) => pillars ? PillarGateHalfWidth : GateHalfWidth;

    // 試験用紙の濃さ(0〜1)。始まりから 0.25 秒で 1、終わりの 0.25 秒で 0 へ。
    // 毎コマ呼ぶので、IReadOnlyList の foreach(列挙子の箱詰め)ではなく添字で回す。
    public static float PaperVisibility(IReadOnlyList<Span> papers, double time)
    {
        if (papers == null || !Finite(time)) return 0f;
        double best = 0;
        for (int i = 0; i < papers.Count; i++)
        {
            Span paper = papers[i];
            if (time <= paper.Start || time >= paper.End) continue;
            double v = Math.Min((time - paper.Start) / PaperFadeSeconds, (paper.End - time) / PaperFadeSeconds);
            best = Math.Max(best, Math.Min(1, v));
        }
        return (float)best;
    }

    // その時刻が試験用紙の時間帯の中か(出入りの途中も含む)。
    public static bool InPaper(IReadOnlyList<Span> papers, double time)
    {
        if (papers == null || !Finite(time)) return false;
        for (int i = 0; i < papers.Count; i++) if (papers[i].Contains(time)) return true;
        return false;
    }

    // 問題が出ている間か(showMs から endMs の手前まで)。
    public static bool Active(QuizItemData item, double offset, double time)
        => item != null && time >= Seconds(item.showMs, offset) && time < Seconds(item.endMs, offset);

    // その種類の問題が出ている時間帯(最初の showMs から最後の endMs まで)。無ければ false。
    public static bool KindSpan(QuizChartData quiz, string kind, double offset, out Span span)
    {
        span = default;
        if (quiz?.items == null) return false;
        double start = double.PositiveInfinity, end = double.NegativeInfinity;
        foreach (var item in quiz.items)
        {
            if (item == null || Kind(item.kind) != kind) continue;
            start = Math.Min(start, Seconds(item.showMs, offset));
            end = Math.Max(end, Seconds(item.endMs, offset));
        }
        if (!(start < end)) return false;
        span = new Span(start, end);
        return true;
    }

    // 前のコマから今のコマのあいだに時刻 at を越えたか(止まっている間・巻き戻しでは越えない)。
    public static bool Crossed(double previous, double now, double at) => previous < at && at <= now;

    static float EaseOut(double t)
    {
        float k = Mathf.Clamp01((float)t);
        return 1f - (1f - k) * (1f - k);
    }

    // ---- 発射点(NoteData.launch) ----

    // 出る瞬間に本体が見える奥の点。カメラ camera と札(label, judgeZ)を結ぶ線が z = spawnZ と交わる所(画面では札の上に重なる)。
    // カメラが無い・判定面より奥にあるときは札の真後ろ(label)。
    public static Vector2 LaunchFar(Vector3? camera, Vector2 label, float judgeZ, float spawnZ)
    {
        if (!camera.HasValue) return label;
        Vector3 c = camera.Value;
        float toJudge = judgeZ - c.z;
        if (!(toJudge > 1e-4f) || !Finite(c.x) || !Finite(c.y) || !Finite(c.z)) return label;
        float k = (spawnZ - c.z) / toJudge;
        return new Vector2(c.x + (label.x - c.x) * k, c.y + (label.y - c.y) * k);
    }

    // 見た目の子のずれ(xy)。根元の奥行き z が spawnZ のとき 奥の点 − 着地点、judgeZ で 0、その間はまっすぐ。届いたあとは 0。
    // 緩急の無い譜面では z の割合 = 残り ÷ 接近なので、仕様の「(P_奥 − 着地点) × 残り ÷ 接近」と同じ。
    public static Vector2 LaunchOffset(Vector2 far, Vector2 landing, float z, float judgeZ, float spawnZ)
    {
        float span = spawnZ - judgeZ;
        if (!(Mathf.Abs(span) > 1e-4f) || !Finite(z)) return Vector2.zero;
        float t = Mathf.Clamp01((z - judgeZ) / span);
        return t <= 0f ? Vector2.zero : (far - landing) * t;
    }

    // ---- 飾りの金 ----

    // 飾りの見え方(1 → 0)。届く時刻 hit までは本物と同じ姿、届いたら判定面を少し過ぎながら DecoyFadeSeconds で消える。
    public static float DecoyVisibility(double time, double hit)
    {
        if (!Finite(time) || !Finite(hit) || time <= hit) return 1f;
        return Mathf.Clamp01(1f - (float)((time - hit) / DecoyFadeSeconds));
    }

    // 見え方 visibility の飾りの見えにくさ(譜面の veil から 1 へ)。
    public static float DecoyVeil(float veil, float visibility)
    {
        float baseVeil = float.IsNaN(veil) || float.IsInfinity(veil) ? 0f : Mathf.Clamp01(veil);
        return 1f - (1f - baseVeil) * Mathf.Clamp01(visibility);
    }

    // ---- 正解の虹の波動 ----

    // 波動が出ている間か(始まりから age 秒。最後の輪が広がり終わるまで)。
    public static bool WaveActive(double age) => Finite(age) && age >= 0 && age < WaveSeconds + (WaveRings - 1) * WaveRingDelaySeconds;

    // ring 本目(0 から)の輪の進み(0〜1)。まだ出ていない・広がり終わった輪は −1。
    public static float WaveProgress(double age, int ring)
    {
        double t = age - ring * WaveRingDelaySeconds;
        return Finite(t) && t >= 0 && t < WaveSeconds ? (float)(t / WaveSeconds) : -1f;
    }

    // 進み progress の輪の半径(終わりはゆっくり)と濃さ(薄れながら広がり、1 で 0)。
    public static float WaveRadius(float progress)
    {
        float k = 1f - Mathf.Clamp01(progress);
        return Mathf.Lerp(WaveFromRadius, WaveToRadius, 1f - k * k * k);
    }

    public static float WaveOpacity(float progress)
        => progress < 0f || progress >= 1f ? 0f : WaveAlpha * Mathf.Pow(1f - progress, 1.3f);

    // ring 本目の輪の色相の始まり。時間で回し、輪ごとに 1/3 ずらす(どの瞬間も3つの輪は違う色が並ぶ)。
    public static float WaveHue(int ring, double age)
        => Mathf.Repeat(ring / (float)WaveRings + (Finite(age) ? (float)age : 0f) * WaveHueSpeed, 1f);

    // 波動の中心。答えの柱は正しい札(答えが無ければ札の列の真ん中)、斬り分けは見出し、π を書くは「π =」、答えの形は問題。
    public static Vector2 WaveOrigin(QuizItemData item)
    {
        string kind = item != null ? Kind(item.kind) : "";
        if (kind == Rhythm)
            return new Vector2(item.answer >= 0 && item.answer < PillarX.Length ? PillarX[item.answer] : 0f, OptionY);
        if (kind == Sort) return new Vector2(0f, SortHeaderY);
        if (kind == Pi) return new Vector2(PiLabelX, PiLineY);
        return new Vector2(0f, CountQuestionY);
    }

    // ---- 板の並び ----

    // 縦横比 aspect の画像を高さ height で、幅が maxWidth を超えたら縮めて置く大きさ(幅, 高さ)。
    public static Vector2 FitSize(float aspect, float height, float maxWidth)
    {
        aspect = aspect > .01f && !float.IsNaN(aspect) && !float.IsInfinity(aspect) ? aspect : 1f;
        float width = height * aspect;
        if (maxWidth > 0f && width > maxWidth)
        {
            height *= maxWidth / width;
            width = maxWidth;
        }
        return new Vector2(width, height);
    }

    // 札の大きさ(幅, 高さ)。ふつうの高さ referencePixels の札を高さ height で出し、ほかの高さの札は同じ比で大きく(小さく)する
    // (字の大きさがそろう)。そのうえで幅が maxWidth を超えたら縦横比のまま縮める。画素が分からなければ height。
    public static Vector2 LabelSize(int pixels, float aspect, float height, int referencePixels, float maxWidth)
        => FitSize(aspect, pixels > 0 && referencePixels > 0 ? height * pixels / referencePixels : height, maxWidth);

    // 同じ字の大きさで書き出した画像どうしなら、高さは画素の比(基準の画像 referencePixels が高さ referenceHeight)。
    // 比が使えない・[min, max] を外れるときは fallback。
    public static float GlyphHeight(int pixels, int referencePixels, float referenceHeight, float fallback, float min, float max)
    {
        if (pixels > 0 && referencePixels > 0 && referenceHeight > 0f)
        {
            float height = referenceHeight * pixels / referencePixels;
            if (height >= min && height <= max) return height;
        }
        return Mathf.Clamp(fallback, min, max);
    }

    // 4本の柱のうち x にいちばん近いもの。
    public static int NearestPillar(float x)
    {
        int best = 0;
        for (int i = 1; i < PillarX.Length; i++)
            if (Math.Abs(PillarX[i] - x) < Math.Abs(PillarX[best] - x)) best = i;
        return best;
    }

    // 赤ペンの丸の描き進み(start から PenDrawSeconds で 1)。
    public static float PenProgress(double time, double start)
    {
        if (!Finite(time) || !Finite(start) || time < start) return 0f;
        return Mathf.Clamp01((float)((time - start) / PenDrawSeconds));
    }

    // 札が金に光っているか(本物を切った次のコマの曲の時計 start から 0.15 秒)。
    public static bool OptionFlashing(double time, double start)
        => Finite(start) && Finite(time) && time >= start && time < start + OptionFlashSeconds;

    // ---- sort ----

    // 問題の板を出す時刻。show と、最初の金が出る時刻 − 0.2 秒の早い方(接近 2.0 秒では最初の金が show より前に出る)。
    public static double SortBoardStart(double show, double firstSpawn)
        => Finite(firstSpawn) ? Math.Min(show, firstSpawn - SortBoardLeadSeconds) : show;

    // 札が箱へ滑り込む進み(遅めの判定の窓が閉じた hit + 0.24 秒から 0.10 秒、終わりはゆっくり)。
    public static float SortSlide(double time, double hit)
    {
        if (!Finite(time) || !Finite(hit)) return 0f;
        double start = hit + SortSlideDelaySeconds;
        if (time <= start) return 0f;
        return EaseOut((time - start) / SortSlideSeconds);
    }

    // 札の濃さ。その金が出る(spawn)までは少し暗く、出たら 0.1 秒で 1。
    public static float SortLabelAlpha(double time, double spawn)
    {
        if (!Finite(spawn) || !Finite(time) || time < spawn) return SortDimAlpha;
        return Mathf.Lerp(SortDimAlpha, 1f, Mathf.Clamp01((float)((time - spawn) / SortBrightenSeconds)));
    }

    // 左(sign −1)・右(+1)の箱の中心と、上の端の語の中心。
    public static Vector2 BoxCenter(float sign) => new Vector2(BoxX * Mathf.Sign(sign), BoxY);
    public static Vector2 BoxWordCenter(float sign)
        => new Vector2(BoxX * Mathf.Sign(sign), BoxY + BoxHeight * .5f - BoxWordTopMargin - BoxWordHeight * .5f);

    // 箱に積んだ index 番目の札の中心(語の下に2列×2段。左上から右へ、次の段へ)。
    public static Vector2 BoxSlot(float sign, int index)
    {
        index = Mathf.Clamp(index, 0, 3);
        float top = BoxY + BoxHeight * .5f - BoxWordTopMargin - BoxWordHeight - BoxSlotGap;
        float bottom = BoxY - BoxHeight * .5f + BoxSlotBottomMargin;
        float rowHeight = (top - bottom) * .5f;
        float x = BoxX * Mathf.Sign(sign) + (index % 2 == 0 ? -1f : 1f) * BoxWidth * .25f;
        return new Vector2(x, top - rowHeight * (index / 2 + .5f));
    }

    // ---- count ----

    // 時刻 time までに届いた金の数(times は届く時刻。切っても逃しても数える)。
    public static int CountAt(IReadOnlyList<double> times, double time)
    {
        if (times == null || !Finite(time)) return 0;
        int count = 0;
        for (int i = 0; i < times.Count; i++) if (times[i] <= time + 1e-9) count++;
        return count;
    }

    // 画像(中心 center・大きさ size)の中の割合 (slotX, slotY) の点。x は左から、y は上から。
    public static Vector2 SlotPoint(Vector2 center, Vector2 size, float slotX, float slotY)
        => new Vector2(center.x + (slotX - .5f) * size.x, center.y + (.5f - slotY) * size.y);

    // ---- pi ----

    // 長さ rowLength の行のマスの間隔(0.38、11 マスの行は 0.342)。
    public static float PiPitch(int rowLength) => rowLength > 1 ? Mathf.Min(PiCellSpacing, PiRowSpan / (rowLength - 1)) : PiCellSpacing;

    // 長さ rowLength の行の、列 column のマスの x(x = −1.71 + 間隔 × 列)。
    public static float CellX(int column, int rowLength) => PiFirstCellX + PiPitch(rowLength) * column;

    // 行 row のマスの数(rows の外・壊れた値は 10)。
    public static int RowLength(IReadOnlyList<int> rows, int row)
        => rows != null && row >= 0 && row < rows.Count && rows[row] > 0 ? rows[row] : PiRowCells;

    // マス cell の行と列(rows は各行のマスの数)。rows の外は最後の行の続き。rows が無ければ 10 マスずつ。
    public static void CellOf(IReadOnlyList<int> rows, int cell, out int row, out int column)
    {
        cell = Math.Max(0, cell);
        if (rows == null || rows.Count == 0)
        {
            row = cell / PiRowCells;
            column = cell % PiRowCells;
            return;
        }
        row = 0;
        column = cell;
        for (int r = 0; r < rows.Count; r++)
        {
            int length = Math.Max(1, rows[r]);
            row = r;
            if (column < length || r == rows.Count - 1) return;
            column -= length;
        }
    }

    // 行を上へ送る進み(行が埋まってから 0.3 秒で 1、終わりはゆっくり)。
    public static float RowScroll(double age) => !Finite(age) || age <= 0 ? 0f : EaseOut(age / PiRowScrollSeconds);

    // 行 row の高さ。書く行は y −0.15。行 j が埋まる(その行の最後のノーツが届く rowEnds[j])たびに、
    // そこまでに書いた行(row ≤ j)を 0.9 上へ送る。
    public static float RowY(int row, IReadOnlyList<double> rowEnds, double time)
    {
        float y = PiLineY;
        if (rowEnds == null) return y;
        for (int j = Math.Max(0, row); j < rowEnds.Count; j++)
            if (Finite(rowEnds[j])) y += PiRowStep * RowScroll(time - rowEnds[j]);
        return y;
    }

    // 花丸が出る進み(0 → 1)。
    public static float FlowerAmount(double time, double start)
        => !Finite(start) || !Finite(time) || time < start ? 0f : EaseOut((time - start) / FlowerSeconds);

    // 紙の縁の光(始まりからの経過 age 秒)。
    public static bool EdgeFlashShown(double age) => age >= 0 && age < EdgeFlashSeconds;
    public static float EdgeFlashOpacity(double age) => EdgeFlashShown(age) ? EdgeFlashAlpha * (1f - (float)(age / EdgeFlashSeconds)) : 0f;
}
