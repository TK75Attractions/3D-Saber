using System.Collections.Generic;
using UnityEngine;

// タイトルの問いかけで「はい」を選んだ人向けの練習。GamePlayManager の Update からだけ Tick する。
// 譜面・判定・演出・効果音は本番と同じ部品をそのまま使い、文は上部に 1 行、スキップは画面下の帯の照準だけ。
// 120 BPM の 2 小節ループ(SongPlayer の DSP 時計)で、音・判定・譜面の進行が同じ時計を見る。
// 終わったら(スキップしても)選曲へ進む。得点・実績は残さない。
public sealed class TutorialController : MonoBehaviour
{
    public const string MusicResourcePath = "Audio/Tutorial_Loop120";
    // 音源を曲と同じ -18 LUFS に作ってあるので、曲(SongPlayer の音量 1 × 曲別補正)と同じく 1 で鳴らす。
    public const float MusicVolume = 1f;
    public struct TutorialStepRecord { public string Id; public double Seconds; public string Reason; public bool FirstLoop; }

    public TutorialOverlay Overlay { get; private set; }
    public TutorialStep Step => steps != null && stepIndex >= 0 && stepIndex < steps.Length ? steps[stepIndex] : null;
    public int StepIndex => stepIndex;
    public int StepCount => steps != null ? steps.Length : 0;
    public int Successes { get; private set; }
    public int RemainingDirections => TutorialProgram.RemainingDirections(Step, completedDirections);
    public bool StepDone { get; private set; }
    public IReadOnlyList<NoteData> DemonstrationNotes => liveChart?.notes;
    public double DemonstrationTime => started && song != null ? Now : 0;
    public bool IsFinished { get; private set; }
    public string FinishReason { get; private set; }
    public string Hint { get; private set; }
    public string BannerTitle { get; private set; } = "";
    public string BannerSub { get; private set; } = "";
    public double ElapsedSeconds => started ? Now - startChartTime : 0;
    // 練習の 1 ステップを諦めて次へ進むまでの秒数。できない人を列で待たせない。
    public float PracticeTimeoutSeconds { get; set; } = TutorialProgram.DefaultStepTimeoutSeconds;
    public IReadOnlyList<TutorialStepRecord> Records => records;

    // 範囲を合わせる段(2026-10-24)。スマホの座標をこの秒数以内に受けていれば、最初に入れる。
    public const double PhoneActiveSeconds = 10;
    // スマホへの指示・返事の読み取り・スマホの有無。テストでは差し替えて、本物の指示を送らない。
    public static System.Func<bool> PhoneInputActive = DefaultPhoneInputActive;
    public static System.Func<int> SendLearnArea = DefaultSendLearnArea;
    public static System.Func<int, PhoneSaberAcknowledgement[]> ReadLearnArea = DefaultReadLearnArea;
    // 覚えた枠(カメラ画像の 0..1)を広げて、この台の位置補正として保存し直す。保存できたら true。
    public static System.Func<Rect, bool> ApplyWidenedRange = DefaultApplyWidenedRange;
    // 広げて保存した枠(カメラ画像の 0..1)。保存しなかったら null。
    public Rect? AppliedRange { get; private set; }
    public TutorialRangeLearning Range => range;
    public double RangeSecondsLeft => range != null && started ? range.SecondsLeft(Now) : 0;
    // 学習中に剣の両端が通った範囲(剣の平面の世界座標 XY)。覆いが枠で見せる。
    public bool HasRangeCover { get; private set; }
    public Rect RangeCover { get; private set; }
    public float RangeCoverZ { get; private set; }
    public Camera RangeCamera { get; private set; }

    SongPlayer song;
    NoteSpawner spawner;
    ScoreManager score;
    SaberTracker[] sabers = System.Array.Empty<SaberTracker>();
    AudioSource source;
    AudioClip music;
    float originalVolume;
    bool originalLoop;
    ChartData liveChart;
    TutorialStep[] steps;
    SaberUIPointer pointer;
    readonly List<CuttableNote> watched = new List<CuttableNote>();
    readonly Dictionary<CuttableNote, int> noteSteps = new Dictionary<CuttableNote, int>();
    readonly TutorialPairProgress pairs = new TutorialPairProgress();
    readonly HashSet<int> completedLongCounts = new HashSet<int>();
    readonly HashSet<CutDirection> completedDirections = new HashSet<CutDirection>();
    // 「練習してみよう」: 型の種類を届く時刻(ミリ秒)で引き、GREAT 以上を取った種類を覚える。
    readonly Dictionary<long, string> kindByHit = new Dictionary<long, string>();
    readonly HashSet<string> completedKinds = new HashSet<string>();
    bool timedOutPractice;
    TutorialRangeLearning range;
    SaberInputBridge[] bridges = System.Array.Empty<SaberInputBridge>();
    string station = "";
    bool rangeCheered;
    readonly List<TutorialStepRecord> records = new List<TutorialStepRecord>();
    double totalOffset, startChartTime, stepStart, stepEndAt, nextPatternAt, firstPatternAt;
    int stepIndex = -1, swingFrames;
    // 周の数が決まった段: 足した周の数と、判定が出た(切れた・見逃した)この段のノーツの数。
    int addedLoops, judgedNotes;
    float hintUntil;
    bool started, cleanedUp;
    // 用意ができた時刻(範囲合わせの始まりの記録 range-start の wait に使う。2026-10-30 夜、改善案⑥)。
    double initializedAt;

    // 譜面の時計(曲時計から判定のずらし分を引いたもの)。NoteData.time と同じ基準。
    double Now => song.SongTime - totalOffset;

    // rangeOnly: チュートリアルで「いいえ」を選んだ人向けに、振る範囲を合わせる段だけを流す(終わったら選曲へ)。
    public bool RangeOnly { get; private set; }

    public void Initialize(SongPlayer player, NoteSpawner notes, ScoreManager scoring, double extraOffset,
        SaberTracker[] saberTrackers, LongNoteCutSfx longSfx, GoldNoteSfx goldSfx, bool rangeOnly = false)
    {
        RangeOnly = rangeOnly;
        song = player; spawner = notes; score = scoring;
        sabers = saberTrackers ?? System.Array.Empty<SaberTracker>();
        // 本番と同じ「判定のずらし」を掛けて、曲に入ったときと同じ手触りにする。
        totalOffset = extraOffset + GameSession.JudgmentOffsetMs / 1000.0;
        score.songPlayer = song; score.Reset(); score.Bind(spawner);
        if (longSfx != null) longSfx.Bind(spawner);
        if (goldSfx != null) goldSfx.Bind(spawner);
        spawner.SetExtraOffsetSeconds(totalOffset);
        liveChart = new ChartData { bpm = TutorialProgram.Bpm };
        spawner.SetChart(liveChart);
        spawner.OnNoteSpawned += WatchNote;
        // 旧 HUD は出さない(判定調整と同じ)。判定文字はこの画面の覆いが出す。
        foreach (var hud in UnityEngine.Object.FindObjectsByType<ScoreHUD>(FindObjectsSortMode.None))
        {
            hud.enabled = false;
            if (hud.scoreText != null) hud.scoreText.gameObject.SetActive(false);
            if (hud.comboText != null) hud.comboText.gameObject.SetActive(false);
            if (hud.maxComboText != null) hud.maxComboText.gameObject.SetActive(false);
            if (hud.tierText != null) hud.tierText.gameObject.SetActive(false);
            if (hud.flickWarningText != null) hud.flickWarningText.gameObject.SetActive(false);
        }
        // 4 秒ちょうどの PCM 音源をループし、音声の再生位置に DSP 時計を合わせる。
        source = song.GetComponent<AudioSource>();
        originalVolume = source.volume; originalLoop = source.loop;
        source.Stop(); source.playOnAwake = false; source.loop = true; source.pitch = 1f; source.spatialBlend = 0f;
        source.volume = MusicVolume;
        music = Resources.Load<AudioClip>(MusicResourcePath);
        if (music == null) Debug.LogError("Tutorial: 練習用の音源が見つかりません: " + MusicResourcePath);
        song.Clip = music;
        StartCoroutine(StartMusicWhenVisible());
        Overlay = TutorialOverlay.Ensure();
        Overlay.Bind(this, score);
        // スキップの的は画面下の帯だけで受け付け、通常の振りで押されないようにする(判定調整と同じ)。
        pointer = SaberUIPointer.Build(useAimReticle: true);
        pointer.BottomControlArea = Overlay.Controls;
        pointer.RemapToFullScreen = true;
        pointer.RespectRaycastBlockers = true;
        pointer.BottomControlsOnly = true;
        pointer.HideOutsideBottomControls = true;
        // 範囲の枠は剣と同じカメラで画面に写す(PlayRecoveryView と同じ選び方)。
        bridges = UnityEngine.Object.FindObjectsByType<SaberInputBridge>(FindObjectsSortMode.None);
        RangeCamera = Camera.main;
        foreach (var bridge in bridges) if (bridge != null && bridge.targetCamera != null) { RangeCamera = bridge.targetCamera; break; }
        station = InputPoint.Instance != null ? InputPoint.Instance.StationLabel : "";
        bool phone = PhoneInputActive != null && PhoneInputActive();
        steps = TutorialProgram.Build(rangeOnly ? TutorialProgram.RangeOnlyFlow(phone) : TutorialProgram.FlowFor(phone), true);
        initializedAt = Time.realtimeSinceStartupAsDouble;
    }

    // 範囲合わせの始まりを events.log に1行(改善案⑥): 用意ができてから待った秒と、始めた時点で座標が届いている剣の数(赤・青、1秒以内)。
    void RecordRangeStart()
    {
        var input = InputPoint.Instance;
        int sabers = input == null ? 0 : (input.IsRecentlyActive() ? 1 : 0) + (input.IsRecentlyActive2() ? 1 : 0);
        double wait = System.Math.Max(0, Time.realtimeSinceStartupAsDouble - initializedAt);
        PhoneSaberEventLog.Record(station, "SYSTEM", "range-start",
            "wait=" + wait.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " sabers=" + sabers + (RangeOnly ? " only=1" : ""));
    }

    // テストの SetUpFixture は自動起動を止める。そのときは指示を送らない(探索応答も開かないので届かない)。
    static bool DefaultPhoneInputActive()
    {
        var input = InputPoint.Instance;
        return PhoneSaberP2PBridgeProcess.AutoStartEnabled && input != null &&
            (input.IsRecentlyActive(PhoneActiveSeconds) || input.IsRecentlyActive2(PhoneActiveSeconds));
    }

    // 自動の指示として送り、スタッフ向けのトーストは出さない(F8 の状態欄と events.log には残る)。
    static int DefaultSendLearnArea()
    {
        int id = PhoneSaberDiscoveryResponder.QueueCommand(PhoneSaberDiscoveryResponder.LearnAreaCommand, automatic: true);
        PhoneSaberEventLog.Record(InputPoint.Instance != null ? InputPoint.Instance.StationLabel : "", "SYSTEM",
            "tutorial-learn-area", "id=" + id);
        return id;
    }

    static PhoneSaberAcknowledgement[] DefaultReadLearnArea(int id) => PhoneSaberDiscoveryResponder.Acknowledgements.Read(id);

    // F8 と同じ学習矩形の変換・保存・スマホ範囲更新を使う。
    static bool DefaultApplyWidenedRange(Rect learned)
    {
        var input = InputPoint.Instance;
        if (input == null || (input.StationLabel != "A" && input.StationLabel != "B")) return false;
        return ApplyLearnedRange(input, learned);
    }

    public static bool ApplyLearnedRange(InputPoint input, Rect learned)
    {
        bool saved = input.ApplyLearnedPositionCalibration(learned, null, out string error);
        if (!saved) Debug.LogWarning("Tutorial: 広げた範囲を保存できませんでした: " + error);
        else
            PhoneSaberDiscoveryResponder.Acknowledgements.DiscardLearnedAreas(input.StationLabel);
        return saved;
    }

    System.Collections.IEnumerator StartMusicWhenVisible()
    {
        while (ScreenTransition.IsBusy) yield return null;
        if (!IsFinished && song != null) song.PlayScheduled(AudioSettings.dspTime + .35);
    }

    public void Tick(float frameSeconds)
    {
        if (steps == null) return;
        if (!IsFinished && song != null && song.IsPlaying)
        {
            double now = Now;
            // 範囲だけの回で、タイトルからここへ来るまでにスマホの座標が途切れたら段が無い。そのまま選曲へ。
            if (!started) { started = true; startChartTime = now; if (steps.Length == 0) Finish("no-steps"); else BeginStep(0, now); }
            var step = Step;
            if (step != null && step.Kind == TutorialStepKind.Practice && !StepDone) ExtendPattern(now, step);
            spawner.Tick(song.SongTime);
            if (step != null)
            {
                if (!StepDone)
                {
                    double elapsed = now - stepStart;
                    switch (step.Kind)
                    {
                        case TutorialStepKind.Practice:
                        {
                            // 合格したらすぐ次へ。周の数が決まった段(左右交互=最大2周、ほかの練習=最大1周)は、
                            // 合格しないまま最後の周の判定が全部出たら次へ。
                            string outcome = TutorialProgram.PracticeOutcome(step,
                                TutorialProgram.PracticeGoalReached(step, Successes, completedDirections), judgedNotes, now, firstPatternAt, elapsed,
                                step.TimeoutSeconds > 0 ? step.TimeoutSeconds : PracticeTimeoutSeconds);
                            if (outcome != null) Complete(now, outcome);
                            break;
                        }
                        case TutorialStepKind.Explain:
                            swingFrames = AnySaberSwinging() ? swingFrames + 1 : 0;
                            if ((elapsed >= TutorialProgram.HandsMinimumSeconds && swingFrames >= 3) || elapsed >= step.TimeoutSeconds) Complete(now, "ok");
                            break;
                        case TutorialStepKind.Ready:
                            if (elapsed >= step.TimeoutSeconds) Complete(now, "ok");
                            break;
                        case TutorialStepKind.Range:
                            TickRange(now);
                            break;
                    }
                }
                else if (now >= stepEndAt) Next(now);
            }
        }
        if (Hint != null && Time.unscaledTime > hintUntil) Hint = null;
        if (Overlay != null) Overlay.Tick(frameSeconds);
    }

    // 成功するまで同じ型を出し続ける(周の数が決まった段はその周の数だけ)。先読み分だけ譜面の末尾に足し、過去には足さない。
    void ExtendPattern(double now, TutorialStep step)
    {
        double horizon = now + spawner.approachTime + .3;
        int guard = 0;
        while (nextPatternAt <= horizon && (step.Loops <= 0 || addedLoops < step.Loops) && guard++ < 8)
        {
            foreach (var p in step.Pattern)
            {
                var note = p.ToNote(nextPatternAt);
                liveChart.notes.Add(note);
                // 出たノーツの HitTime は spawner.EffectiveTime と同じ値になる
                if (!string.IsNullOrEmpty(p.kind)) kindByHit[HitKey(spawner.EffectiveTime(note))] = p.kind;
            }
            addedLoops++;
            nextPatternAt += System.Math.Max(.5, step.PeriodSeconds);
        }
    }

    static long HitKey(double hitTime) => (long)System.Math.Round(hitTime * 1000.0);

    void BeginStep(int index, double now)
    {
        stepIndex = index; Successes = 0; StepDone = false; swingFrames = 0; Hint = null;
        stepStart = now;
        addedLoops = 0; judgedNotes = 0;
        pairs.Clear();
        completedLongCounts.Clear();
        completedDirections.Clear();
        kindByHit.Clear();
        completedKinds.Clear();
        var step = Step;
        range = null; HasRangeCover = false; rangeCheered = false; AppliedRange = null;
        if (step.Kind == TutorialStepKind.Range) RecordRangeStart();
        if (step.Kind == TutorialStepKind.Range && SendLearnArea != null)
        {
            // タイトルに戻ったら台の準備の範囲へ戻すので、変える前の位置補正を覚えておく(2026-10-24 ユーザー「B にして」)。
            TutorialRangeBaseline.Remember(station);
            range = new TutorialRangeLearning(SendLearnArea(), now);
        }
        nextPatternAt = TutorialProgram.NextBarAtOrAfter(now + TutorialProgram.ReadDelaySeconds + spawner.approachTime);
        firstPatternAt = nextPatternAt;
        BannerTitle = step.Kind == TutorialStepKind.Ready && timedOutPractice ? "曲を選びに行こう" : step.Title;
        BannerSub = step.Sub;
        if (Overlay != null) Overlay.ShowStep(step, index, steps.Length);
    }

    void Complete(double now, string reason)
    {
        if (StepDone) return;
        StepDone = true;
        var step = Step;
        stepEndAt = now + (step.Kind == TutorialStepKind.Range ? TutorialProgram.RangeEndSeconds
            : reason == "timeout" ? TutorialProgram.TimeoutEndSeconds : TutorialProgram.StepEndSeconds);
        records.Add(new TutorialStepRecord
        {
            Id = step.Id, Seconds = now - stepStart, Reason = reason,
            FirstLoop = step.Id == TutorialProgram.AllStepId && reason == "ok"
                && now >= firstPatternAt && now < firstPatternAt + step.PeriodSeconds,
        });
        if (step.Kind == TutorialStepKind.Range)
        {
            BannerTitle = reason == "ok" ? TutorialProgram.RangeLearnedTitle : TutorialProgram.RangeKeptTitle; BannerSub = "";
            TutorialRangeBaseline.Finish(station, reason == "ok");
            Debug.Log($"Tutorial: range {reason} id={range?.CommandId} station={station} widened={(AppliedRange.HasValue ? AppliedRange.Value.ToString("F3") : "-")}");
        }
        else if (reason == "timeout") { timedOutPractice = true; BannerTitle = "次に進むよ"; BannerSub = "あとは曲の中で慣れよう"; }
        else if (step.Kind == TutorialStepKind.Practice) { BannerTitle = "OK!"; BannerSub = ""; }
        DropUnspawnedNotes();
        if (Overlay != null) Overlay.ShowBanner(BannerTitle, BannerSub);
    }

    // スマホの返事を見て進める。学習中だけ剣の届いた範囲を広げ、半分を過ぎたら一度だけ声をかける。
    // 返事が無い・覚えられなかったときは、今の補正のまま次へ進む(列を待たせない)。
    void TickRange(double now)
    {
        if (range == null) { Complete(now, "noanswer"); return; }
        var acks = ReadLearnArea != null ? ReadLearnArea(range.CommandId) : null;
        var state = range.Update(now, acks, station);
        Rect learned = default;
        bool applied = state == TutorialRangeLearning.Phase.Learned && TutorialRangeLearning.TryLearnedBounds(acks, station, out learned)
            && ApplyWidenedRange != null && ApplyWidenedRange(learned);
        if (applied)
            AppliedRange = PhoneSaberPositionCalibration.WidenLearnedArea(learned);
        else if (state == TutorialRangeLearning.Phase.Learned)
        {
            var input = InputPoint.Instance;
            Debug.LogWarning("Tutorial: 範囲を適用できませんでした: " +
                (input != null ? input.LearnedPositionCalibrationResult : "InputPoint がありません"));
        }
        if (state == TutorialRangeLearning.Phase.Learning)
        {
            GrowRangeCover();
            if (!rangeCheered && range.SecondsLeft(now) <= TutorialProgram.RangeCheerSeconds)
            {
                rangeCheered = true;
                SetHint(TutorialProgram.RangeCheer);
            }
        }
        if (range.Finished)
            Complete(now, applied ? "ok"
                : state == TutorialRangeLearning.Phase.NoAnswer ? "noanswer" : "failed");
    }

    void GrowRangeCover()
    {
        foreach (var bridge in bridges)
        {
            if (bridge == null || !bridge.isActiveAndEnabled) continue;
            if (bridge.HasBlade) { GrowRangeCover(bridge.WorldEndA); GrowRangeCover(bridge.WorldEndB); }
            else GrowRangeCover(bridge.transform.position);
        }
    }

    void GrowRangeCover(Vector3 p)
    {
        var r = HasRangeCover ? RangeCover : new Rect(p.x, p.y, 0, 0);
        RangeCover = Rect.MinMaxRect(Mathf.Min(r.xMin, p.x), Mathf.Min(r.yMin, p.y), Mathf.Max(r.xMax, p.x), Mathf.Max(r.yMax, p.y));
        RangeCoverZ = p.z;
        HasRangeCover = true;
    }

    // まだ出ていない型は消す。流れている途中のノーツは最後まで見せる。
    void DropUnspawnedNotes()
    {
        if (liveChart == null || spawner == null) return;
        int next = spawner.NextIndex;
        if (liveChart.notes.Count > next) liveChart.notes.RemoveRange(next, liveChart.notes.Count - next);
    }

    void Next(double now)
    {
        if (stepIndex + 1 >= steps.Length) Finish("done");
        else BeginStep(stepIndex + 1, now);
    }

    public void Skip()
    {
        if (IsFinished) return;
        var step = Step;
        if (step != null && !StepDone) records.Add(new TutorialStepRecord { Id = step.Id, Seconds = started ? Now - stepStart : 0, Reason = "skipped" });
        Finish("skipped");
    }

    void Finish(string reason)
    {
        if (IsFinished) return;
        IsFinished = true; FinishReason = reason;
        double total = started ? Now - startChartTime : 0;
        var summary = new System.Text.StringBuilder();
        foreach (var r in records) summary.Append(r.Id).Append('=').Append(r.Seconds.ToString("F1")).Append('/').Append(r.Reason).Append(' ');
        Debug.Log($"Tutorial: {reason} total={total:F1}s steps=[{summary}]");
        DropUnspawnedNotes();
        if (Overlay != null) Overlay.ShowFinished();
        if (pointer != null) pointer.gameObject.SetActive(false);
        if (!GamePlayManager.ExitTutorial()) Debug.LogWarning("Tutorial: 選曲へ移れませんでした");
    }

    void WatchNote(CuttableNote note)
    {
        watched.Add(note);
        noteSteps[note] = stepIndex;
        note.OnJudged += Judged;
        note.OnMiss += Missed;
    }

    void Judged(CuttableNote note, JudgmentTier tier, Vector3 point, Vector3 velocity)
    {
        var step = Step;
        if (step == null || StepDone || IsFinished || !noteSteps.TryGetValue(note, out int owner) || owner != stepIndex) return;
        judgedNotes++;
        if (TutorialProgram.CountsAsSuccess(step, tier, note.LastCutCorrectDirection, note.RequiredCutCount, note.CutsAchieved))
        {
            if (step.Kinds.Length > 0)
            {
                // 練習してみよう: 種類ごとに1回。両手は左右とも GREAT 以上の組がそろったときに数える。
                if (kindByHit.TryGetValue(HitKey(note.HitTime), out string kind)
                    && (kind != TutorialProgram.SimulKind || pairs.Register(note.HitTime, note.RequiredHand)))
                    completedKinds.Add(kind);
                Successes = completedKinds.Count;
            }
            else if (step.Id == "long")
            {
                // 同じ回数だけ繰り返しても通過させず、2・3 回をそれぞれ切り切る。
                if (completedLongCounts.Add(note.RequiredCutCount)) Successes++;
            }
            else if (!step.Simultaneous)
            {
                Successes++;
                if (step.NeedDirection) completedDirections.Add(note.RequiredDirection);
            }
            else if (pairs.Register(note.HitTime, note.RequiredHand)) Successes += 2;
            return;
        }
        if (note.RequiredCutCount > 1) SetHint("数字がなくなるまで、同じ場所を切ろう");
        // 切れたが GREAT 以上でなかった(GOOD・BAD、数えない)。早い・遅いを知らせる。
        else if (tier != JudgmentTier.Miss && score != null && score.LastErrorValid) SetHint(TutorialProgram.NotPerfectHint(score.LastErrorMs));
    }

    void Missed(CuttableNote note)
    {
        var step = Step;
        if (step == null || step.Kind != TutorialStepKind.Practice || StepDone || IsFinished
            || !noteSteps.TryGetValue(note, out int owner) || owner != stepIndex) return;
        judgedNotes++;
        SetHint(note.RequiredCutCount > 1 ? "数字の回数だけ、同じ場所を切ろう"
            : TutorialProgram.MissHint(ColorName(note), note.RequiredDirection != CutDirection.None));
    }

    static string ColorName(CuttableNote note) =>
        note.IsGold ? "gold" : note.RequiredHand == SaberHand.Left ? "blue" : "red";

    void SetHint(string text, float seconds = 2.2f)
    {
        Hint = text;
        hintUntil = Time.unscaledTime + seconds;
    }

    bool AnySaberSwinging()
    {
        foreach (var saber in sabers)
            if (saber != null && saber.HasPrevious && saber.Speed >= TutorialProgram.SwingSpeedToAdvance) return true;
        return false;
    }

    void OnDestroy()
    {
        if (cleanedUp) return;
        cleanedUp = true;
        if (spawner != null) spawner.OnNoteSpawned -= WatchNote;
        foreach (var n in watched) if (n != null) { n.OnJudged -= Judged; n.OnMiss -= Missed; }
        watched.Clear();
        if (song != null) song.Stop();
        if (source != null)
        {
            if (source.clip == music) source.clip = null;
            source.volume = originalVolume; source.loop = originalLoop;
        }
        // Resources の共有音源は破棄しない。チュートリアルへ入り直すときにも使う。
        music = null;
    }
}
