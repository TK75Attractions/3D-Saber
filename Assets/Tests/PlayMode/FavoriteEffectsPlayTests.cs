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
    bool overrideCurtainEffectsSetting;
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
        if (overrideCurtainEffectsSetting) DisplaySettings.ResetReducedEffectsCacheForTest();
        overrideCurtainEffectsSetting = false;
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
        foreach (var theme in new[] { StageTheme.AmberFoundry, StageTheme.MoonlitGarden, StageTheme.AzurePrism,
            StageTheme.CrystalGrotto, StageTheme.ObsidianRelay, StageTheme.AbyssalRuins })
        {
            yield return LoadGame(theme);
            var response = floor.GetComponentInChildren<StageThemeResponse>();
            Assert.NotNull(response, "StageReactiveEffectsから背景専用応答が接続されていません。");
            for (int lane = 0; lane < 4; lane++)
            {
                SetChart(Single(lane)); yield return null;
                manager.noteSpawner.Tick(1);
                Draw(1); SetClock(1);
                var responseBody = response.transform.Find("Surface").GetComponent<MeshFilter>().sharedMesh;
                Vector3[] bodyBefore = responseBody.vertices;
                // 青を右側へ置いても、担当ハンドではなく実位置の床列へ出る。
                Cut(notes[0], 1, Vector3.right * 6, CutDirection.None);
                Draw(1.25);
                Assert.AreEqual(JudgmentTier.Perfect, manager.scoreManager.LastTier);
                Assert.AreEqual(1 << lane, response.ActiveLaneMask);
                Assert.AreEqual(1 << lane, effects.ActiveFloorLaneMask);
                if (theme == StageTheme.ObsidianRelay || theme == StageTheme.AbyssalRuins)
                {
                    Assert.IsTrue(response.ReplacesSideResponse);
                    var floorLight = effects.GetComponent<MeshFilter>().sharedMesh;
                    Assert.Greater(floorLight.vertexCount, 0, "素材動作中も共通床を描く");
                    foreach (var point in floorLight.vertices)
                        Assert.Less(point.y, floor.floorY + .7f, "同じ成功へ旧側面波を重ねない");
                }
                if (theme == StageTheme.AbyssalRuins)
                {
                    CollectionAssert.AreNotEqual(bodyBefore, responseBody.vertices, "実Perfectで珊瑚の形が開く");
                    Assert.AreEqual(0, response.transform.Find("Details").GetComponent<MeshFilter>().sharedMesh.vertexCount,
                        "珊瑚の成功へ祝福光を足さない");
                }
                SetChart(new ChartData());
                Assert.AreEqual(0, response.ActiveResponseCount);
                if (theme == StageTheme.AbyssalRuins) CollectionAssert.AreEqual(bodyBefore, responseBody.vertices,
                    "譜面再ロードで開いた珊瑚を静止形へ戻す");
            }

            SetChart(Single(1)); yield return null;
            manager.noteSpawner.Tick(1); Draw(1);
            notes[0].MarkMiss(); Draw(1.25);
            Assert.AreEqual(0, response.ActiveResponseCount, "Missで素材が応答しました。");
            Assert.AreEqual(0, effects.ActiveFloorLaneMask, "Missで成功床を出しました。");

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

            // 同時刻の4列でも、方向降格後にPerfectだった実列だけを返す。
            // Perfectを青の左列と赤の右内列へ置き、親のPair統合も通す。
            var simultaneous = new ChartData { bpm = 120, notes = new List<NoteData>() };
            for (int lane = 0; lane < 4; lane++)
            {
                var data = Single(lane).notes[0];
                data.type = "direction"; data.direction = "up";
                data.color = lane < 2 ? "blue" : "red";
                simultaneous.notes.Add(data);
            }
            SetChart(simultaneous); yield return null;
            manager.noteSpawner.Tick(1); Draw(1);
            Assert.AreEqual(4, notes.Count, "同時刻4列のノーツを生成できません。");
            int perfectMask = 0;
            for (int lane = 0; lane < 4; lane++)
            {
                var current = notes.Find(note => note != null && note.isActiveAndEnabled &&
                    !note.IsFinalized && StageReactiveEffects.FloorLaneForX(note.transform.position.x) == lane);
                Assert.NotNull(current, "対象列のノーツがありません: " + lane);
                bool succeeds = lane == 0 || lane == 2;
                Cut(current, 1, (succeeds ? Vector3.up : Vector3.right) * 6,
                    succeeds ? CutDirection.Up : CutDirection.Right);
                Assert.AreEqual(succeeds ? JudgmentTier.Perfect : JudgmentTier.Great,
                    manager.scoreManager.LastTier, "混在判定が再現できません: " + lane);
                if (succeeds) perfectMask |= 1 << lane;
                Assert.AreEqual(perfectMask, response.ActiveLaneMask,
                    "同時判定でGreat列の素材が応答したか、Perfect列が抜けました。");
                Assert.AreEqual(perfectMask, effects.ActiveFloorLaneMask,
                    "同時判定でGreat列の床が応答したか、Perfect列が抜けました。");
            }
            Draw(1.25);
            Assert.AreEqual(5, response.ActiveLaneMask);
            Assert.AreEqual(5, effects.ActiveFloorLaneMask);
            Assert.AreEqual(2, response.ActiveResponseCount);

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
    { return RealTimeAudioWithScriptedPerfectInput(StageTheme.AstralOrbit); }

    [UnityTest, Timeout(240000)]
    public IEnumerator YurikagoHardAzurePrism_RealTimeAudioWithScriptedPerfectInput()
    { return RealTimeAudioWithScriptedPerfectInput(StageTheme.AzurePrism); }

    [UnityTest, Timeout(240000)]
    public IEnumerator YurikagoHardCrystalGrotto_RealTimeAudioWithScriptedPerfectInput()
    { return RealTimeAudioWithScriptedPerfectInput(StageTheme.CrystalGrotto); }

    [UnityTest, Timeout(240000)]
    public IEnumerator YurikagoHardPulseArray_RealTimeAudioWithScriptedPerfectInput()
    { return RealTimeAudioWithScriptedPerfectInput(StageTheme.PulseArray); }

    [UnityTest, Timeout(240000)]
    public IEnumerator YurikagoHardObsidianRelay_RealTimeAudioWithScriptedPerfectInput()
    { return RealTimeAudioWithScriptedPerfectInput(StageTheme.ObsidianRelay); }

    [UnityTest, Timeout(240000)]
    public IEnumerator YurikagoHardAbyssalRuins_RealTimeAudioWithScriptedPerfectInput()
    { return RealTimeAudioWithScriptedPerfectInput(StageTheme.AbyssalRuins); }

    [UnityTest, Timeout(240000)]
    public IEnumerator YurikagoHardVioletVault_RealTimeAudioWithScriptedPerfectInput()
    { return RealTimeAudioWithScriptedPerfectInput(StageTheme.VioletVault); }

    [UnityTest, Timeout(120000)]
    public IEnumerator MeteorsUseFinalPerfectRespectFourLanesAndClearOnReload()
    {
        overrideCurtainEffectsSetting = true; DisplaySettings.SetReducedEffectsForTest(false);
        yield return LoadGame(StageTheme.AstralOrbit);
        Assert.IsTrue(scenic.MeteorResponsesReady);
        var original = new Vector3[4][];
        for (int lane = 0; lane < 4; lane++) original[lane] = MeteorMeshForLane(scenic, lane).vertices;
        for (int lane = 0; lane < 4; lane++)
        {
            SetChart(Single(lane)); yield return null;
            manager.noteSpawner.Tick(1); Draw(1);
            var current = notes.Find(n => n != null && n.isActiveAndEnabled && !n.IsFinalized &&
                StageReactiveEffects.FloorLaneForX(n.transform.position.x) == lane);
            Assert.NotNull(current);
            Cut(current, 1, Vector3.right * 6, CutDirection.None); Draw(1.30);
            Assert.AreEqual(JudgmentTier.Perfect, manager.scoreManager.LastTier);
            Assert.AreEqual(1 << lane, scenic.MeteorLaneMask); Assert.AreEqual(1 << lane, effects.ActiveFloorLaneMask);
            CollectionAssert.AreNotEqual(original[lane], MeteorMeshForLane(scenic, lane).vertices);
            for (int other = 0; other < 4; other++)
                if (other != lane) CollectionAssert.AreEqual(original[other], MeteorMeshForLane(scenic, other).vertices);
            var light = effects.GetComponent<MeshFilter>().sharedMesh;
            Assert.Greater(light.vertexCount, 0, "岩が分かれる時にも共通床を描く");
            foreach (var point in light.vertices) Assert.Less(point.y, floor.floorY + .7f, "旧側面成功光を重ねない");
        }

        var simultaneous = new ChartData { bpm = 120, notes = new List<NoteData>() };
        for (int lane = 0; lane < 4; lane++)
        {
            var data = Single(lane).notes[0]; data.type = "direction"; data.direction = "up";
            data.color = lane < 2 ? "blue" : "red"; simultaneous.notes.Add(data);
        }
        SetChart(simultaneous); yield return null; manager.noteSpawner.Tick(1); Draw(1);
        int perfectMask = 0;
        for (int lane = 0; lane < 4; lane++)
        {
            var current = notes.Find(n => n != null && n.isActiveAndEnabled && !n.IsFinalized &&
                StageReactiveEffects.FloorLaneForX(n.transform.position.x) == lane);
            Assert.NotNull(current);
            bool perfect = lane == 0 || lane == 2;
            Cut(current, 1, (perfect ? Vector3.up : Vector3.right) * 6,
                perfect ? CutDirection.Up : CutDirection.Right);
            Assert.AreEqual(perfect ? JudgmentTier.Perfect : JudgmentTier.Great, manager.scoreManager.LastTier);
            if (perfect) perfectMask |= 1 << lane;
            Assert.AreEqual(perfectMask, scenic.MeteorLaneMask, "最終Greatの実列へ岩の反応を出さない");
            Assert.AreEqual(perfectMask, effects.ActiveFloorLaneMask);
        }
        Draw(1.30); Assert.AreEqual(2, scenic.ActiveMeteorResponseCount);
        SetChart(Single(1)); yield return null; manager.noteSpawner.Tick(1); Draw(1);
        notes[0].MarkMiss(); Draw(1.30);
        Assert.AreEqual(0, scenic.MeteorLaneMask); Assert.AreEqual(0, effects.ActiveFloorLaneMask);

        var longChart = Single(2); longChart.notes[0].type = "long"; longChart.notes[0].count = 4;
        SetChart(longChart); yield return null; manager.noteSpawner.Tick(1); Draw(1);
        for (int i = 0; i < 3; i++) Cut(notes[0], 1, Vector3.right * 6, CutDirection.None);
        Assert.AreEqual(0, scenic.MeteorLaneMask, "ロング途中を素材の成功にしない");
        Assert.AreEqual(0, effects.ActiveFloorLaneMask);
        Cut(notes[0], 1, Vector3.right * 6, CutDirection.None); Draw(1.30);
        Assert.AreEqual(4, scenic.MeteorLaneMask); Assert.AreEqual(4, effects.ActiveFloorLaneMask);
        SetChart(new ChartData());
        Assert.AreEqual(0, scenic.ActiveMeteorResponseCount); Assert.AreEqual(0, scenic.MeteorLaneMask);
        for (int lane = 0; lane < 4; lane++) CollectionAssert.AreEqual(original[lane], MeteorMeshForLane(scenic, lane).vertices);
        yield return ReleaseFloorAndAssertResources();
    }

    [UnityTest, Timeout(120000)]
    public IEnumerator MeteorActualGameSceneReceivesPerfectAndStopsWithTheSong()
    {
        overrideCurtainEffectsSetting = true; DisplaySettings.SetReducedEffectsForTest(false);
        GameSession.SelectedSongId = "揺籠"; GameSession.SelectedDifficulty = "hard"; GameSession.IsCalibrationMode = false;
        SceneManager.sceneLoaded += CreateMeteorFloorBeforeManagerStart;
        try { yield return SceneManager.LoadSceneAsync("Game", LoadSceneMode.Single); }
        finally { SceneManager.sceneLoaded -= CreateMeteorFloorBeforeManagerStart; }
        manager = Object.FindFirstObjectByType<GamePlayManager>(); Assert.NotNull(manager);
        try
        {
            float deadline = Time.realtimeSinceStartup + 30;
            while (!manager.songPlayer.IsScheduled && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(manager.songPlayer.IsScheduled); manager.endWaitSeconds = 10000;
            foreach (var judge in Object.FindObjectsByType<SaberCutJudge>(FindObjectsSortMode.None)) judge.autonomous = false;
            foreach (var input in Object.FindObjectsByType<SaberInputBridge>(FindObjectsSortMode.None)) input.enabled = false;
            floor = Object.FindFirstObjectByType<FloorRenderer>(); Assert.NotNull(floor);
            Assert.AreEqual(StageTheme.AstralOrbit, floor.ActiveTheme);
            scenic = floor.GetComponentInChildren<ScenicStageWorld>(); Assert.IsTrue(scenic.MeteorResponsesReady);
            effects = floor.GetComponentInChildren<StageReactiveEffects>(); Assert.NotNull(effects);
            notes.Clear(); manager.noteSpawner.OnNoteSpawned += Collect;
            manager.noteSpawner.SetExtraOffsetSeconds(0);
            manager.noteSpawner.SetChart(Single(3));
            SetClock(.9); yield return null; yield return null;
            SetClock(1); yield return null; yield return null;
            var note = notes.Find(n => n != null && n.isActiveAndEnabled && !n.IsFinalized &&
                StageReactiveEffects.FloorLaneForX(n.transform.position.x) == 3);
            Assert.NotNull(note);
            // 実ManagerのUpdateが進めた時計で人工の正方向入力を与える。描画Tickは直接呼ばない。
            Cut(note, manager.songPlayer.SongTime, Vector3.right * 6, CutDirection.None, false);
            Assert.AreEqual(JudgmentTier.Perfect, manager.scoreManager.LastTier);
            Assert.AreEqual(8, scenic.MeteorLaneMask); Assert.AreEqual(8, effects.ActiveFloorLaneMask);
            SetClock(1.30); yield return null; yield return null;
            Assert.Greater(scenic.MeteorOpening(3), .9f);
            Assert.That(scenic.LastTickSeconds, Is.EqualTo(manager.songPlayer.SongTime).Within(.15));
            manager.songPlayer.Stop(); double stopped = scenic.LastTickSeconds;
            var held = MeteorMeshForLane(scenic, 3).vertices;
            yield return null; yield return null;
            Assert.AreEqual(stopped, scenic.LastTickSeconds);
            CollectionAssert.AreEqual(held, MeteorMeshForLane(scenic, 3).vertices);
            manager.noteSpawner.SetChart(new ChartData());
            Assert.AreEqual(0, scenic.MeteorLaneMask); Assert.AreEqual(0, scenic.ActiveMeteorResponseCount);
        }
        finally { if (manager != null) { manager.enabled = false; manager.songPlayer.Stop(); } }
        yield return ReleaseFloorAndAssertResources();
    }

    static Mesh MeteorMeshForLane(ScenicStageWorld world, int lane)
    {
        string[] names = { "OrbitingRock-1-1", "OrbitingRock-1-2", "OrbitingRock1-2", "OrbitingRock1-1" };
        return world.transform.Find(names[lane]).GetComponent<MeshFilter>().sharedMesh;
    }

    static void CreateMeteorFloorBeforeManagerStart(Scene scene, LoadSceneMode mode)
    {
        if (scene.name != "Game") return;
        var forced = new GameObject("MeteorResponseGameTestFloor").AddComponent<FloorRenderer>();
        forced.randomizeOnPlay = false; forced.Build(StageTheme.AstralOrbit);
    }

    [UnityTest, Timeout(120000)]
    public IEnumerator VaultCurtainIsIndependentOfFinalJudgmentsAndKeepsPerfectFloorAndSideResponse()
    {
        overrideCurtainEffectsSetting = true; DisplaySettings.SetReducedEffectsForTest(false);
        yield return LoadGame(StageTheme.VioletVault);
        var curtain = floor.GetComponentInChildren<VioletCurtainStage>(); Assert.NotNull(curtain);
        var curtainMesh = curtain.GetComponentInChildren<MeshFilter>().sharedMesh;
        var rest = curtainMesh.vertices;
        timeline = new StagePerformanceTimeline { sections = new[] {
            new StagePerformanceTimeline.Section { startSeconds = 0, endSeconds = 20, intensity = 1 } } };
        var simultaneous = new ChartData { bpm = 120, notes = new List<NoteData>() };
        for (int lane = 0; lane < 4; lane++)
        {
            var data = Single(lane).notes[0]; data.time = 10000;
            data.type = "direction"; data.direction = "up"; data.color = lane < 2 ? "blue" : "red";
            simultaneous.notes.Add(data);
        }
        SetChart(simultaneous); yield return null;
        manager.noteSpawner.Tick(10); Draw(10);
        Assert.AreEqual(1, curtain.Opening);
        var peak = curtainMesh.vertices;
        CollectionAssert.AreNotEqual(rest, peak, "明示区間で幕が実際に畳まれる");
        int perfectMask = 0;
        for (int lane = 0; lane < 4; lane++)
        {
            var note = notes.Find(n => n != null && n.isActiveAndEnabled && !n.IsFinalized &&
                StageReactiveEffects.FloorLaneForX(n.transform.position.x) == lane);
            Assert.NotNull(note);
            bool perfect = lane == 0 || lane == 2;
            Cut(note, 10, (perfect ? Vector3.up : Vector3.right) * 6,
                perfect ? CutDirection.Up : CutDirection.Right);
            Assert.AreEqual(perfect ? JudgmentTier.Perfect : JudgmentTier.Great, manager.scoreManager.LastTier);
            if (perfect) perfectMask |= 1 << lane;
            Assert.AreEqual(perfectMask, effects.ActiveFloorLaneMask, "Greatの実列へ成功床を追加しない");
            Draw(10);
            CollectionAssert.AreEqual(peak, curtainMesh.vertices, "曲同期の幕を成功・方向降格で動かさない");
        }
        Draw(10.12);
        var successMesh = effects.GetComponent<MeshFilter>().sharedMesh;
        bool hasSide = false, hasFloor = false;
        foreach (var point in successMesh.vertices)
        {
            hasSide |= point.y > floor.floorY + .7f;
            hasFloor |= point.y < floor.floorY + .7f;
        }
        Assert.IsTrue(hasSide && hasFloor, "曲同期の幕が既存Perfect床と側面反応を消している");

        var longChart = Single(2); longChart.notes[0].time = 10000;
        longChart.notes[0].type = "long"; longChart.notes[0].count = 4;
        SetChart(longChart); yield return null;
        manager.noteSpawner.Tick(10); Draw(10);
        for (int i = 0; i < 3; i++) Cut(notes[0], 10, Vector3.right * 6, CutDirection.None);
        Assert.AreEqual(0, effects.ActiveFloorLaneMask, "ロング途中に成功床を足さない");
        CollectionAssert.AreEqual(peak, curtainMesh.vertices, "ロング途中を幕の合図にしない");
        notes[0].MarkMiss(); Draw(10);
        Assert.AreEqual(0, effects.ActiveFloorLaneMask);
        CollectionAssert.AreEqual(peak, curtainMesh.vertices, "Missで曲区間の姿勢を消さない");
        SetChart(new ChartData());
        Assert.AreEqual(0, curtain.Opening);
        CollectionAssert.AreEqual(rest, curtainMesh.vertices, "譜面再読込で幕を原位置へ戻す");
        timeline = new StagePerformanceTimeline(); Draw(10);
        CollectionAssert.AreEqual(rest, curtainMesh.vertices, "区間なしを譜面密度から補わない");
        yield return ReleaseFloorAndAssertResources();
    }

    [UnityTest, Timeout(120000)]
    public IEnumerator VaultCurtainActualManagerDrivesLongSectionAndStopsWithTheSong()
    {
        overrideCurtainEffectsSetting = true; DisplaySettings.SetReducedEffectsForTest(false);
        GameSession.SelectedSongId = "揺籠"; GameSession.SelectedDifficulty = "hard";
        GameSession.IsCalibrationMode = false;
        SceneManager.sceneLoaded += CreateVaultFloorBeforeManagerStart;
        try { yield return SceneManager.LoadSceneAsync("Game", LoadSceneMode.Single); }
        finally { SceneManager.sceneLoaded -= CreateVaultFloorBeforeManagerStart; }
        manager = Object.FindFirstObjectByType<GamePlayManager>(); Assert.NotNull(manager);
        try
        {
            float deadline = Time.realtimeSinceStartup + 30;
            while (!manager.songPlayer.IsScheduled && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(manager.songPlayer.IsScheduled);
            manager.endWaitSeconds = 10000;
            manager.noteSpawner.SetChart(new ChartData());
            foreach (var judge in Object.FindObjectsByType<SaberCutJudge>(FindObjectsSortMode.None)) judge.autonomous = false;
            foreach (var input in Object.FindObjectsByType<SaberInputBridge>(FindObjectsSortMode.None)) input.enabled = false;
            floor = Object.FindFirstObjectByType<FloorRenderer>(); Assert.NotNull(floor);
            Assert.AreEqual(StageTheme.VioletVault, floor.ActiveTheme);
            var curtain = floor.GetComponentInChildren<VioletCurtainStage>(); Assert.NotNull(curtain);
            var mesh = curtain.GetComponentInChildren<MeshFilter>().sharedMesh;
            timeline = StagePerformanceTimeline.Load("揺籠");
            // 音源のDSP時計を飛ばす接続検証。実時間通しとは別に扱い、Floor.Tickを直接呼ばない。
            foreach (var time in new[] { 100.0, 144.245, 150.0, 163.361, 166.0, 150.0, 100.0 })
            {
                SetClock(time); yield return null; yield return null;
                Assert.That(curtain.LastTickSeconds, Is.EqualTo(manager.songPlayer.SongTime).Within(.15));
                Assert.AreEqual(timeline.EvaluateVaultCurtain(curtain.LastTickSeconds), curtain.Opening, .00001f,
                    "GamePlayManagerから幕へ明示区間が届かない");
                if (time == 150) Assert.AreEqual(1, curtain.Opening);
                if (time == 100 || time == 166) Assert.AreEqual(0, curtain.Opening);
            }
            SetClock(150); yield return null; yield return null;
            manager.songPlayer.Stop(); double stopped = curtain.LastTickSeconds;
            var held = mesh.vertices;
            yield return null; yield return null;
            Assert.AreEqual(stopped, curtain.LastTickSeconds);
            CollectionAssert.AreEqual(held, mesh.vertices, "停止中に独立時計で幕が進む");
        }
        finally
        {
            if (manager != null) { manager.enabled = false; manager.songPlayer.Stop(); }
        }
        yield return ReleaseFloorAndAssertResources();
    }

    static void CreateVaultFloorBeforeManagerStart(Scene scene, LoadSceneMode mode)
    {
        if (scene.name != "Game") return;
        var forced = new GameObject("VaultCurtainGameTestFloor").AddComponent<FloorRenderer>();
        forced.randomizeOnPlay = false; forced.Build(StageTheme.VioletVault);
    }

    [UnityTest, Timeout(120000)]
    public IEnumerator PulseArrayActualManagerDrivesFormationAndStopsWithTheSong()
    {
        GameSession.SelectedSongId = "揺籠"; GameSession.SelectedDifficulty = "hard";
        GameSession.IsCalibrationMode = false;
        SceneManager.sceneLoaded += CreatePulseFloorBeforeManagerStart;
        try { yield return SceneManager.LoadSceneAsync("Game", LoadSceneMode.Single); }
        finally { SceneManager.sceneLoaded -= CreatePulseFloorBeforeManagerStart; }
        manager = Object.FindFirstObjectByType<GamePlayManager>(); Assert.NotNull(manager);
        try
        {
            float deadline = Time.realtimeSinceStartup + 30;
            while (!manager.songPlayer.IsScheduled && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(manager.songPlayer.IsScheduled, "実シーンの音源準備が完了しません。");
            // 本物のUpdate接続を使い、結果保存やシーン遷移に届かない余韻をテスト内だけ設定する。
            manager.endWaitSeconds = 10000;
            manager.noteSpawner.SetChart(new ChartData());
            foreach (var judge in Object.FindObjectsByType<SaberCutJudge>(FindObjectsSortMode.None)) judge.autonomous = false;
            foreach (var input in Object.FindObjectsByType<SaberInputBridge>(FindObjectsSortMode.None)) input.enabled = false;
            floor = Object.FindFirstObjectByType<FloorRenderer>(); Assert.NotNull(floor);
            Assert.AreEqual(StageTheme.PulseArray, floor.ActiveTheme);
            var stage = floor.GetComponentInChildren<PulseArrayStage>(); Assert.NotNull(stage);
            timeline = StagePerformanceTimeline.Load("揺籠");
            var fixture = stage.transform.Find("LampFixtures").GetComponent<MeshFilter>().sharedMesh;
            // 実時間通しとは別の、DSP基準の早送り・巻き戻し接続検証。直接Floor.Tickしない。
            foreach (var time in new[] { 100.0, 143.745, 150.0, 163.861, 166.0, 150.0, 100.0 })
            {
                SetClock(time);
                yield return null; yield return null;
                Assert.That(stage.LastTickSeconds, Is.EqualTo(manager.songPlayer.SongTime).Within(.15));
                Assert.AreEqual(timeline.EvaluateLightFormation(stage.LastTickSeconds), stage.FormationIntensity, .00001f,
                    "GamePlayManagerから明示区間の配列値が届いていません。");
                if (time == 150) Assert.AreEqual(1, stage.FormationIntensity);
                if (time == 100 || time == 166) Assert.AreEqual(0, stage.FormationIntensity);
            }
            SetClock(150); yield return null; yield return null;
            Assert.AreEqual(1, stage.FormationIntensity);
            manager.songPlayer.Stop(); double stopped = stage.LastTickSeconds;
            var held = fixture.vertices;
            yield return null; yield return null;
            Assert.AreEqual(stopped, stage.LastTickSeconds);
            CollectionAssert.AreEqual(held, fixture.vertices, "曲停止中に独立時計で配列が進んでいます。");
        }
        finally
        {
            if (manager != null) { manager.enabled = false; manager.songPlayer.Stop(); }
        }
        yield return ReleaseFloorAndAssertResources();
    }

    static void CreatePulseFloorBeforeManagerStart(Scene scene, LoadSceneMode mode)
    {
        if (scene.name != "Game") return;
        var forced = new GameObject("PulseFormationGameTestFloor").AddComponent<FloorRenderer>();
        forced.randomizeOnPlay = false; forced.Build(StageTheme.PulseArray);
    }

    IEnumerator RealTimeAudioWithScriptedPerfectInput(StageTheme theme)
    {
        yield return LoadGame(theme);
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
        floor.Tick(time, chorus, timeline.EvaluateLightFormation(time), timeline.EvaluateVaultCurtain(time));
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
