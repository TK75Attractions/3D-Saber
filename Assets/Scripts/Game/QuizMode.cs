using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

// 勉強の裏譜面「Q」第3版(2026-10-28)。曲の途中で画面を黒い試験用紙に切り替え、問題・札・箱・数・桁・赤ペンを描く。決まりと数値は QuizRules。
// 切る相手はこのゲームの金のノーツ(ふつうのノーツ)で、判定・採点・音はいつもどおり。答えの柱の飾りの金は NoteSpawner.Decoy が飛ばし、
// ここは試験用紙の上へ上げるだけ。発射点・隠した向きの見た目も NoteSpawner と CuttableNote が受け持つ(ここは札を描く)。
// 進行は GamePlayManager の Tick(曲の時計)だけ。絵は曲の時計と、切った・逃したの記録で決めるので、一時停止の間は止まる。
// 試験用紙: カメラのすぐ前に画面を覆う黒い板を、M のノイズ・停電の暗幕と同じ段(MemoryVeilLayers.NoiseSortingOrder)に置く。
//   背景・景色・床・小節線・ステージの光とレーザー・ゲートの枠は板の下になる(各部品には手を入れない)。
//   床の印(FloorTimingGuide)は紙の間だけ止める。飾りの金には床の印が無いので、紙の出入りの薄い間に本物の印だけが透けないよう
//   (仕様「同じにできない部品は、試験用紙の間は本物の側も出さない」)。床の判定線も紙の下なので見え方は変わらない。
//   板の上に Q の絵、ノーツ(本物も飾りも AboveNoteSortingOrder)、剣と切った光(AboveSortingOrder)を描く。
//   HUD(点・コンボ・判定の文字)とコンボの炎は画面の一番上の Canvas なので、CanvasGroup で板と同じだけ薄める。
//   黒は頂点色 0 の板(霧・光・発光が無く、プロジェクターの露出の上げ幅でも黒のまま)。紙の縁の光もこの板に重ねて描く。
public sealed partial class QuizMode : MonoBehaviour
{
    // 並び順。M・停電とは同じ譜面で重ならない。問題・札・桁の字と赤ペン・線は、ノーツ・切った光・剣より手前に描く
    // (黒い紙の上では、切った光の輪や割れた破片が字を隠した。Codex の点検、10/28)。紙だけがステージより上・ノーツより下。
    public const int PaperSortingOrder = MemoryVeilLayers.NoiseSortingOrder;
    public const int ImageSortingOrder = MemoryVeilLayers.AboveSortingOrder + 10;
    public const int MarkSortingOrder = MemoryVeilLayers.AboveSortingOrder + 11;
    // 判定面から手前へ少しだけ出す(深さは見ないが、画像の代わりの材質のときに枠などに埋もれないように)。
    const float MarkLift = .02f, ImageLift = .1f;
    // 切った・逃したの記録(SlotState)。
    public const int SlotWaiting = 0, SlotCut = 1, SlotMissed = 2;

    ChartData chart;
    QuizChartData quiz;
    NoteSpawner spawner;
    string folder = "";
    Camera view;
    Transform board;
    QuizMesh paper, marks;
    double offset = double.NaN;
    float timedApproach = float.NaN;
    List<QuizRules.Span> papers = new List<QuizRules.Span>();
    bool hasRhythm, hasSort, hasCount, hasPi;
    QuizRules.Span rhythmSpan, sortSpan, countSpan, piSpan;
    bool ended, previousCutFeedbackLift, floorGuideStopped;
    Camera previousLaunchView;
    double previousTime = double.NaN;
    float judgeZ;
    readonly Vector3[] paperCorners = new Vector3[4];
    float fittedNear = float.NaN, fittedFov = float.NaN, fittedAspect = float.NaN;
    float nextSaberRefresh;

    // HUD と、コンボの炎・節目の字(GameplayFeedbackPresenter)。試験用紙と同じだけ薄め、終わりに元へ戻す。
    CanvasGroup hudGroup, feedbackGroup;
    float hudAlpha = 1f, feedbackAlpha = 1f;

    readonly List<(SaberInputBridge bridge, bool above)> bridges = new List<(SaberInputBridge, bool)>();
    readonly List<(MouseSaberInput mouse, bool above)> mice = new List<(MouseSaberInput, bool)>();
    // 試験用紙の上へ上げた描画(ノーツ・飾りの金・着地の枠・残り回数・同時押しの線・割れた破片)と、元の並び順。
    readonly Dictionary<Renderer, int> originalOrders = new Dictionary<Renderer, int>();
    readonly List<Renderer> noteRenderers = new List<Renderer>();
    readonly List<Renderer> partRenderers = new List<Renderer>();
    readonly List<SimultaneousNoteLink> links = new List<SimultaneousNoteLink>();
    int liftedPieceChildren = -1;
    Action<CuttableNote> onSpawned, onDecoySpawned, onMissed;
    Action<CuttableNote, Vector3, Vector3> onCut;
    Action<CuttableNote, int, int> onPartialCut;
    Action onChartReset;

    // 問題ごとのノーツ(譜面の時刻順、同じ時刻は x の順)。sort は札の順、pi はマスの順、count は届いた数。
    struct Slot { public int item; public int index; public float ms; public float x; }
    readonly List<Slot> slots = new List<Slot>();
    int[] slotState = new int[0];
    int[] itemFirstSlot = new int[0], itemSlotCount = new int[0];
    // 問題ごとのノーツの届く時刻(曲の時計)と、pi の行が埋まる時刻(その行の最後のノーツが届く時刻)。
    List<double>[] itemTimes = new List<double>[0];
    readonly List<double> piRowEnds = new List<double>();
    // 答えの柱の札が金に光り始めた曲の時計(問題ごと。切った次の Tick で入れる。カメラの遅れで光が短くならないように)。
    double[] optionFlashStart = new double[0];
    int[] optionFlashPillar = new int[0];
    bool[] optionFlashPending = new bool[0];
    // 紙の縁の光の始まり(曲の時計)。
    readonly List<double> edgeFlashStarts = new List<double>();
    readonly List<FlashRect> flashRects = new List<FlashRect>();

    public float PaperVisibility { get; private set; }
    public IReadOnlyList<QuizRules.Span> Papers => papers;
    public QuizChartData Quiz => quiz;
    public double OffsetSeconds => offset;
    public MeshRenderer PaperRenderer => paper != null ? paper.Renderer : null;
    public Mesh PaperMesh => paper != null ? paper.Mesh : null;
    public MeshRenderer MarkRenderer => marks != null ? marks.Renderer : null;
    public bool Ended => ended;
    // 床の印を止めているか(紙の間)。
    public bool FloorGuideStopped => floorGuideStopped;
    // このコマに描いた赤ペンの丸・薄い四角の数、ゲートの枠、紙の縁の光の数(テスト用)。
    public int RedCircles { get; private set; }
    public int Squares { get; private set; }
    public bool GateOutlineDrawn { get; private set; }
    public int ActiveEdgeFlashes { get; private set; }

    // 問題 item のノーツの数と、index 番目の記録(SlotWaiting / SlotCut / SlotMissed)(テスト用)。
    public int SlotCount(int item) => item >= 0 && item < itemSlotCount.Length ? itemSlotCount[item] : 0;
    public int SlotState(int item, int index)
        => item >= 0 && item < itemSlotCount.Length && index >= 0 && index < itemSlotCount[item]
            ? slotState[itemFirstSlot[item] + index] : SlotWaiting;
    // count: 時刻 time までに届いた金の数(「?」の所に出す数)。
    public int CountShown(int item, double time)
        => item >= 0 && item < itemTimes.Length ? QuizRules.CountAt(itemTimes[item], time) : 0;
    // pi: 行 row の今の高さ。
    public float RowY(int row, double time) => QuizRules.RowY(row, piRowEnds, time);

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
        BuildSlots();
        RefreshTiming();

        var boardObject = new GameObject("QuizBoard");
        board = boardObject.transform;
        board.SetParent(transform, false);
        marks = new QuizMesh(board, "QuizMarks", MarkSortingOrder, world: true);
        if (view != null) paper = new QuizMesh(view.transform, "QuizPaper", PaperSortingOrder, world: false);
        PrepareImages();
        FindCanvases();
        LiftSabers();

        if (spawner != null)
        {
            previousCutFeedbackLift = spawner.CutFeedbackAbovePaper;
            spawner.CutFeedbackAbovePaper = true;
            // 発射点の奥の点は、札を描くこのカメラから決める。
            previousLaunchView = spawner.LaunchView;
            if (view != null) spawner.LaunchView = view;
            onSpawned = HandleSpawned;
            onDecoySpawned = HandleDecoySpawned;
            onCut = HandleCut;
            onMissed = HandleMissed;
            onPartialCut = HandlePartialCut;
            onChartReset = EndPlay;
            spawner.OnNoteSpawned += onSpawned;
            spawner.OnDecoySpawned += onDecoySpawned;
            spawner.OnChartReset += onChartReset;
            foreach (var note in spawner.LiveNotes) HandleSpawned(note);
            foreach (var decoy in spawner.LiveDecoys) HandleDecoySpawned(decoy);
        }
    }

    // 問題ごとに、譜面のノーツ(見えないノーツは除く)を showMs〜endMs から集める。
    void BuildSlots()
    {
        slots.Clear();
        piRowEnds.Clear();
        int count = quiz.items.Count;
        itemFirstSlot = new int[count];
        itemSlotCount = new int[count];
        itemTimes = new List<double>[count];
        optionFlashStart = new double[count];
        optionFlashPillar = new int[count];
        optionFlashPending = new bool[count];
        float scale = chart != null && chart.coordScale > 0f ? chart.coordScale : 1f;
        var picked = new List<NoteData>();
        for (int i = 0; i < count; i++)
        {
            var item = quiz.items[i];
            itemFirstSlot[i] = slots.Count;
            itemTimes[i] = new List<double>();
            optionFlashStart[i] = double.NaN;
            optionFlashPillar[i] = -1;
            picked.Clear();
            if (chart?.notes != null)
                foreach (var note in chart.notes)
                    if (note != null && !note.hidden && note.time >= item.showMs - .5f && note.time < item.endMs - .5f) picked.Add(note);
            picked.Sort((a, b) => { int c = a.time.CompareTo(b.time); return c != 0 ? c : a.x.CompareTo(b.x); });
            for (int k = 0; k < picked.Count; k++)
                slots.Add(new Slot { item = i, index = k, ms = picked[k].time, x = picked[k].x * scale });
            itemSlotCount[i] = slots.Count - itemFirstSlot[i];
        }
        slotState = new int[slots.Count];
    }

    // 曲の時計の時刻(オフセット・接近の時間が変わったら作り直す)。
    void RefreshTiming()
    {
        double next = spawner != null ? spawner.TotalOffsetSeconds : chart != null ? chart.offsetMs / 1000.0 : 0.0;
        float approach = spawner != null ? spawner.approachTime : 1f;
        if (next == offset && approach == timedApproach) return;
        offset = next;
        timedApproach = approach;
        hasRhythm = QuizRules.KindSpan(quiz, QuizRules.Rhythm, offset, out rhythmSpan);
        hasSort = QuizRules.KindSpan(quiz, QuizRules.Sort, offset, out sortSpan);
        hasCount = QuizRules.KindSpan(quiz, QuizRules.Count, offset, out countSpan);
        hasPi = QuizRules.KindSpan(quiz, QuizRules.Pi, offset, out piSpan);
        RefreshTimes();
        papers = QuizRules.BuildPapers(quiz, offset);
        // 接近 2.0 秒では斬り分けの1問目の金が問題の前に出る。その札を見せるため、板と一緒に紙も早める(ほかの問題の板は紙の中)。
        int first = FirstItem(QuizRules.Sort);
        if (first >= 0) papers = QuizRules.CoverBoard(papers, SortBoardStart(first), QuizRules.SortCoverLeadSeconds);
    }

    void RefreshTimes()
    {
        for (int i = 0; i < itemTimes.Length; i++)
        {
            var times = itemTimes[i];
            times.Clear();
            for (int k = 0; k < itemSlotCount[i]; k++) times.Add(QuizRules.Seconds(slots[itemFirstSlot[i] + k].ms, offset));
        }
        // 行数だけを用意する。行送りの開始は、全マスの切断・MISSが確定してから記録する。
        int pi = FirstItem(QuizRules.Pi);
        if (pi < 0) return;
        var rows = quiz.items[pi].rows;
        for (int k = 0; k < itemSlotCount[pi]; k++)
        {
            QuizRules.CellOf(rows, k, out int row, out _);
            while (piRowEnds.Count <= row) piRowEnds.Add(double.NaN);
        }
    }

    void AdvancePiRows(double time)
    {
        int pi = FirstItem(QuizRules.Pi);
        if (pi < 0) return;
        int first = itemFirstSlot[pi], cells = itemSlotCount[pi];
        int begin = 0;
        for (int row = 0; row < piRowEnds.Count; row++)
        {
            int end = row + 1 == piRowEnds.Count
                ? cells : Math.Min(cells, begin + QuizRules.RowLength(quiz.items[pi].rows, row));
            if (!QuizRules.Finite(piRowEnds[row]))
            {
                bool filled = end > begin;
                for (int k = begin; k < end && filled; k++)
                    filled = slotState[first + k] != SlotWaiting;
                // 入力の遅れがあっても、最後の桁を描くコマから行送りを見せる。
                if (filled) piRowEnds[row] = time;
            }
            begin = end;
        }
    }

    int FirstItem(string kind)
    {
        for (int i = 0; i < quiz.items.Count; i++) if (quiz.items[i].kind == kind) return i;
        return -1;
    }

    // 斬り分けの問題の板を出す時刻(show と、最初の金が出る時刻 − 0.2 秒の早い方)。
    double SortBoardStart(int index)
    {
        var item = quiz.items[index];
        double show = QuizRules.Seconds(item.showMs, offset);
        double firstHit = itemSlotCount[index] > 0 ? itemTimes[index][0] : QuizRules.Seconds(item.playMs, offset);
        double firstSpawn = spawner != null ? spawner.SpawnTimeFor(firstHit) : firstHit - 1.0;
        return QuizRules.SortBoardStart(show, firstSpawn);
    }

    // その時刻に板を出している斬り分けの問題(無ければ −1)。板は SortBoardStart から次の問題の板が出るまで(最後の問題は endMs まで)。
    // 前の問題の飛んでいる金はそのまま(ノーツは NoteSpawner のもの)。
    public int ActiveSortItem(double time)
    {
        if (quiz?.items == null || !QuizRules.Finite(time)) return -1;
        int current = -1;
        for (int i = 0; i < quiz.items.Count; i++)
        {
            if (quiz.items[i].kind != QuizRules.Sort) continue;
            if (time < SortBoardStart(i)) break;
            current = i;
        }
        if (current >= 0 && time >= QuizRules.Seconds(quiz.items[current].endMs, offset)) return -1;
        return current;
    }

    public void Tick(double songTime)
    {
        if (ended || !QuizRules.Finite(songTime)) return;
        RefreshTiming();
        // 絵はすべて曲の時計と記録で決める(時計が戻っても、まだ始まっていない光は描かれないだけ)。
        float visibility = QuizRules.PaperVisibility(papers, songTime);
        PaperVisibility = visibility;
        if (spawner != null) spawner.CutFeedbackSizeScale = Mathf.Lerp(1f, QuizRules.PaperCutFeedbackScale, visibility);
        StartFlashes(songTime);
        AdvancePiRows(songTime);
        TriggerEdgeFlashes(songTime, visibility);
        RefreshSabers();
        StopFloorGuide(visibility);
        DrawPaper(songTime, visibility);
        FadeCanvases(visibility);
        DrawBoard(songTime, visibility);
        LiftPieces();
        previousTime = songTime;
    }

    // ---- 試験用紙 ----

    void DrawPaper(double time, float visibility)
    {
        if (paper == null) return;
        paper.Clear();
        ActiveEdgeFlashes = 0;
        if (visibility > 0f && view != null)
        {
            FitPaper();
            // 黒は 0(線形の色空間でも、露出を上げても黒のまま)。濃さだけを出入りに使う。
            paper.Quad(paperCorners[0], paperCorners[1], paperCorners[2], paperCorners[3], new Color(0f, 0f, 0f, visibility));
            DrawEdgeFlashes(time, visibility);
        }
        paper.Flush();
    }

    // カメラのすぐ前(near + 0.05)に、画角の2倍の板(撮影で縦横比が変わっても端が欠けない)。カメラが変わったときだけ作り直す。
    void FitPaper()
    {
        if (view.nearClipPlane == fittedNear && view.fieldOfView == fittedFov && view.aspect == fittedAspect) return;
        fittedNear = view.nearClipPlane; fittedFov = view.fieldOfView; fittedAspect = view.aspect;
        view.CalculateFrustumCorners(new Rect(0, 0, 1, 1), PaperDepth(), Camera.MonoOrStereoscopicEye.Mono, paperCorners);
        Vector3 center = (paperCorners[0] + paperCorners[1] + paperCorners[2] + paperCorners[3]) * .25f;
        for (int i = 0; i < paperCorners.Length; i++) paperCorners[i] = center + (paperCorners[i] - center) * 2f;
    }

    float PaperDepth() => Mathf.Min(view.nearClipPlane + .05f, (view.nearClipPlane + view.farClipPlane) * .5f);

    // 画面の割合 (x, y) の点を、紙のメッシュの座標(カメラの中の座標)で。
    Vector3 PaperPoint(double x, double y, float depth)
        => view.transform.InverseTransformPoint(view.ViewportToWorldPoint(new Vector3((float)x, (float)y, depth)));

    // 判定面のゲートの枠の、画面での矩形(0〜1)。カメラの後ろにかかるときは空。
    FlashRect GateViewportRect()
    {
        double left = double.PositiveInfinity, bottom = double.PositiveInfinity;
        double right = double.NegativeInfinity, top = double.NegativeInfinity;
        for (int sx = -1; sx <= 1; sx += 2)
            for (int sy = 0; sy <= 1; sy++)
            {
                Vector3 p = view.WorldToViewportPoint(new Vector3(sx * QuizRules.GateHalfWidth,
                    sy == 0 ? QuizRules.GateBottomY : QuizRules.GateTopY, judgeZ));
                if (p.z <= 0f || !QuizRules.Finite(p.x) || !QuizRules.Finite(p.y)) return new FlashRect(0, 0, 0, 0);
                left = Math.Min(left, p.x); right = Math.Max(right, p.x);
                bottom = Math.Min(bottom, p.y); top = Math.Max(top, p.y);
            }
        return new FlashRect(left, bottom, right, top);
    }

    // ---- 紙の縁の光(pi の盛り上がり) ----

    // 前のコマから越えた flashesMs ごとに、全画面の大きな光の予算(PlayExcitement.TryLargeFlash、1 秒に 3 回まで)に乗せてから出す。
    // 予算の光は試験用紙の下に描かれるので見えず、紙の上にはここが描く。F3 の弱める設定・紙が出ていないときは出さない。
    void TriggerEdgeFlashes(double time, float visibility)
    {
        for (int i = edgeFlashStarts.Count - 1; i >= 0; i--)
        {
            double age = time - edgeFlashStarts[i];
            if (age < 0 || age >= QuizRules.EdgeFlashSeconds) edgeFlashStarts.RemoveAt(i);
        }
        if (!hasPi || !QuizRules.Finite(previousTime) || time <= previousTime) return;
        foreach (var item in quiz.items)
        {
            if (item.kind != QuizRules.Pi) continue;
            foreach (float ms in item.flashesMs)
            {
                double at = QuizRules.Seconds(ms, offset);
                if (!QuizRules.Crossed(previousTime, time, at)) continue;
                if (DisplaySettings.ReducedEffects || visibility <= 0f) continue;
                var excitement = PlayExcitement.Current;
                if (excitement != null && !excitement.TryLargeFlash(LargeFlashKind.Edge, LargeFlashTint.White,
                        QuizRules.EdgeFlashAlpha, (float)QuizRules.EdgeFlashSeconds)) continue;
                edgeFlashStarts.Add(at);
            }
        }
    }

    // 画面の縁 8% の白い帯(LargeFlashLayout と同じ形)。ゲートの内側は光らせない(画面でのゲートの矩形と、その外の 24px を抜く)。
    void DrawEdgeFlashes(double time, float visibility)
    {
        if (edgeFlashStarts.Count == 0 || DisplaySettings.ReducedEffects) return;
        LargeFlashLayout.Build(LargeFlashKind.Edge, LargeFlashLayout.ProtectedGate(GateViewportRect()), flashRects);
        float depth = PaperDepth();
        foreach (double start in edgeFlashStarts)
        {
            float alpha = QuizRules.EdgeFlashOpacity(time - start) * visibility;
            if (alpha <= 0f) continue;
            ActiveEdgeFlashes++;
            var color = new Color(1f, 1f, 1f, alpha);
            foreach (var r in flashRects)
                paper.Quad(PaperPoint(r.Left, r.Bottom, depth), PaperPoint(r.Left, r.Top, depth),
                    PaperPoint(r.Right, r.Top, depth), PaperPoint(r.Right, r.Bottom, depth), color);
        }
    }

    // ---- 床の印 ----

    // 紙が出ている間(出入りの途中も)だけ床の印を止め、紙が消えたら戻す。元から止まっていた床の印には触れない。
    void StopFloorGuide(float visibility)
    {
        var guide = spawner != null ? spawner.FloorGuide : null;
        if (guide == null) return;
        bool stop = visibility > 0f;
        if (stop == floorGuideStopped) return;
        if (stop)
        {
            if (!guide.gameObject.activeSelf) return;
            guide.gameObject.SetActive(false);
            floorGuideStopped = true;
        }
        else
        {
            guide.gameObject.SetActive(true);
            floorGuideStopped = false;
        }
    }

    void RestoreFloorGuide()
    {
        var guide = spawner != null ? spawner.FloorGuide : null;
        if (floorGuideStopped && guide != null) guide.gameObject.SetActive(true);
        floorGuideStopped = false;
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
        // 見えないノーツは描かれない(今の Q では使わない)。購読は出現ごとに ResetForSpawn が外す。
        if (note == null || ended || note.IsHidden) return;
        LiftNote(note);
        MuteOmenOnPaper(note);
        // ロングは切るたびにひびを足すので、そのたびに上げ直す。
        if (note.RequiredCutCount > 1) note.OnPartialCut += onPartialCut;
        // 問題のノーツなら、切った・逃したを記録する(札の色・π の桁)。
        if (FindSlot(note) >= 0)
        {
            note.OnCut += onCut;
            note.OnMiss += onMissed;
        }
    }

    // 飾りの金も本物と同じ段へ上げる(本物とどこかで見分けがつくと答えがばれる)。
    void HandleDecoySpawned(CuttableNote decoy)
    {
        if (decoy == null || ended) return;
        LiftNote(decoy);
        MuteOmenOnPaper(decoy);
    }

    // 試験用紙の間に届くノーツは、金の予兆の「チリッ」を鳴らさない(光は出す)。本物と飾りに同じに立てるので、音で答えはばれない。
    void MuteOmenOnPaper(CuttableNote note)
    {
        if (note.TimingCue != null && QuizRules.InPaper(papers, note.HitTime)) note.TimingCue.MuteOmen = true;
    }

    void HandlePartialCut(CuttableNote note, int cutIndex, int total)
    {
        if (!ended && note != null && !note.IsHidden) LiftNote(note);
    }

    // ノーツ(本体・矢印・ひび・着地の枠・残り回数・同時押しの線)を、試験用紙と剣のあいだに描く。
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

    // 割れた破片も試験用紙の上へ。破片は使い回すので、数が増えたときだけ上げ直す。
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

    // ---- 切った・逃したの記録 ----

    // ノーツの問題とその中の番号(届く時刻が同じで、x がいちばん近いもの)。無ければ −1。
    int FindSlot(CuttableNote note)
    {
        if (note == null || !QuizRules.Finite(offset)) return -1;
        double hit = note.HitTime;
        float x = note.LandingPosition.x;
        int best = -1;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < slots.Count; i++)
        {
            if (Math.Abs(QuizRules.Seconds(slots[i].ms, offset) - hit) > QuizRules.MatchSeconds) continue;
            float distance = Math.Abs(slots[i].x - x);
            if (distance < bestDistance) { bestDistance = distance; best = i; }
        }
        return best;
    }

    void HandleCut(CuttableNote note, Vector3 point, Vector3 velocity)
    {
        if (ended || note == null) return;
        // 部分達成のロングが窓を逃したときも OnCut が来る(IsMissed 付き)。
        Record(FindSlot(note), note.IsCut && !note.IsMissed ? SlotCut : SlotMissed);
    }

    void HandleMissed(CuttableNote note)
    {
        if (!ended && note != null) Record(FindSlot(note), SlotMissed);
    }

    void Record(int slot, int state)
    {
        if (slot < 0 || slot >= slotState.Length || slotState[slot] != SlotWaiting) return;
        slotState[slot] = state;
        var s = slots[slot];
        // 答えの柱: 本物を切るたびに、その柱の札を 0.15 秒だけ金に光らせる(次の Tick の曲の時計から)。
        if (state == SlotCut && quiz.items[s.item].kind == QuizRules.Rhythm)
        {
            optionFlashPillar[s.item] = QuizRules.NearestPillar(s.x);
            optionFlashPending[s.item] = true;
        }
    }

    void StartFlashes(double time)
    {
        for (int i = 0; i < optionFlashPending.Length; i++)
        {
            if (!optionFlashPending[i]) continue;
            optionFlashPending[i] = false;
            optionFlashStart[i] = time;
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
        RestoreFloorGuide();
        if (spawner != null) spawner.CutFeedbackSizeScale = 1f;
        if (spawner != null)
        {
            spawner.CutFeedbackAbovePaper = previousCutFeedbackLift;
            if (view != null && spawner.LaunchView == view) spawner.LaunchView = previousLaunchView;
            if (onSpawned != null) spawner.OnNoteSpawned -= onSpawned;
            if (onDecoySpawned != null) spawner.OnDecoySpawned -= onDecoySpawned;
            if (onChartReset != null) spawner.OnChartReset -= onChartReset;
            foreach (var note in spawner.LiveNotes)
            {
                if (note == null) continue;
                if (onCut != null) note.OnCut -= onCut;
                if (onMissed != null) note.OnMiss -= onMissed;
                if (onPartialCut != null) note.OnPartialCut -= onPartialCut;
            }
            // 飾りの金は Q の間だけのもの。曲の終わり・途中でやめたとき・譜面のやり直しで消し、残りも出さない。
            spawner.ClearDecoys();
        }
        RestoreOrders();
        edgeFlashStarts.Clear();
        ActiveEdgeFlashes = 0;
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
