using System;
using System.Collections.Generic;
using UnityEngine;

public enum CalibrationRunMode { Idle, Observe, Measure, Practice, Result }

// GamePlayManager からだけ Tick する。音・譜面・集計は同じ SongPlayer の DSP 時計を使う。
public sealed class CalibrationController : MonoBehaviour
{
    public CalibrationDraft Draft { get; private set; }
    public CalibrationResult Result { get; private set; }
    public CalibrationRunMode Mode { get; private set; }
    public bool IsRunning => Mode == CalibrationRunMode.Observe || Mode == CalibrationRunMode.Measure || Mode == CalibrationRunMode.Practice;
    public string Notice { get; private set; } = "本番で合っているなら、今の設定のままで大丈夫です。";
    public string LastCut { get; private set; } = "音に合わせて切り終えましょう";
    public double LastErrorMs { get; private set; }
    public bool HasLastError { get; private set; }
    public int CollectedCount => samples.Count;
    public double RunTime => song != null ? song.SongTime : 0;
    public int ProgressCount => !IsRunning ? 0 : Mathf.Clamp((int)Math.Floor(
        (RunTime - totalOffset - CalibrationProtocol.NoteTime(CalibrationProtocol.WarmupNotes)) / CalibrationProtocol.BeatSeconds) + 1,
        0, CalibrationProtocol.MeasuredNotes);
    public bool Stick1Ready => InputPoint.Instance != null && InputPoint.Instance.IsRecentlyActive(.7);
    public bool Stick2Ready => InputPoint.Instance != null && InputPoint.Instance.IsRecentlyActive2(.7);
    public bool CanMeasure => Stick1Ready && Stick2Ready;
    public float ReferenceVolume { get; private set; } = .65f;
    public CalibrationOverlay Overlay { get; private set; }

    SongPlayer song;
    NoteSpawner spawner;
    ScoreManager score;
    AudioClip clicks;
    AudioSource source;
    SaberUIPointer pointer;
    JudgmentSfx[] cutSounds;
    float[] originalVolumes;
    readonly List<CalibrationSample> samples = new List<CalibrationSample>();
    readonly List<CuttableNote> watched = new List<CuttableNote>();
    readonly HashSet<int> captured = new HashSet<int>();
    double extraOffset, totalOffset;
    int runOffset, slowFrames;
    bool interrupted, lastRunWasMeasurement, cleanedUp;

    public void Initialize(SongPlayer player, NoteSpawner notes, ScoreManager scoring, double extra)
    {
        song = player; spawner = notes; score = scoring; extraOffset = extra;
        Draft = new CalibrationDraft();
        score.songPlayer = song; score.Reset(); score.Bind(spawner);
        spawner.SetChart(new ChartData());
        spawner.OnNoteSpawned += WatchNote;
        source = song.GetComponent<AudioSource>();
        source.Stop(); source.playOnAwake = false; source.loop = false; source.pitch = 1; source.spatialBlend = 0;
        source.volume = ReferenceVolume;
        var pcm = CalibrationProtocol.ClickSamples(48000);
        clicks = AudioClip.Create("CalibrationReference100BPM", pcm.Length, 1, 48000, false); clicks.SetData(pcm, 0);
        // クリック音とカット音を取り違えないよう、この練習だけ判定効果音を止める。
        cutSounds = UnityEngine.Object.FindObjectsByType<JudgmentSfx>(FindObjectsSortMode.None);
        originalVolumes = new float[cutSounds.Length];
        for (int i = 0; i < cutSounds.Length; i++) { originalVolumes[i] = cutSounds[i].volume; cutSounds[i].volume = 0; }
        foreach (var hud in UnityEngine.Object.FindObjectsByType<ScoreHUD>(FindObjectsSortMode.None))
        {
            hud.enabled = false;
            if (hud.scoreText != null) hud.scoreText.gameObject.SetActive(false);
            if (hud.comboText != null) hud.comboText.gameObject.SetActive(false);
            if (hud.tierText != null) hud.tierText.gameObject.SetActive(false);
            if (hud.flickWarningText != null) hud.flickWarningText.gameObject.SetActive(false);
        }
        Overlay = CalibrationOverlay.Ensure(); Overlay.Bind(this);
        pointer = SaberUIPointer.Build();
        pointer.RemapToFullScreen = true;
        AudioSettings.OnAudioConfigurationChanged += AudioConfigurationChanged;
    }

    public void Begin(CalibrationRunMode mode)
    {
        if (mode != CalibrationRunMode.Observe && mode != CalibrationRunMode.Measure && mode != CalibrationRunMode.Practice) return;
        if (mode == CalibrationRunMode.Measure && !CanMeasure)
        { Notice = "実機セーバー2本の入力を確認してから測定します。接続前でも「音と表示を見る」は使えます。"; return; }
        StopPlayback(); Result = null; samples.Clear(); captured.Clear();
        HasLastError = false; LastCut = "音に合わせて切り終えましょう";
        runOffset = Draft.OffsetMs; totalOffset = extraOffset + runOffset / 1000.0;
        slowFrames = 0; interrupted = false; lastRunWasMeasurement = mode == CalibrationRunMode.Measure;
        spawner.approachTime = GameSession.NoteApproachTime;
        spawner.SetExtraOffsetSeconds(totalOffset); spawner.SetChart(CalibrationProtocol.CreateChart());
        score.Reset(); song.Clip = clicks; source.volume = ReferenceVolume;
        song.PlayScheduled(AudioSettings.dspTime + .35); Mode = mode;
        Notice = mode == CalibrationRunMode.Observe ? "切らずに確認。音と、ノーツが枠に届く瞬間を比べてください。" :
            "4拍の合図 → 4ノーツ練習 → 24ノーツ。青は左手、赤は右手で切ります。";
        if (pointer != null) pointer.gameObject.SetActive(false);
        Overlay.Refresh();
    }
    public void Tick(float frameSeconds)
    {
        if (Draft == null) return;
        if (IsRunning && song.IsPlaying)
        {
            double time = song.SongTime;
            if (Mode == CalibrationRunMode.Measure && time >= CalibrationProtocol.FirstNoteSeconds)
            {
                if (!CanMeasure) interrupted = true;
                if (frameSeconds > .1f) slowFrames++;
            }
            spawner.Tick(time);
            if (time >= CalibrationProtocol.EndSeconds + extraOffset)
            {
                if (Mode == CalibrationRunMode.Observe)
                { StopPlayback(); Mode = CalibrationRunMode.Idle; Notice = "確認が終わりました。合っていれば変更せず、次へ進めます。"; }
                else Complete();
            }
        }
        Overlay.Tick();
    }
    void WatchNote(CuttableNote note) { watched.Add(note); note.OnCut += RecordCut; }
    void RecordCut(CuttableNote note, Vector3 point, Vector3 velocity)
    {
        if (!IsRunning || Mode == CalibrationRunMode.Observe) return;
        int noteIndex = (int)Math.Round((note.HitTime - totalOffset - CalibrationProtocol.FirstNoteSeconds) / CalibrationProtocol.BeatSeconds);
        // ScoreManager は先に購読している。本番が確定した誤差を再利用し、
        // 判定後の効果音・発光処理にかかった時間を測定値に加えない。
        double error = score.LastErrorValid ? score.LastErrorMs : (song.SongTime - note.HitTime) * 1000;
        LastErrorMs = error; HasLastError = true;
        string side = note.LastCutterHand == SaberHand.Left ? "左" : note.LastCutterHand == SaberHand.Right ? "右" : "マウス等";
        LastCut = $"{side}  /  {(error < -8 ? "早い" : error > 8 ? "遅い" : "中央")}  {CalibrationDraft.FormatMs(error)}";
        int index = noteIndex - CalibrationProtocol.WarmupNotes;
        if (index < 0 || index >= CalibrationProtocol.MeasuredNotes || !captured.Add(index)) return;
        samples.Add(new CalibrationSample(index, error, note.LastCutterHand));
    }
    void Complete()
    {
        Result = CalibrationResult.Analyze(samples, runOffset, interrupted, slowFrames);
        // 試し切りは再調整の起点にはしない。測定と検証を混同させない。
        if (!lastRunWasMeasurement) Result.CanRecommend = false;
        StopPlayback(); Mode = CalibrationRunMode.Result; Notice = lastRunWasMeasurement ? "測定結果 / 保存値は変わっていません" : "試し切りの結果 / まだ保存されていません";
    }
    void StopPlayback()
    {
        if (song != null) song.Stop();
        foreach (var n in watched) if (n != null) n.OnCut -= RecordCut;
        watched.Clear();
        if (spawner != null) spawner.SetChart(new ChartData());
        if (pointer != null) pointer.gameObject.SetActive(true);
    }
    public void Stop()
    {
        StopPlayback(); Mode = CalibrationRunMode.Idle; Result = null; HasLastError = false;
        Notice = "中断しました。保存値は変わっていません。";
    }
    public void ChangeOffset(int delta)
    {
        if (IsRunning) return;
        Draft.SetOffset(Draft.OffsetMs + delta); Result = null; HasLastError = false; Mode = CalibrationRunMode.Idle;
        Notice = "仮の設定です。「試し切り」で確認してから保存できます。"; Overlay.Refresh();
    }
    public void SelectProfile(int profile)
    {
        if (IsRunning) return;
        Draft.SelectProfile(profile); Result = null; HasLastError = false; Mode = CalibrationRunMode.Idle;
        Notice = "設定の保存先を選びました。Windowsの音の出力先は自動では切り替わりません。"; Overlay.Refresh();
    }
    public void RestoreSaved()
    {
        if (IsRunning) return;
        Draft.RestoreSaved(); Result = null; HasLastError = false; Mode = CalibrationRunMode.Idle;
        Notice = "この出力先の保存値に戻しました。"; Overlay.Refresh();
    }
    public void TryRecommendation()
    {
        if (Result == null || !Result.CanRecommend || IsRunning) return;
        Draft.SetOffset(Result.ProposedOffsetMs); Begin(CalibrationRunMode.Practice);
    }
    public void ChangeVolume(float delta)
    {
        if (IsRunning) return;
        ReferenceVolume = Mathf.Clamp(ReferenceVolume + delta, .1f, 1f); if (source != null) source.volume = ReferenceVolume;
        Overlay.Refresh();
    }
    public void SaveAndExit()
    {
        if (IsRunning) return;
        Draft.Commit(); StopPlayback(); GamePlayManager.ExitCalibration();
    }
    public void DiscardAndExit() { StopPlayback(); GamePlayManager.ExitCalibration(); }
    void OnApplicationFocus(bool focus)
    {
        if (!focus && IsRunning) InterruptRun("画面のフォーカスが外れたため中断しました。環境を固定して再開してください。");
    }
    void AudioConfigurationChanged(bool deviceChanged)
    {
        if (IsRunning) InterruptRun("音声機器の設定が変わったため中断しました。出力先を確認して測り直してください。");
        else Notice = "音声機器の設定が変わりました。出力先とプロフィールを確認してください。";
    }
    void InterruptRun(string message) { interrupted = true; StopPlayback(); Mode = CalibrationRunMode.Idle; Result = null; Notice = message; }
    void OnDestroy()
    {
        if (cleanedUp) return; cleanedUp = true;
        AudioSettings.OnAudioConfigurationChanged -= AudioConfigurationChanged;
        if (spawner != null) spawner.OnNoteSpawned -= WatchNote;
        StopPlayback();
        if (source != null && source.clip == clicks) source.clip = null;
        if (clicks != null) UISkinKit.SafeDestroy(clicks);
        if (cutSounds != null) for (int i = 0; i < cutSounds.Length; i++) if (cutSounds[i] != null) cutSounds[i].volume = originalVolumes[i];
    }
}
