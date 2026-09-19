using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

// 実Gameシーンで、高速の曲時計走査と実時間の再生検証を別のテストにする。
public class FavoriteEffectsPlayTests
{
    static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    readonly List<CuttableNote> notes = new List<CuttableNote>();
    GamePlayManager manager;
    FloorRenderer floor;
    StageReactiveEffects effects;
    ScenicStageWorld scenic;
    FoundryStageMotion foundry;
    StagePerformanceTimeline timeline;
    string originalSong, originalDifficulty;
    bool originalCalibration;
    int judged;

    [SetUp]
    public void RememberSession()
    {
        originalSong = GameSession.SelectedSongId;
        originalDifficulty = GameSession.SelectedDifficulty;
        originalCalibration = GameSession.IsCalibrationMode;
    }

    [TearDown]
    public void RestoreSession()
    {
        if (manager != null && manager.noteSpawner != null) manager.noteSpawner.OnNoteSpawned -= Collect;
        GameSession.SelectedSongId = originalSong;
        GameSession.SelectedDifficulty = originalDifficulty;
        GameSession.IsCalibrationMode = originalCalibration;
    }

    [UnityTest, Timeout(360000)]
    public IEnumerator YurikagoHardAll11Backgrounds_AcceleratedClockReloadAndResourceRelease()
    {
        for (int theme = 0; theme < StageThemeCatalog.Count; theme++)
        {
            yield return LoadGame((StageTheme)theme);
            var chart = ChartLoader.LoadFromStreamingAssets("揺籠", "hard");
            Assert.AreEqual(404, chart.notes.Count, "調査対象譜面が変わっています。");
            SetChart(chart);
            yield return null;
            double duration = manager.songPlayer.Duration;
            Assert.Greater(duration, 180);
            // 30Hz相当の全曲時刻を走査。20tickごとに実フレームを渡す高速検証。
            // 実音声の連続再生や、実時間の描画負荷を再現したとは扱わない。
            int frames = (int)Math.Ceiling((duration + 2) * 30);
            for (int frame = 0; frame <= frames; frame++)
            {
                Advance(frame / 30.0, true);
                if (frame % 20 == 0) yield return null;
            }
            Assert.AreEqual(404, manager.noteSpawner.NextIndex);
            Assert.AreEqual(404, judged, "高速走査中にノーツを見逃しました: " + (StageTheme)theme);
            Assert.AreEqual(0, effects.ActiveWaveCount);
            var response = floor.GetComponentInChildren<StageThemeResponse>();
            if (response != null) Assert.AreEqual(0, response.ActiveResponseCount);
            double stopped = effects.LastTickSeconds;
            yield return null; yield return null;
            Assert.AreEqual(stopped, effects.LastTickSeconds, "親がTickしない間に演出が進んでいます。");
            yield return ReleaseFloorAndAssertResources();
        }
    }

    [UnityTest, Timeout(120000)]
    public IEnumerator ThemeResponsesUseFinalPerfect_RespectPlacementAndClearOnReset()
    {
        foreach (var theme in new[] { StageTheme.AmberFoundry, StageTheme.MoonlitGarden })
        {
            yield return LoadGame(theme);
            var response = floor.GetComponentInChildren<StageThemeResponse>();
            Assert.NotNull(response, "StageReactiveEffectsから背景専用応答が接続されていません。");
            for (int lane = 0; lane < 4; lane++)
            {
                SetChart(Single(lane)); yield return null;
                manager.noteSpawner.Tick(1);
                Draw(1); SetClock(1);
                // 青を右側へ置いても、担当ハンドではなく実位置の床列へ出る。
                Cut(notes[0], 1, Vector3.right * 6, CutDirection.None);
                Draw(1.25);
                Assert.AreEqual(JudgmentTier.Perfect, manager.scoreManager.LastTier);
                Assert.AreEqual(1 << lane, response.ActiveLaneMask);
                Assert.AreEqual(1 << lane, effects.ActiveFloorLaneMask);
                SetChart(new ChartData());
                Assert.AreEqual(0, response.ActiveResponseCount);
            }

            SetChart(Single(1)); yield return null;
            manager.noteSpawner.Tick(1.10); Draw(1.10);
            Cut(notes[0], 1.10, Vector3.right * 6, CutDirection.None);
            Assert.AreNotEqual(JudgmentTier.Perfect, manager.scoreManager.LastTier);
            Assert.AreEqual(0, response.ActiveResponseCount, "Great以下で素材が応答しました。");

            var direction = Single(1); direction.notes[0].type = "direction"; direction.notes[0].direction = "up";
            SetChart(direction); yield return null;
            manager.noteSpawner.Tick(1); Draw(1);
            Cut(notes[0], 1, Vector3.right * 6, CutDirection.Right);
            Assert.AreEqual(JudgmentTier.Great, manager.scoreManager.LastTier, "横振りによる方向降格が再現できません。");
            Assert.AreEqual(0, response.ActiveResponseCount, "方向降格前のPerfectで素材が応答しました。");

            var longChart = Single(2); longChart.notes[0].type = "long"; longChart.notes[0].count = 4;
            SetChart(longChart); yield return null;
            manager.noteSpawner.Tick(1); Draw(1);
            for (int i = 0; i < 3; i++) Cut(notes[0], 1, Vector3.right * 6, CutDirection.None);
            Assert.AreEqual(0, response.ActiveResponseCount, "途中ロングが素材を祝福しました。");
            Cut(notes[0], 1, Vector3.right * 6, CutDirection.None);
            Assert.AreEqual(1 << 2, response.ActiveLaneMask);
            Draw(.5);
            Assert.AreEqual(0, response.ActiveResponseCount, "巻き戻しで素材反応が残りました。");
            yield return ReleaseFloorAndAssertResources();
        }
    }

    [UnityTest, Timeout(240000)]
    public IEnumerator YurikagoHardAstralOrbit_RealTimeAudioWithScriptedPerfectInput()
    {
        yield return LoadGame(StageTheme.AstralOrbit);
        SetChart(ChartLoader.LoadFromStreamingAssets("揺籠", "hard"));
        // GamePlayManagerは停止しているので結果保存/結果シーン遷移は実行されない。
        // 音声とDSP時計を実時間で進め、人工の正方向入力を毎フレーム与える。
        // 物理セーバー入力の再現ではなく、このテスト内でDSP基準時刻を飛ばさない。
        double duration = manager.songPlayer.Duration;
        Assert.That(duration, Is.InRange(185, 186));
        manager.songPlayer.Play();
        float started = Time.realtimeSinceStartup;
        while (!manager.songPlayer.IsPlaying) yield return null;
        double previousTime = manager.songPlayer.SongTime, maxFrameGap = 0;
        var missedDetails = new List<string>();
        var missedNotes = new HashSet<CuttableNote>();
        while (manager.songPlayer.SongTime < duration + 2)
        {
            double now = manager.songPlayer.SongTime;
            double gap = now - previousTime;
            maxFrameGap = Math.Max(maxFrameGap, gap); previousTime = now;
            Advance(now, false);
            foreach (var note in notes)
                if (note != null && note.IsMissed && missedNotes.Add(note))
                    missedDetails.Add($"note={note.HitTime:F3}s observed={now:F3}s frameGap={gap:F3}s");
            yield return null;
        }
        Debug.Log($"Real-time cut test: {judged}/404 cuts, maxFrameGap={maxFrameGap:F3}s; " + string.Join("; ", missedDetails));
        Assert.GreaterOrEqual(Time.realtimeSinceStartup - started, duration - .5,
            "実時間テストの時計が短縮されています。");
        Assert.AreEqual(404, manager.noteSpawner.NextIndex);
        Assert.AreEqual(404, judged, $"実時間の人工操作で全ノーツを処理できませんでした。maxFrameGap={maxFrameGap:F3}s; " + string.Join("; ", missedDetails));
        manager.songPlayer.Stop();
        yield return ReleaseFloorAndAssertResources();
    }

    IEnumerator LoadGame(StageTheme theme)
    {
        if (manager != null && manager.noteSpawner != null) manager.noteSpawner.OnNoteSpawned -= Collect;
        GameSession.SelectedSongId = "揺籠"; GameSession.SelectedDifficulty = "hard"; GameSession.IsCalibrationMode = false;
        yield return SceneManager.LoadSceneAsync("Game", LoadSceneMode.Single);
        manager = Object.FindFirstObjectByType<GamePlayManager>(); Assert.NotNull(manager);
        float deadline = Time.realtimeSinceStartup + 30;
        while (!manager.songPlayer.IsScheduled && Time.realtimeSinceStartup < deadline) yield return null;
        Assert.IsTrue(manager.songPlayer.IsScheduled, "音源ロードが完了しません。");
        Assert.AreEqual(404, manager.noteSpawner.TotalNoteCount);
        manager.enabled = false;
        foreach (var source in Object.FindObjectsByType<AudioSource>(FindObjectsSortMode.None)) source.Stop();
        foreach (var judge in Object.FindObjectsByType<SaberCutJudge>(FindObjectsSortMode.None)) judge.autonomous = false;
        foreach (var input in Object.FindObjectsByType<SaberInputBridge>(FindObjectsSortMode.None)) input.enabled = false;
        manager.noteSpawner.OnNoteSpawned += Collect;
        yield return ReleaseFloorAndAssertResources();
        floor = new GameObject("FavoriteEffectsPlayTestFloor").AddComponent<FloorRenderer>();
        floor.randomizeOnPlay = false; floor.theme = theme; floor.Build(theme);
        timeline = StagePerformanceTimeline.Load("揺籠");
        scenic = floor.GetComponentInChildren<ScenicStageWorld>();
        foundry = FoundryStageMotion.Ensure(floor);
        effects = StageReactiveEffects.Create(floor, manager.noteSpawner, timeline);
        Assert.AreEqual(theme, floor.ActiveTheme);
        yield return null;
    }

    static ChartData Single(int lane) => new ChartData { bpm = 120, notes = new List<NoteData> {
        new NoteData { time = 1000, x = lane == 0 ? -2.5f : lane == 1 ? -1 : lane == 2 ? 1 : 2.5f, color = "blue" } } };

    void Collect(CuttableNote note) => notes.Add(note);
    void SetChart(ChartData chart)
    {
        notes.Clear(); judged = 0;
        manager.noteSpawner.SetExtraOffsetSeconds(0);
        manager.noteSpawner.SetChart(chart);
        floor.SetRhythm(chart); Draw(0);
    }
    void SetClock(double time)
    {
        typeof(SongPlayer).GetField("clockSynchronized", Private).SetValue(manager.songPlayer, true);
        typeof(SongPlayer).GetField("startDspTime", Private).SetValue(manager.songPlayer, AudioSettings.dspTime - time);
    }
    void Cut(CuttableNote note, double time, Vector3 velocity, CutDirection direction, bool setClock = true)
    {
        if (setClock) SetClock(time);
        manager.noteSpawner.RefreshJudgmentWindows(time);
        note.Cut(note.transform.position, velocity, direction, note.RequiredHand == SaberHand.Any ? SaberHand.Left : note.RequiredHand);
    }
    void Advance(double time, bool accelerated)
    {
        manager.noteSpawner.Tick(time);
        foreach (var note in notes)
        {
            if (note == null || note.IsCut || note.IsMissed || note.IsFinalized || note.HitTime > time) continue;
            double hit = accelerated ? note.HitTime : time;
            Draw(hit);
            int count = note.RemainingCuts;
            for (int i = 0; i < count; i++)
                Cut(note, hit, Vector3.right * 6, note.RequiredDirection, accelerated);
            if (note.IsCut)
            {
                judged++;
                Assert.AreEqual(JudgmentTier.Perfect, manager.scoreManager.LastTier,
                    "人工の正方向操作がPerfectに収まりません: t=" + time + ", hit=" + note.HitTime);
            }
        }
        Draw(time);
    }
    void Draw(double time)
    {
        float chorus = timeline.Evaluate(time);
        floor.Tick(time, chorus);
        scenic?.Tick(time, chorus, timeline.EvaluateEclipse(time));
        foundry?.Tick(time, chorus);
        effects.Tick(time);
    }
    IEnumerator ReleaseFloorAndAssertResources()
    {
        manager.noteSpawner.SetChart(new ChartData()); notes.Clear();
        var current = Object.FindFirstObjectByType<FloorRenderer>();
        if (current == null) yield break;
        var resources = new HashSet<Object>();
        foreach (var filter in current.GetComponentsInChildren<MeshFilter>(true))
            if (filter.sharedMesh != null) resources.Add(filter.sharedMesh);
        foreach (var renderer in current.GetComponentsInChildren<Renderer>(true))
            foreach (var material in renderer.sharedMaterials) if (material != null) resources.Add(material);
        Object.Destroy(current.gameObject);
        floor = null; scenic = null; foundry = null; effects = null;
        yield return null; yield return null; yield return null;
        foreach (var resource in resources)
            Assert.IsTrue(resource == null, "背景所有Mesh/Materialが破棄されていません: " + (resource == null ? "" : resource.name));
    }
}
