using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// 勉強の裏譜面「Q」(2026-10-27、Milthm のエイプリルフール譜面「Algebra」の4つの仕組みを移したもの)の決まり。
// 時計・描画・状態を持たない純関数と数値だけ(QuizMode がこれで描き、テストもここを直接たたく)。
// 座標は譜面の座標(= 判定面 z=judgeZ のワールド座標)。時刻は曲の時計の秒(NoteSpawner.TotalOffsetSeconds を足した値)。
//   試験用紙: papers の時間帯に 0.25 秒で黒い紙を出し、終わりの 0.25 秒で戻す(接する時間帯はつないで、境目で戻さない)。
//   rhythm: 横の線(y −0.15)と左の的(x −1.6)。玉は右の画面の端から 4.0/秒の等速で、届く時刻に的の中心へ。届いたら 0.3 秒で薄れる。
//   matrix: 4つの的(x ±0.6・±1.8)。左の端の近くの小さなレーン 4 本(間隔 0.28)を、印が 2.2/秒で落ちて y −0.35 の小さな丸に着く。
//           レーンは画面でまっすぐ縦に見えるよう、判定面の上では少し傾ける(QuizMode がカメラから求める)。
//   count: 縦の線(x 0)。玉は上の画面の端から 3.6/秒で落ちて y −0.35 の的へ。金の玉(unit)の数が答え。
//   formula: 数式の画像を幅 9.0・濃さ 0.55 でゲートの中央に置き、ringsMs に白い輪(半径 2.2→6.0、0.6 秒、濃さ 0.3)。
//   玉・印は、その問題が出る(showMs)前は出さず、showMs から 0.15 秒で薄く現れる(次の問題の玉を前の問題の間に見せない)。
// 切る相手は見えないノーツ(NoteData.hidden)。玉や印はノーツではない(飾りの玉は数えない)。
// 2026-10-27 夜(撮影を見た b1 の指示): rhythm 2.8→4.0・count 2.4→3.6 の速さ、showMs からの出現、数式の幅 4.2→9.0、
// 数える問題の式・凡例・金の丸を約 2.5 倍、行列のレーンを画面で縦に。
public static class QuizRules
{
    public const string Rhythm = "rhythm", Matrix = "matrix", Count = "count", Formula = "formula";
    // 画像は曲フォルダ(StreamingAssets/Songs/<曲>)の下のこのフォルダ。
    public const string ImageFolder = "quiz";
    // 数える問題の凡例「● = 1」の画像。項目(QuizItemData.legend)に無いときに探す名前。
    public const string DefaultLegendImage = "legend.png";
    // 見えないノーツと玉・印を同じ時刻とみなす幅(秒)。譜面は同じ値を書くので、浮動小数の丸めだけを許す。
    public const double MatchSeconds = .002;

    // ---- 試験用紙 ----
    public const double PaperFadeSeconds = .25;
    // これより近い時間帯はつなぐ(C と D のように接するとき、境目で黒を戻さない)。
    public const double PaperJoinSeconds = .001;

    // ---- 黒板(問題・楽譜・行列・凡例の画像) ----
    // 仕様(spec_v2)の大きさは、見えている広さをゲートの枠(x ±5.5 前後)くらいと見たもの。実際の判定面は 1080p で
    // 約 130px/単位なので、そのままでは楽譜の行が約 20px(音符の頭が 3px)になって読めない。黒板の画像だけを
    // BoardScale 倍にする(玉・的・レーン・数式は見えないノーツと重ねるので、譜面の座標のまま変えない)。
    public const float BoardScale = 3f;
    public const float QuestionHeight = .30f * BoardScale;
    public const float RowHeight = .17f * BoardScale;
    public const float RowGap = .05f * BoardScale;
    // 問題と1行目のあいだ(仕様に無いので、行の間より少し広くする)。
    public const float QuestionGap = .08f * BoardScale;
    // 黒板のいちばん下(ゲートの上端 0.3 から少しあける)。
    public const float BoardBottomY = .55f;
    public static readonly Color AnswerGreen = new Color(.5f, 1f, .35f);
    public const double RevealGreenSeconds = .4;

    // ---- 玉・印の出現 ----
    // その問題の showMs から、この秒数で薄く現れる(それより前は出さない)。
    public const double AppearSeconds = .15;

    // ---- rhythm ----
    // 速さは読む1小節(2.4 秒)のうちに右の画面の端から入る 4.0(仕様の 2.8 から b1 の指示で変えた)。
    public const float RhythmLineY = -.15f, RhythmTargetX = -1.6f, RhythmTargetRadius = .24f;
    public const float BallRadius = .16f, RhythmSpeed = 4f;
    public const double PassFadeSeconds = .3;
    public const float HintDecoyAlpha = .25f;
    // 線は的の少し左から右の画面の端まで。
    public const float RhythmLineLeftMargin = .45f;

    // ---- matrix ----
    // 4つの的は間隔 1.2(仕様の 1.0 では、隣の丸の内側を振ると正しい的まで届いた。Codex の点検を受けて b1 の指示で広げた)。
    // 見えないノーツの当たりの倍率は MatrixHitScale(譜面の hitScale)。本編の判定(刃 0.32 + 当たり 0.88 × 0.45 = 0.716)は、
    // 正しい的の中心から隣の丸の縁まで(1.2 − 0.3 = 0.9)に届かない。細い側の振りで広げても SaberCutJudge.ScaledThinRangeLimit(0.85)まで。
    public static readonly float[] MatrixTargetX = { -1.8f, -.6f, .6f, 1.8f };
    public const float MatrixTargetSpacing = 1.2f;
    public const float MatrixHitScale = .45f;
    public const float MatrixTargetY = -.15f, MatrixTargetRadius = .30f, MatrixLabelY = .32f;
    public const int LaneCount = 4;
    public const float LaneSpacing = .28f, LaneBottomY = -.35f, LaneDotRadius = .09f, GuideRadius = .09f, GuideSpeed = 2.2f;
    // 左のレーンを画面の左の端からどれだけ内に置くか。字(A B C D)は小さな丸の下。
    public const float LaneMargin = .45f, LaneLetterY = -.62f;
    public const float MatrixImageHeight = .55f * BoardScale, MatrixBoardBottomY = .85f;

    // ---- count ----
    // 速さは読む1小節(1.5 秒)のうちに上の画面の端から入る 3.6(仕様の 2.4 から b1 の指示で変えた)。
    public const float CountLineX = 0f, CountTargetY = -.35f, CountTargetRadius = .24f, CountSpeed = 3.6f;
    // 動画のように、左上の大きな凡例「● = 1」と左の大きな式。式・答え・凡例・金の丸を、黒板の大きさのさらに CountScale 倍にする。
    public const float CountScale = 2.5f;
    // 式は縦の線(x 0)の左に、右の端をそろえて置く(線や玉と重ねない)。横に長すぎる式は、画面の左の端に収まるよう縮める。
    public const float CountQuestionHeight = QuestionHeight * CountScale;
    public const float CountQuestionRightX = -.8f, CountQuestionCenterY = 1.35f, CountQuestionLeftMargin = .4f;
    public const float LegendHeight = .20f * BoardScale * CountScale, LegendMargin = .5f;
    public const float UnitMarkRadius = .045f * BoardScale * CountScale, UnitMarkSpacing = .12f * BoardScale * CountScale,
        UnitMarkGap = .04f * BoardScale * CountScale;

    // ---- formula ----
    // 幅は 9.0(仕様の 4.2 では小さすぎて読めない、b1 の指示)。ノーツは譜面の側がこの大きさの記号の上へ置く。
    public const float FormulaWidth = 9f, FormulaAlpha = .55f;
    // 判定面よりわずかに奥。
    public const float FormulaDepth = .05f;
    public static readonly Vector2 GateCenter = new Vector2(0f, -.15f);
    public const float RingFromRadius = 2.2f, RingToRadius = 6f, RingAlpha = .3f, RingWidth = .07f;
    public const double RingSeconds = .6;

    // ---- 切ったときの弾け ----
    public const double PopSeconds = .3;
    public const float PopGrowth = 2.4f, PopAlpha = .9f, PopWidth = .06f;

    // ---- 線の太さ ----
    public const float LineWidth = .035f, TargetWidth = .045f, LaneLineWidth = .025f;
    public const float LineAlpha = .85f;

    // カメラが無いとき(EditMode など)の、判定面で見えている端。本編は QuizMode がカメラから求める。
    public const float DefaultRightEdgeX = 7.3f, DefaultTopEdgeY = 4.6f, DefaultLeftEdgeX = -6.5f;

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
            item.question = item.question ?? "";
            item.answerImage = item.answerImage ?? "";
            item.legend = item.legend ?? "";
            item.rows = (item.rows ?? new List<string>()).Select(r => r ?? "").ToList();
            item.labels = (item.labels ?? new List<string>()).Select(l => l == null ? "" : l.Trim()).ToList();
            item.dots = (item.dots ?? new List<QuizDotData>()).Where(d => d != null && Finite(d.ms)).OrderBy(d => d.ms).ToList();
            item.guides = (item.guides ?? new List<QuizGuideData>())
                .Where(g => g != null && Finite(g.ms) && g.lane >= 0 && g.lane < LaneCount).OrderBy(g => g.ms).ToList();
            item.ringsMs = (item.ringsMs ?? new List<float>()).Where(r => Finite(r)).OrderBy(r => r).ToList();
            if (!Finite(item.revealMs)) item.revealMs = 0f;
            if (item.answer < 0 || item.answer >= item.rows.Count) item.answer = -1;
        }
        return quiz;
    }

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

    // ---- 流れる玉・落ちる印 ----

    // rhythm の玉の x。届く時刻に的の中心、それより前は右に、あとは左へ同じ速さで進む。
    public static float BallX(double arrival, double time) => RhythmTargetX + RhythmSpeed * (float)(arrival - time);

    // 上から落ちる玉・印の y(targetY に届く時刻 arrival)。
    public static float FallY(float targetY, float speed, double arrival, double time) => targetY + speed * (float)(arrival - time);

    // 届く何秒前から見えるか(端 edge から的 target まで、speed で進む)。
    public static double LeadSeconds(float target, float edge, float speed)
        => speed > 0f ? Math.Max(0.0, (edge - target) / speed) : 0.0;

    // 届いたあとは PassFadeSeconds で薄れる。
    public static float PassFade(double arrival, double time)
        => time <= arrival ? 1f : Mathf.Clamp01(1f - (float)((time - arrival) / PassFadeSeconds));

    // 流れる玉の濃さ。練習の問題(hint)だけ、飾り(real=false)を薄くする。それ以外は本物と飾りが同じ見た目。
    public static float BallAlpha(bool hint, bool real, double arrival, double time)
        => (hint && !real ? HintDecoyAlpha : 1f) * PassFade(arrival, time);

    // 玉を描くか(右・上の端から入ってきたあと、届いて薄れ切る前)。
    public static bool BallShown(double arrival, double time, double leadSeconds)
        => time >= arrival - leadSeconds && time < arrival + PassFadeSeconds;

    // その問題の玉・印の現れ方(0〜1)。showMs より前は 0(次の問題の玉を前の問題の間に見せない)、showMs から AppearSeconds で 1。
    public static float AppearFade(QuizItemData item, double offset, double time)
    {
        if (item == null || !Finite(time)) return 0f;
        double since = time - Seconds(item.showMs, offset);
        return since < 0 ? 0f : (float)Math.Min(1.0, since / AppearSeconds);
    }

    // 行列のレーン(判定面で下の点 bottom から上の点 top へ)の、高さ y での x。下と上の点は画面の同じ横の位置から求めるので、
    // この線は画面でまっすぐ縦に見える(カメラが下を向いた透視でも傾かない)。y が下の点より下でも同じ線の延長。
    public static float LaneXAt(Vector2 bottom, Vector2 top, float y)
    {
        float span = top.y - bottom.y;
        if (Mathf.Abs(span) < 1e-5f) return bottom.x;
        return bottom.x + (top.x - bottom.x) * ((y - bottom.y) / span);
    }

    // 数える問題の式の大きさ(幅, 高さ)。高さは CountQuestionHeight、横に長い式は画面の左の端(leftEdgeX)の内側に収まるよう縮める。
    public static Vector2 CountQuestionSize(float aspect, float leftEdgeX)
    {
        aspect = aspect > .01f && !float.IsNaN(aspect) && !float.IsInfinity(aspect) ? aspect : 1f;
        float room = Mathf.Max(.5f, CountQuestionRightX - (leftEdgeX + CountQuestionLeftMargin));
        float height = Mathf.Min(CountQuestionHeight, room / aspect);
        return new Vector2(height * aspect, height);
    }

    // ---- 正しい行の緑 ----

    // 緑にする時間帯。練習の問題は playMs から endMs まで。ほかは最後の本物の玉が届いたあと、endMs までの最後の 0.4 秒だけ
    // (動画にない、答えを見せるための足し)。正しい行の無い問題は false。
    public static bool GreenSpan(QuizItemData item, double offset, out Span span)
    {
        span = default;
        if (item == null || item.rows == null || item.answer < 0 || item.answer >= item.rows.Count) return false;
        double end = Seconds(item.endMs, offset);
        double start;
        if (item.hint) start = Seconds(item.playMs, offset);
        else
        {
            start = end - RevealGreenSeconds;
            if (item.dots != null)
                foreach (var dot in item.dots)
                    if (dot != null && dot.real) start = Math.Max(start, Seconds(dot.ms, offset));
        }
        if (!(start < end)) return false;
        span = new Span(start, end);
        return true;
    }

    public static bool Green(QuizItemData item, double offset, double time)
        => GreenSpan(item, offset, out var span) && time >= span.Start && time < span.End;

    // count・formula: revealMs から answerImage に差し替える。
    public static bool Revealed(QuizItemData item, double offset, double time)
        => item != null && !string.IsNullOrEmpty(item.answerImage) && item.revealMs > 0f
            && time >= Seconds(item.revealMs, offset);

    // ---- 黒板の並び ----

    // rhythm の答えの行(0 が上)の中心の高さ。
    public static float RowCenterY(int row, int rowCount)
        => BoardBottomY + RowHeight * .5f + (rowCount - 1 - row) * (RowHeight + RowGap);

    // rhythm の問題の中心の高さ(行の上)。
    public static float QuestionCenterY(int rowCount)
        => BoardBottomY + rowCount * RowHeight + Math.Max(0, rowCount - 1) * RowGap + QuestionGap + QuestionHeight * .5f;

    // 左のレーンの x(lane 0 = A が左)。
    public static float LaneX(int lane, float laneLeftX) => laneLeftX + lane * LaneSpacing;

    // 4つの的のうち x にいちばん近いもの。
    public static int NearestMatrixTarget(float x)
    {
        int best = 0;
        for (int i = 1; i < MatrixTargetX.Length; i++)
            if (Math.Abs(MatrixTargetX[i] - x) < Math.Abs(MatrixTargetX[best] - x)) best = i;
        return best;
    }

    // count の金の小さな丸(0 から)の中心。問題の画像の左下から右へ並べる。
    public static Vector2 UnitMark(int index, float questionLeftX, float questionBottomY)
        => new Vector2(questionLeftX + UnitMarkRadius + index * UnitMarkSpacing, questionBottomY - UnitMarkGap - UnitMarkRadius);

    // count の答え(金の玉の数)。
    public static int UnitCount(QuizItemData item)
    {
        int count = 0;
        if (item?.dots != null) foreach (var dot in item.dots) if (dot != null && dot.unit) count++;
        return count;
    }

    // ---- 見えないノーツとの対応 ----

    // 届く時刻 hitTime の見えないノーツに対応する玉の番号(無ければ −1)。
    public static int FindDot(QuizItemData item, double offset, double hitTime)
    {
        if (item?.dots == null || !Finite(hitTime)) return -1;
        for (int i = 0; i < item.dots.Count; i++)
            if (item.dots[i] != null && Math.Abs(Seconds(item.dots[i].ms, offset) - hitTime) <= MatchSeconds) return i;
        return -1;
    }

    // 届く時刻 hitTime の見えないノーツに対応する印の番号(無ければ −1)。
    public static int FindGuide(QuizItemData item, double offset, double hitTime)
    {
        if (item?.guides == null || !Finite(hitTime)) return -1;
        for (int i = 0; i < item.guides.Count; i++)
            if (item.guides[i] != null && Math.Abs(Seconds(item.guides[i].ms, offset) - hitTime) <= MatchSeconds) return i;
        return -1;
    }

    // ---- 輪 ----

    static float EaseOut(double t)
    {
        float k = Mathf.Clamp01((float)t);
        return 1f - (1f - k) * (1f - k);
    }

    // 大問の白い輪(ringsMs からの経過 age 秒)。半径 2.2 → 6.0 をすっと広げ、濃さ 0.3 から消す。
    public static bool RingShown(double age) => age >= 0 && age < RingSeconds;
    public static float RingRadius(double age) => Mathf.Lerp(RingFromRadius, RingToRadius, EaseOut(age / RingSeconds));
    public static float RingOpacity(double age) => RingShown(age) ? RingAlpha * (1f - (float)(age / RingSeconds)) : 0f;

    // 前のコマから今のコマのあいだに時刻 at を越えたか(止まっている間・巻き戻しでは越えない)。
    public static bool Crossed(double previous, double now, double at) => previous < at && at <= now;

    // 見えないノーツを切ったときの弾け(的で輪が広がる)。
    public static bool PopShown(double age) => age >= 0 && age < PopSeconds;
    public static float PopRadius(float baseRadius, double age) => baseRadius * Mathf.Lerp(1f, PopGrowth, EaseOut(age / PopSeconds));
    public static float PopOpacity(double age) => PopShown(age) ? PopAlpha * (1f - (float)(age / PopSeconds)) : 0f;
}
