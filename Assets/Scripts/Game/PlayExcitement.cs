using System;
using System.Collections.Generic;
using UnityEngine;

// 育1〜3・継1と、後続の演出が使う光・背景の押し込みの入口。
// 独自のUpdateは持たず、GamePlayManagerから一度だけTickする。
public sealed partial class PlayExcitement : MonoBehaviour
{
    public static PlayExcitement Current { get; private set; }
    public static int VisualLevel => Current != null && Current.started && !Current.ended ? Current.Meter.Level : 0;
    public VoltageMeter Meter { get; private set; }

    static LargeFlashBudget flashBudget = new LargeFlashBudget();
    readonly VoltageSoundLimiter soundLimiter = new VoltageSoundLimiter();
    readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>();

    GamePlayManager owner;
    SongPlayer song;
    ScoreManager score;
    ChorusDrop drop;
    StageFeverShow fever;
    VoltageScoreBinding binding;
    VoltageMusicKey key;
    VoltageRandom random;
    AudioSource source;
    string runId;
    bool started, ended, wasAdvancing;
    double lastSongTime = double.NaN;
    double visualTime;

    bool Advancing => started && !ended && isActiveAndEnabled && owner != null && owner.isActiveAndEnabled
        && song != null && song.IsPlaying && !song.IsPaused && !song.IsWaitingForAudio
        && Time.timeScale > 0 && score != null && !score.IsFinalized;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Current = null;
        flashBudget = new LargeFlashBudget();
    }

    public static PlayExcitement Create(GamePlayManager owner, StagePerformanceTimeline timeline)
    {
        var existing = owner.GetComponent<PlayExcitement>();
        if (existing != null) return existing;
        var show = owner.gameObject.AddComponent<PlayExcitement>();
        show.owner = owner; show.song = owner.songPlayer; show.score = owner.scoreManager;
        show.runId = GameSession.AchievementRunId;
        show.key = VoltageMusicKey.Parse(timeline != null ? timeline.key : null);
        show.random = new VoltageRandom(GameSession.SelectedSongId, show.runId);
        show.Meter = new VoltageMeter(VisitorPlays.StartingVoltageLevel);
        show.Meter.Changed += show.Changed;
        show.PrepareSounds();
        DisplaySettings.OnReducedEffectsChanged += show.ReducedChanged;
        return show;
    }

    public static void TickFor(GamePlayManager owner, bool active)
    {
        var show = owner.GetComponent<PlayExcitement>();
        if (show != null && show.isActiveAndEnabled) show.Tick(active);
    }

    void StartPresentation()
    {
        started = true;
        Current = this;
        drop = owner.GetComponentInChildren<ChorusDrop>();
        var stage = UnityEngine.Object.FindFirstObjectByType<FloorRenderer>();
        fever = stage != null ? stage.GetComponentInChildren<StageFeverShow>() : null;
        BuildVisuals(stage);
        binding = new VoltageScoreBinding(score, Meter, () => Advancing);
        VisitorPlays.RecordVoltagePeak(runId, Meter.MaxLevelReached);
        if (fever != null) fever.VoltageLevel = Meter.Level;
    }

    void Tick(bool active)
    {
        if (!active) { if (started) StopPresentation(); return; }
        if (ended) return;
        if (!started) StartPresentation();
        if (score == null || score.IsFinalized) { StopPresentation(); return; }

        double time = song != null ? song.SongTime : 0;
        bool advancing = Advancing;
        float dt = 0;
        if (advancing && wasAdvancing && Finite(lastSongTime))
        {
            double elapsed = time - lastSongTime;
            if (elapsed < 0 || elapsed > .5)
            {
                ClearTransients();
                if (source != null) source.Stop();
            }
            else dt = (float)elapsed;
        }
        if (!advancing && wasAdvancing && source != null) source.Stop();
        lastSongTime = time;
        wasAdvancing = advancing;
        visualTime += dt;
        if (fever != null) fever.VoltageLevel = Meter.Level;
        DrawVisuals(dt, time, advancing);
    }

    void Changed(VoltageMeter.Change change)
    {
        VisitorPlays.RecordVoltagePeak(runId, Meter.MaxLevelReached);
        if (fever != null) fever.VoltageLevel = Meter.Level;

        if (change.StepNumber > 0 && soundLimiter.TryPlay(Time.realtimeSinceStartupAsDouble, change.StepNumber == 8))
        {
            if (change.StepNumber == 8) Play(FlourishSfx.VoltageChordName(key));
            else Play(FlourishSfx.VoltageBellName(key.StepMidi(change.StepNumber, change.Level),
                FlourishSfx.VoltageVoice(change.Level)));
        }

        if (change.Raised)
        {
            Color color = LevelColor(change.Level, visualTime);
            // 受付が残り1回なら、V4の白い光を優先する。
            if (change.Level == 4)
            {
                TryLargeFlash(LargeFlashKind.Full, LargeFlashTint.White, .4f, .15f);
                Play("CrowdShort");
            }
            LargeFlashTint tint = change.Level == 1 ? LargeFlashTint.Cyan
                : change.Level == 4 ? LargeFlashTint.White : LargeFlashTint.Gold;
            TryLargeFlash(LargeFlashKind.Edge, tint, .3f, .3f);
            PushBackground(.15f, .08f);
            if (!DisplaySettings.ReducedEffects) AddRing(color);
        }
        else if (change.Lowered)
        {
            Play("VoltageBreak");
            if (!DisplaySettings.ReducedEffects)
                AddShards(change.PreviousSteps, change.PreviousLevel);
        }
        DrawGauge();
    }

    // 後続の演出もこの入口を使い、別の回数制限や全画面Canvasを作らない。
    public bool TryLargeFlash(LargeFlashKind kind, LargeFlashTint tint, float alpha, float seconds)
    {
        if (!Advancing || DisplaySettings.ReducedEffects || !Finite(alpha) || !Finite(seconds)
            || alpha <= 0 || seconds <= 0 || flashes.Count >= 32
            || largeLight == null || !largeLight.Available || !TryGateRect(out _)) return false;
        // Y(闇と灯り)の暗い区間では暗さの分だけ弱める。暗幕の穴(剣のまわり)だけが光って見えるのを防ぐ。
        float dark = owner.DarkMode != null ? owner.DarkMode.Level : 0f;
        alpha *= 1f - DarkRules.Level(dark);
        if (alpha < .02f) return false;
        if (!flashBudget.TryTake(Time.realtimeSinceStartupAsDouble)) return false;
        flashes.Add(new ScreenPulse { kind = kind, color = FlashColor(tint), alpha = Mathf.Clamp01(alpha), seconds = seconds });
        DrawScreenFlashes();
        return true;
    }

    // 強さは奥行き方向のワールド単位。ゲート・ノーツ・剣には触れない。
    public void PushBackground(float seconds, float strength)
    {
        if (Advancing && !DisplaySettings.ReducedEffects && drop != null)
            drop.PushBackground(seconds, strength);
    }

    void PrepareSounds()
    {
        if (!Application.isPlaying) return;
        source = gameObject.AddComponent<AudioSource>();
        source.playOnAwake = false; source.spatialBlend = 0; source.volume = 1;
        for (int level = 0; level <= 4; level++)
            for (int step = 1; step < 8; step++)
            {
                int midi = key.StepMidi(step, level), voice = FlourishSfx.VoltageVoice(level);
                AddClip(FlourishSfx.VoltageBellName(midi, voice), () => FlourishSfx.VoltageBell(midi, voice));
            }
        AddClip(FlourishSfx.VoltageChordName(key), () => FlourishSfx.VoltageChord(key));
        AddClip("VoltageBreak", FlourishSfx.VoltageBreak);
        AddClip("CrowdShort", FlourishSfx.CrowdShort);
    }

    void AddClip(string name, Func<float[]> build)
    {
        if (!clips.ContainsKey(name))
            clips[name] = ProceduralSfx.Clip(name, FlourishSfx.Cached(name, build));
    }

    void Play(string name)
    {
        if (!Advancing || source == null || !clips.TryGetValue(name, out var clip)) return;
        ProceduralSfx.Revive(ref clip);
        clips[name] = clip;
        if (clip != null) source.PlayOneShot(clip, 1);
    }

    void ReducedChanged(bool low)
    {
        if (!started || ended) return;
        if (low)
        {
            ClearTransients();
            if (drop != null) drop.ClearBackgroundPushes();
        }
        DrawVisuals(0, song != null ? song.SongTime : 0, Advancing);
    }

    void StopPresentation()
    {
        if (ended) return;
        ended = true;
        binding?.Dispose(); binding = null;
        if (source != null) source.Stop();
        if (fever != null) fever.VoltageLevel = 0;
        if (drop != null) drop.ClearBackgroundPushes();
        ClearTransients();
        HideVisuals();
        background?.Apply(0);
        if (Current == this) Current = null;
    }

    void OnDisable() { if (started) StopPresentation(); }

    void OnDestroy()
    {
        StopPresentation();
        DisplaySettings.OnReducedEffectsChanged -= ReducedChanged;
        if (Meter != null) Meter.Changed -= Changed;
        ReleaseVisuals();
        foreach (var clip in clips.Values) UISkinKit.SafeDestroy(clip);
        clips.Clear();
        UISkinKit.SafeDestroy(source);
    }

    static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
