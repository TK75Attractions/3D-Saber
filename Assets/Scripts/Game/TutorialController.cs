using System.Collections.Generic;
using UnityEngine;

// タイトルの問いかけで「はい」を選んだ人向けの練習。GamePlayManager の Update からだけ Tick する。
// 譜面・判定・演出・効果音は本番と同じ部品をそのまま使い、文は上部に 1 行、スキップは画面下の帯の照準だけ。
// 時計は無音の 2 秒ループ(SongPlayer の DSP 時計)で、判定・受付窓・譜面の進行が同じ時計を見る。
// 終わったら(スキップしても)選曲へ進む。得点・実績は残さない。
public sealed class TutorialController : MonoBehaviour
{
    public struct TutorialStepRecord { public string Id; public double Seconds; public string Reason; }

    public TutorialOverlay Overlay { get; private set; }
    public TutorialStep Step => steps != null && stepIndex >= 0 && stepIndex < steps.Length ? steps[stepIndex] : null;
    public int StepIndex => stepIndex;
    public int StepCount => steps != null ? steps.Length : 0;
    public int Successes { get; private set; }
    public bool StepDone { get; private set; }
    public bool IsFinished { get; private set; }
    public string FinishReason { get; private set; }
    public string Hint { get; private set; }
    public string BannerTitle { get; private set; } = "";
    public string BannerSub { get; private set; } = "";
    public double ElapsedSeconds => started ? Now - startChartTime : 0;
    // 練習の 1 ステップを諦めて次へ進むまでの秒数。できない人を列で待たせない。
    public float PracticeTimeoutSeconds { get; set; } = TutorialProgram.DefaultStepTimeoutSeconds;
    public IReadOnlyList<TutorialStepRecord> Records => records;

    SongPlayer song;
    NoteSpawner spawner;
    ScoreManager score;
    SaberTracker[] sabers = System.Array.Empty<SaberTracker>();
    AudioSource source;
    AudioClip clock;
    ChartData liveChart;
    TutorialStep[] steps;
    SaberUIPointer pointer;
    readonly List<CuttableNote> watched = new List<CuttableNote>();
    readonly List<TutorialStepRecord> records = new List<TutorialStepRecord>();
    double totalOffset, startChartTime, stepStart, stepEndAt, nextPatternAt;
    int stepIndex = -1, swingFrames;
    float hintUntil;
    bool started, cleanedUp;

    // 譜面の時計(曲時計から判定のずらし分を引いたもの)。NoteData.time と同じ基準。
    double Now => song.SongTime - totalOffset;

    public void Initialize(SongPlayer player, NoteSpawner notes, ScoreManager scoring, double extraOffset,
        SaberTracker[] saberTrackers, LongNoteCutSfx longSfx, GoldNoteSfx goldSfx)
    {
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
        // 無音のループで DSP 時計だけを回す。
        source = song.GetComponent<AudioSource>();
        source.Stop(); source.playOnAwake = false; source.loop = true; source.pitch = 1f; source.spatialBlend = 0f;
        clock = AudioClip.Create("TutorialClock", 48000 * 2, 1, 48000, false);
        clock.SetData(new float[48000 * 2], 0);
        song.Clip = clock;
        song.PlayScheduled(AudioSettings.dspTime + .35);
        Overlay = TutorialOverlay.Ensure();
        Overlay.Bind(this, score);
        // スキップの的は画面下の帯だけで受け付け、通常の振りで押されないようにする(判定調整と同じ)。
        pointer = SaberUIPointer.Build(useAimReticle: true);
        pointer.BottomControlArea = Overlay.Controls;
        pointer.RemapToFullScreen = true;
        pointer.RespectRaycastBlockers = true;
        pointer.BottomControlsOnly = true;
        steps = TutorialProgram.Build(TutorialProgram.CommonFlow, true);
    }

    public void Tick(float frameSeconds)
    {
        if (steps == null) return;
        if (!IsFinished && song != null && song.IsPlaying)
        {
            double now = Now;
            if (!started) { started = true; startChartTime = now; BeginStep(0, now); }
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
                            if (Successes >= step.Goal) Complete(now, "ok");
                            else if (elapsed >= PracticeTimeoutSeconds) Complete(now, "timeout");
                            break;
                        case TutorialStepKind.Explain:
                            swingFrames = AnySaberSwinging() ? swingFrames + 1 : 0;
                            if (swingFrames >= 3 || elapsed >= step.TimeoutSeconds) Complete(now, "ok");
                            break;
                        case TutorialStepKind.Ready:
                            if (elapsed >= step.TimeoutSeconds) Complete(now, "ok");
                            break;
                    }
                }
                else if (now >= stepEndAt) Next(now);
            }
        }
        if (Hint != null && Time.unscaledTime > hintUntil) Hint = null;
        if (Overlay != null) Overlay.Tick(frameSeconds);
    }

    // 成功するまで同じ型を出し続ける。先読み分だけ譜面の末尾に足し、過去には足さない。
    void ExtendPattern(double now, TutorialStep step)
    {
        double horizon = now + spawner.approachTime + .3;
        int guard = 0;
        while (nextPatternAt <= horizon && guard++ < 8)
        {
            foreach (var p in step.Pattern) liveChart.notes.Add(p.ToNote(nextPatternAt));
            nextPatternAt += System.Math.Max(.5, step.PeriodSeconds);
        }
    }

    void BeginStep(int index, double now)
    {
        stepIndex = index; Successes = 0; StepDone = false; swingFrames = 0; Hint = null;
        stepStart = now;
        var step = Step;
        nextPatternAt = now + TutorialProgram.ReadDelaySeconds + spawner.approachTime;
        BannerTitle = step.Title; BannerSub = step.Sub;
        if (Overlay != null) Overlay.ShowStep(step, index, steps.Length);
    }

    void Complete(double now, string reason)
    {
        if (StepDone) return;
        StepDone = true;
        stepEndAt = now + (reason == "timeout" ? TutorialProgram.TimeoutEndSeconds : TutorialProgram.StepEndSeconds);
        var step = Step;
        records.Add(new TutorialStepRecord { Id = step.Id, Seconds = now - stepStart, Reason = reason });
        if (reason == "timeout") { BannerTitle = "次に進むよ"; BannerSub = "あとは曲の中で慣れよう"; }
        else if (step.Kind == TutorialStepKind.Practice) { BannerTitle = "OK!"; BannerSub = ""; }
        DropUnspawnedNotes();
        if (Overlay != null) Overlay.ShowBanner(BannerTitle, BannerSub);
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
        note.OnJudged += Judged;
        note.OnMiss += Missed;
    }

    void Judged(CuttableNote note, JudgmentTier tier, Vector3 point, Vector3 velocity)
    {
        var step = Step;
        if (step == null || StepDone || IsFinished) return;
        if (TutorialProgram.CountsAsSuccess(step, tier, note.LastCutCorrectDirection, note.RequiredCutCount, note.CutsAchieved))
        {
            Successes++;
            return;
        }
        if (note.RequiredCutCount > 1) SetHint("止まっている間に、同じ場所を 3 回切ろう");
        else if (step.NeedDirection && !note.LastCutCorrectDirection)
            SetHint("矢印の向きに振ろう(" + TutorialProgram.DirectionJapanese(note.RequiredDirection) + ")");
    }

    void Missed(CuttableNote note)
    {
        var step = Step;
        if (step == null || step.Kind != TutorialStepKind.Practice || StepDone || IsFinished) return;
        SetHint(TutorialProgram.MissHint(ColorName(note), note.RequiredDirection != CutDirection.None));
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
        if (source != null && source.clip == clock) source.clip = null;
        if (clock != null) UISkinKit.SafeDestroy(clock);
        clock = null;
    }
}
