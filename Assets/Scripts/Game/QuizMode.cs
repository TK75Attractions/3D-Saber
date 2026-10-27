using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

// 勉強の裏譜面「Q」(2026-10-27、Milthm「Algebra」の4つの仕組み)。曲の途中で画面を黒い試験用紙に切り替え、
// 問題・楽譜・流れる玉・的・行列のレーン・数える玉・数式と輪を描く。決まりと数値は QuizRules。
// 切る相手は見えないノーツ(NoteData.hidden。NoteSpawner が合図ごと隠す)で、判定・採点・音はふつうのノーツと同じ。
// 進行は GamePlayManager の Tick(曲の時計)だけ。絵はすべて曲の時計で決めるので、一時停止の間は止まり、時計どおりに進む。
// 試験用紙: カメラのすぐ前に画面を覆う黒い板を、M のノイズ・停電の暗幕と同じ段(MemoryVeilLayers.NoiseSortingOrder)に置く。
//   背景・景色・床・小節線・床の印・ステージの光とレーザー・ゲートの枠は板の下になる(各部品には手を入れない)。
//   板の上に Q の絵、見えるノーツ(AboveNoteSortingOrder)、剣と切った光(AboveSortingOrder)を描く。
//   HUD(点・コンボ・判定の文字)とコンボの炎は画面の一番上の Canvas なので、CanvasGroup で板と同じだけ薄める。
//   黒は頂点色 0 の板(霧・光・発光が無く、プロジェクターの露出の上げ幅でも黒のまま)。
public sealed partial class QuizMode : MonoBehaviour
{
    // 並び順。M・停電とは同じ譜面で重ならない。
    public const int PaperSortingOrder = MemoryVeilLayers.NoiseSortingOrder;
    public const int ImageSortingOrder = PaperSortingOrder + 1;
    public const int MarkSortingOrder = PaperSortingOrder + 2;
    // 判定面から手前へ少しだけ出す(深さは見ないが、画像の代わりの材質のときに枠などに埋もれないように)。
    const float MarkLift = .02f, ImageLift = .1f;

    ChartData chart;
    QuizChartData quiz;
    NoteSpawner spawner;
    string folder = "";
    Camera view;
    Transform board;
    QuizMesh paper, marks;
    double offset = double.NaN;
    List<QuizRules.Span> papers = new List<QuizRules.Span>();
    bool hasRhythm, hasMatrix, hasCount, hasFormula;
    QuizRules.Span rhythmSpan, matrixSpan, countSpan, formulaSpan;
    bool ended, previousCutFeedbackLift;
    double previousTime = double.NaN;
    float judgeZ;
    float rightEdgeX = QuizRules.DefaultRightEdgeX, topEdgeY = QuizRules.DefaultTopEdgeY, leftEdgeX = QuizRules.DefaultLeftEdgeX;
    // 数える問題の式の高さでの、画面の左の端(横に長い式をこの内側に収める)。
    float countLeftEdgeX = QuizRules.DefaultLeftEdgeX;
    // 行列の左のレーン(判定面の下の点と上の点)。画面の同じ横の位置を通るので、画面ではまっすぐ縦に見える。
    readonly Vector2[] laneBottoms = new Vector2[QuizRules.LaneCount];
    readonly Vector2[] laneTops = new Vector2[QuizRules.LaneCount];
    readonly Vector3[] paperCorners = new Vector3[4];
    float fittedNear = float.NaN, fittedFov = float.NaN, fittedAspect = float.NaN;
    float nextSaberRefresh;

    // HUD と、コンボの炎・節目の字(GameplayFeedbackPresenter)。試験用紙と同じだけ薄め、終わりに元へ戻す。
    CanvasGroup hudGroup, feedbackGroup;
    float hudAlpha = 1f, feedbackAlpha = 1f;

    readonly List<(SaberInputBridge bridge, bool above)> bridges = new List<(SaberInputBridge, bool)>();
    readonly List<(MouseSaberInput mouse, bool above)> mice = new List<(MouseSaberInput, bool)>();
    // 試験用紙の上へ上げた描画(見えるノーツ・着地の枠・残り回数・同時押しの線・割れた破片)と、元の並び順。
    readonly Dictionary<Renderer, int> originalOrders = new Dictionary<Renderer, int>();
    readonly List<Renderer> noteRenderers = new List<Renderer>();
    readonly List<Renderer> partRenderers = new List<Renderer>();
    readonly List<SimultaneousNoteLink> links = new List<SimultaneousNoteLink>();
    int liftedPieceChildren = -1;
    Action<CuttableNote> onSpawned;
    Action<CuttableNote, Vector3, Vector3> onHiddenCut;
    Action<CuttableNote, int, int> onPartialCut;
    Action onChartReset;

    // 見えないノーツを切ったときの弾け。start は切った次の Tick の曲の時計(それまで NaN)。
    struct Pop { public Vector2 center; public float radius; public Color color; public double start; }
    readonly List<Pop> pops = new List<Pop>();
    // 切った本物の玉(問題の番号, 玉の番号)。その玉は的で弾けて消える。
    readonly HashSet<long> popped = new HashSet<long>();
    // 大問の輪の始まり(曲の時計)。
    readonly List<double> ringStarts = new List<double>();

    public float PaperVisibility { get; private set; }
    public IReadOnlyList<QuizRules.Span> Papers => papers;
    public QuizChartData Quiz => quiz;
    public double OffsetSeconds => offset;
    public MeshRenderer PaperRenderer => paper != null ? paper.Renderer : null;
    public Mesh PaperMesh => paper != null ? paper.Mesh : null;
    public MeshRenderer MarkRenderer => marks != null ? marks.Renderer : null;
    public bool Ended => ended;
    // このコマに描いた弾けと輪の数(テスト用)。
    public int ActivePops { get; private set; }
    public int ActiveRings { get; private set; }
    public float RightEdgeX => rightEdgeX;
    public float TopEdgeY => topEdgeY;
    public float LeftEdgeX => leftEdgeX;
    // 行列のレーンの下の点(小さな丸)と上の点(画面の上の端の少し外)(テスト用)。
    public Vector2 LaneBottom(int lane) => laneBottoms[Mathf.Clamp(lane, 0, QuizRules.LaneCount - 1)];
    public Vector2 LaneTop(int lane) => laneTops[Mathf.Clamp(lane, 0, QuizRules.LaneCount - 1)];
    // このコマに描いた玉(rhythm・count)と行列の印の数(テスト用)。
    public int BallsDrawn { get; private set; }
    public int GuidesDrawn { get; private set; }

    // camera を省くと Camera.main を使う。テストは自分のカメラを渡す(ほかのテストが残した MainCamera を拾わないため)。
    public static QuizMode Create(Transform parent, ChartData chart, NoteSpawner spawner, string songFolder, Camera camera = null)
    {
        if (chart?.quiz == null || !chart.quiz.HasContent) return null;
        var go = new GameObject("QuizMode");
        go.transform.SetParent(parent, false);
        var mode = go.AddComponent<QuizMode>();
        mode.Initialize(chart, spawner, songFolder, camera);
        return mode;
    }

    void Initialize(ChartData source, NoteSpawner noteSpawner, string songFolder, Camera camera)
    {
        chart = source;
        quiz = QuizRules.Normalize(source.quiz);
        spawner = noteSpawner;
        folder = songFolder ?? "";
        judgeZ = spawner != null ? spawner.judgeZ : 0f;
        view = camera != null ? camera : Camera.main;
        RefreshTiming();
        MeasureEdges();

        var boardObject = new GameObject("QuizBoard");
        board = boardObject.transform;
        board.SetParent(transform, false);
        marks = new QuizMesh(board, "QuizMarks", MarkSortingOrder, world: true);
        if (view != null) paper = new QuizMesh(view.transform, "QuizPaper", PaperSortingOrder, world: false);
        PrepareImages();
        PrepareLabels();
        FindCanvases();
        LiftSabers();

        if (spawner != null)
        {
            previousCutFeedbackLift = spawner.CutFeedbackAbovePaper;
            spawner.CutFeedbackAbovePaper = true;
            onSpawned = HandleSpawned;
            onHiddenCut = HandleHiddenCut;
            onPartialCut = HandlePartialCut;
            onChartReset = EndPlay;
            spawner.OnNoteSpawned += onSpawned;
            spawner.OnChartReset += onChartReset;
            foreach (var note in spawner.LiveNotes) HandleSpawned(note);
        }
    }

    void RefreshTiming()
    {
        double next = spawner != null ? spawner.TotalOffsetSeconds : chart != null ? chart.offsetMs / 1000.0 : 0.0;
        if (next == offset) return;
        offset = next;
        papers = QuizRules.BuildPapers(quiz, offset);
        hasRhythm = QuizRules.KindSpan(quiz, QuizRules.Rhythm, offset, out rhythmSpan);
        hasMatrix = QuizRules.KindSpan(quiz, QuizRules.Matrix, offset, out matrixSpan);
        hasCount = QuizRules.KindSpan(quiz, QuizRules.Count, offset, out countSpan);
        hasFormula = QuizRules.KindSpan(quiz, QuizRules.Formula, offset, out formulaSpan);
    }

    // 判定面で見えている端(右の端は rhythm の線の高さ、上の端は真ん中、左の端は上の端の高さ)。
    // 玉は右・上の画面の端から入り、行列のレーンと数える問題の凡例は左の端の近くに置く。カメラが無ければ既定値のまま。
    void MeasureEdges()
    {
        if (view != null)
        {
            if (RayToPlane(view, new Vector2(.5f, 1f), judgeZ, out Vector3 top)) topEdgeY = top.y;
            if (RayToPlane(view, new Vector2(0f, 1f), judgeZ, out Vector3 topLeft)) leftEdgeX = topLeft.x;
            float lineY = view.WorldToViewportPoint(new Vector3(QuizRules.RhythmTargetX, QuizRules.RhythmLineY, judgeZ)).y;
            if (QuizRules.Finite(lineY) && RayToPlane(view, new Vector2(1f, lineY), judgeZ, out Vector3 right)) rightEdgeX = right.x;
            float countY = view.WorldToViewportPoint(new Vector3(0f, QuizRules.CountQuestionCenterY, judgeZ)).y;
            if (QuizRules.Finite(countY) && RayToPlane(view, new Vector2(0f, countY), judgeZ, out Vector3 countLeft))
                countLeftEdgeX = countLeft.x;
        }
        MeasureLanes();
    }

    // 行列のレーン。下の点(小さな丸)は左の端の近くに間隔 0.28 で並べ、上の点はその点と画面の同じ横の位置を通る視線が
    // 判定面と交わる所(画面の上の端の少し外)。カメラが下を向いた透視でも、レーンと落ちる印の道が画面でまっすぐ縦に見える。
    // カメラが無ければ判定面でまっすぐ縦。
    void MeasureLanes()
    {
        float left = leftEdgeX + QuizRules.LaneMargin;
        for (int lane = 0; lane < QuizRules.LaneCount; lane++)
        {
            var bottom = new Vector2(QuizRules.LaneX(lane, left), QuizRules.LaneBottomY);
            var top = new Vector2(bottom.x, topEdgeY + .3f);
            if (view != null)
            {
                Vector3 viewport = view.WorldToViewportPoint(new Vector3(bottom.x, bottom.y, judgeZ));
                if (QuizRules.Finite(viewport.x) && viewport.z > 0f
                    && RayToPlane(view, new Vector2(viewport.x, 1.03f), judgeZ, out Vector3 point) && point.y > bottom.y + .5f)
                    top = new Vector2(point.x, point.y);
            }
            laneBottoms[lane] = bottom;
            laneTops[lane] = top;
        }
    }

    static bool RayToPlane(Camera camera, Vector2 viewport, float z, out Vector3 point)
    {
        point = default;
        Ray ray = camera.ViewportPointToRay(new Vector3(viewport.x, viewport.y, 0f));
        if (Mathf.Abs(ray.direction.z) < 1e-5f) return false;
        float k = (z - ray.origin.z) / ray.direction.z;
        if (k <= 0f || float.IsNaN(k) || float.IsInfinity(k)) return false;
        point = ray.origin + ray.direction * k;
        return true;
    }

    public void Tick(double songTime)
    {
        if (ended || !QuizRules.Finite(songTime)) return;
        RefreshTiming();
        // 絵はすべて曲の時計で決める(時計が戻っても、まだ始まっていない弾けと輪は描かれないだけ)。
        float visibility = QuizRules.PaperVisibility(papers, songTime);
        PaperVisibility = visibility;
        RefreshSabers();
        DrawPaper(visibility);
        FadeCanvases(visibility);
        StartPops(songTime);
        TriggerRings(songTime, visibility);
        DrawBoard(songTime, visibility);
        LiftPieces();
        previousTime = songTime;
    }

    // ---- 試験用紙 ----

    void DrawPaper(float visibility)
    {
        if (paper == null) return;
        paper.Clear();
        if (visibility > 0f && view != null)
        {
            FitPaper();
            // 黒は 0(線形の色空間でも、露出を上げても黒のまま)。濃さだけを出入りに使う。
            paper.Quad(paperCorners[0], paperCorners[1], paperCorners[2], paperCorners[3], new Color(0f, 0f, 0f, visibility));
        }
        paper.Flush();
    }

    // カメラのすぐ前(near + 0.05)に、画角の2倍の板(撮影で縦横比が変わっても端が欠けない)。カメラが変わったときだけ作り直す。
    void FitPaper()
    {
        if (view.nearClipPlane == fittedNear && view.fieldOfView == fittedFov && view.aspect == fittedAspect) return;
        fittedNear = view.nearClipPlane; fittedFov = view.fieldOfView; fittedAspect = view.aspect;
        float depth = Mathf.Min(view.nearClipPlane + .05f, (view.nearClipPlane + view.farClipPlane) * .5f);
        view.CalculateFrustumCorners(new Rect(0, 0, 1, 1), depth, Camera.MonoOrStereoscopicEye.Mono, paperCorners);
        Vector3 center = (paperCorners[0] + paperCorners[1] + paperCorners[2] + paperCorners[3]) * .25f;
        for (int i = 0; i < paperCorners.Length; i++) paperCorners[i] = center + (paperCorners[i] - center) * 2f;
    }

    // ---- HUD ----

    void FindCanvases()
    {
        var hud = Object.FindFirstObjectByType<GameHUDSkin>();
        if (hud != null) hudGroup = Group(hud.gameObject, out hudAlpha);
        var feedback = Object.FindFirstObjectByType<GameplayFeedbackPresenter>();
        if (feedback != null) feedbackGroup = Group(feedback.gameObject, out feedbackAlpha);
    }

    static CanvasGroup Group(GameObject owner, out float original)
    {
        var group = owner.GetComponent<CanvasGroup>();
        if (group == null) group = owner.AddComponent<CanvasGroup>();
        original = group.alpha;
        return group;
    }

    void FadeCanvases(float visibility)
    {
        float keep = 1f - Mathf.Clamp01(visibility);
        if (hudGroup != null) hudGroup.alpha = hudAlpha * keep;
        if (feedbackGroup != null) feedbackGroup.alpha = feedbackAlpha * keep;
    }

    void RestoreCanvases()
    {
        if (hudGroup != null) hudGroup.alpha = hudAlpha;
        if (feedbackGroup != null) feedbackGroup.alpha = feedbackAlpha;
    }

    // ---- 剣・ノーツ・破片を試験用紙の上へ ----

    // 剣(刃・縁・残光)は M のノイズと同じ仕組み(SaberMemoryVeilOrder)で一番上へ。終わりに元へ戻す。
    void LiftSabers()
    {
        // Bridge と Mouse が同じ物体に付いていても、変更前の値を両方とも先に記録する。
        foreach (var bridge in Object.FindObjectsByType<SaberInputBridge>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (bridge.gameObject.scene == gameObject.scene) bridges.Add((bridge, bridge.DrawAboveMemoryVeil));
        foreach (var mouse in Object.FindObjectsByType<MouseSaberInput>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (mouse.gameObject.scene == gameObject.scene) mice.Add((mouse, mouse.DrawAboveMemoryVeil));
        foreach (var item in bridges) item.bridge.DrawAboveMemoryVeil = true;
        foreach (var item in mice) item.mouse.DrawAboveMemoryVeil = true;
    }

    // 後から作られる刃の部品の取りこぼしの保険(0.5 秒ごと。毎コマ材質を写さない)。
    void RefreshSabers()
    {
        if (Time.unscaledTime < nextSaberRefresh) return;
        nextSaberRefresh = Time.unscaledTime + .5f;
        foreach (var item in bridges) if (item.bridge != null) SaberMemoryVeilOrder.RefreshFor(item.bridge);
        foreach (var item in mice) if (item.mouse != null) SaberMemoryVeilOrder.RefreshFor(item.mouse);
    }

    void RestoreSabers()
    {
        foreach (var item in bridges) if (item.bridge != null) item.bridge.DrawAboveMemoryVeil = item.above;
        foreach (var item in mice) if (item.mouse != null) item.mouse.DrawAboveMemoryVeil = item.above;
        bridges.Clear();
        mice.Clear();
    }

    void HandleSpawned(CuttableNote note)
    {
        if (note == null || ended) return;
        // 見えないノーツは描かない(合図ごと隠れている)。切ったら的で弾ける。購読は出現ごとに ResetForSpawn が外す。
        if (note.IsHidden)
        {
            note.OnCut += onHiddenCut;
            return;
        }
        LiftNote(note);
        // ロングは切るたびにひびを足すので、そのたびに上げ直す。
        if (note.RequiredCutCount > 1) note.OnPartialCut += onPartialCut;
    }

    void HandlePartialCut(CuttableNote note, int cutIndex, int total)
    {
        if (!ended && note != null && !note.IsHidden) LiftNote(note);
    }

    // 見えるノーツ(本体・矢印・ひび・着地の枠・残り回数・同時押しの線)を、試験用紙と剣のあいだに描く。
    // 試験用紙の外でもそのまま(ステージの上に描くだけで見え方は変わらない)。終わりに元の並び順へ戻す。
    void LiftNote(CuttableNote note)
    {
        noteRenderers.Clear();
        note.GetComponentsInChildren(true, noteRenderers);
        if (note.TimingCue != null)
        {
            AddRenderers(note.TimingCue.GhostRoot);
            AddRenderers(note.TimingCue.ApproachRoot);
        }
        if (note.countLabel != null) AddRenderers(note.countLabel.gameObject);
        foreach (var renderer in noteRenderers) Lift(renderer, MemoryVeilLayers.AboveNoteSortingOrder);
        if (spawner != null && spawner.noteRoot != null)
        {
            links.Clear();
            spawner.noteRoot.GetComponentsInChildren(true, links);
            foreach (var link in links)
                if (link != null && link.Line != null && (link.noteA == note || link.noteB == note))
                    Lift(link.Line, MemoryVeilLayers.AboveNoteSortingOrder);
        }
    }

    void AddRenderers(GameObject root)
    {
        if (root == null) return;
        partRenderers.Clear();
        root.GetComponentsInChildren(true, partRenderers);
        noteRenderers.AddRange(partRenderers);
    }

    void Lift(Renderer renderer, int order)
    {
        if (renderer == null) return;
        if (!originalOrders.ContainsKey(renderer)) originalOrders[renderer] = renderer.sortingOrder;
        renderer.sortingOrder = order;
    }

    // 見えるノーツの割れた破片も試験用紙の上へ(見えないノーツの破片は描かれないので、上げても見えない)。
    // 破片は使い回すので、数が増えたときだけ上げ直す。
    void LiftPieces()
    {
        var pool = spawner != null ? spawner.FragmentPool : null;
        if (pool == null) return;
        int count = pool.transform.childCount;
        if (count == liftedPieceChildren) return;
        liftedPieceChildren = count;
        partRenderers.Clear();
        pool.GetComponentsInChildren(true, partRenderers);
        foreach (var renderer in partRenderers) Lift(renderer, MemoryVeilLayers.AboveNoteSortingOrder);
    }

    void RestoreOrders()
    {
        foreach (var pair in originalOrders)
            if (pair.Key != null) pair.Key.sortingOrder = pair.Value;
        originalOrders.Clear();
        liftedPieceChildren = -1;
    }

    // ---- 見えないノーツを切ったとき ----

    static long Key(int item, int dot) => ((long)item << 32) | (uint)dot;

    // 切った本物の玉か(テスト用)。
    public bool IsDotPopped(int item, int dot) => popped.Contains(Key(item, dot));

    // 数える問題で、切った金の玉の数(問題の下に並べる金の小さな丸の数)。
    public int UnitMarks(int item)
    {
        if (quiz?.items == null || item < 0 || item >= quiz.items.Count) return 0;
        var dots = quiz.items[item].dots;
        int count = 0;
        for (int d = 0; d < dots.Count; d++)
            if (dots[d] != null && dots[d].unit && popped.Contains(Key(item, d))) count++;
        return count;
    }

    // 本物の玉はその玉が的で弾けて消え、行列は切った的で輪が広がる(どれもノーツの届く時刻で対応を探す)。
    void HandleHiddenCut(CuttableNote note, Vector3 point, Vector3 velocity)
    {
        if (ended || note == null || !note.IsCut || note.IsMissed || quiz?.items == null) return;
        double hit = note.HitTime;
        for (int i = 0; i < quiz.items.Count; i++)
        {
            var item = quiz.items[i];
            if (item.kind == QuizRules.Rhythm || item.kind == QuizRules.Count)
            {
                int dot = QuizRules.FindDot(item, offset, hit);
                if (dot < 0) continue;
                popped.Add(Key(i, dot));
                bool rhythm = item.kind == QuizRules.Rhythm;
                AddPop(rhythm ? new Vector2(QuizRules.RhythmTargetX, QuizRules.RhythmLineY)
                        : new Vector2(QuizRules.CountLineX, QuizRules.CountTargetY),
                    rhythm ? QuizRules.RhythmTargetRadius : QuizRules.CountTargetRadius,
                    item.dots[dot].unit ? UISkinPalette.NoteGold : Color.white);
                return;
            }
            if (item.kind == QuizRules.Matrix && QuizRules.FindGuide(item, offset, hit) >= 0)
            {
                int target = QuizRules.NearestMatrixTarget(note.LandingPosition.x);
                AddPop(new Vector2(QuizRules.MatrixTargetX[target], QuizRules.MatrixTargetY), QuizRules.MatrixTargetRadius, Color.white);
                return;
            }
        }
        // 対応する玉・印の無い見えないノーツは、ノーツの場所で弾ける。
        AddPop(note.LandingPosition, QuizRules.RhythmTargetRadius, Color.white);
    }

    void AddPop(Vector2 center, float radius, Color color)
    {
        pops.Add(new Pop { center = center, radius = radius, color = color, start = double.NaN });
    }

    void StartPops(double time)
    {
        for (int i = pops.Count - 1; i >= 0; i--)
        {
            var pop = pops[i];
            if (!QuizRules.Finite(pop.start))
            {
                pop.start = time;
                pops[i] = pop;
            }
            else if (time - pop.start >= QuizRules.PopSeconds) pops.RemoveAt(i);
        }
    }

    // ---- 大問の輪 ----

    // ringsMs を越えたコマで、全画面の大きな光の予算(PlayExcitement.TryLargeFlash、1 秒に 3 回まで)に乗せてから輪を出す。
    // 予算の光は試験用紙の下に描かれるので見えない(輪だけが見える)。F3 の弱める設定では輪を出さない。
    void TriggerRings(double time, float visibility)
    {
        for (int i = ringStarts.Count - 1; i >= 0; i--)
            if (time - ringStarts[i] >= QuizRules.RingSeconds) ringStarts.RemoveAt(i);
        if (!hasFormula || !QuizRules.Finite(previousTime) || time <= previousTime) return;
        foreach (var item in quiz.items)
        {
            if (item.kind != QuizRules.Formula) continue;
            foreach (float ms in item.ringsMs)
            {
                double at = QuizRules.Seconds(ms, offset);
                if (!QuizRules.Crossed(previousTime, time, at)) continue;
                if (DisplaySettings.ReducedEffects || visibility <= 0f) continue;
                var excitement = PlayExcitement.Current;
                if (excitement != null && !excitement.TryLargeFlash(LargeFlashKind.Edge, LargeFlashTint.White,
                        QuizRules.RingAlpha, (float)QuizRules.RingSeconds)) continue;
                ringStarts.Add(at);
            }
        }
    }

    // ---- 終わり ----

    public void EndPlay()
    {
        if (ended) return;
        ended = true;
        PaperVisibility = 0f;
        if (paper != null) paper.Hide();
        if (marks != null) marks.Hide();
        HideBoard();
        RestoreCanvases();
        RestoreSabers();
        if (spawner != null)
        {
            spawner.CutFeedbackAbovePaper = previousCutFeedbackLift;
            if (onSpawned != null) spawner.OnNoteSpawned -= onSpawned;
            if (onChartReset != null) spawner.OnChartReset -= onChartReset;
            foreach (var note in spawner.LiveNotes)
            {
                if (note == null) continue;
                if (onHiddenCut != null) note.OnCut -= onHiddenCut;
                if (onPartialCut != null) note.OnPartialCut -= onPartialCut;
            }
        }
        RestoreOrders();
        pops.Clear();
        ringStarts.Clear();
    }

    void OnDestroy() => ReleaseResources();

    // 終わりの片付けと、作った板・材質・画像の解放(EditMode では OnDestroy が呼ばれないので、テストから直接呼ぶ)。
    public void ReleaseResources()
    {
        EndPlay();
        paper?.Dispose();
        marks?.Dispose();
        paper = marks = null;
        DisposeBoard();
    }
}
