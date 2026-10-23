using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

// 実譜面を読み取り、低速設定でも冒頭のノーツだけ移動時間が短くならないことを確認する。
public class SongLeadInPlayTests
{
    readonly Dictionary<FieldInfo, object> sessionValues = new Dictionary<FieldInfo, object>();
    const string SpeedKey = "noteApproachTime";
    const string OffsetKey = "judgmentOffsetMs";
    bool hadSpeed, hadOffset;
    float savedSpeed;
    int savedOffset;
    GamePlayManager manager;
    CuttableNote firstNote;
    double firstSpawnTime;
    bool countdownEnabled;

    [SetUp]
    public void SetUp()
    {
        manager = null; firstNote = null; firstSpawnTime = double.NaN;
        foreach (var field in typeof(GameSession).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (!field.IsLiteral && !field.IsInitOnly) sessionValues[field] = field.GetValue(null);
        hadSpeed = PlayerPrefs.HasKey(SpeedKey); savedSpeed = PlayerPrefs.GetFloat(SpeedKey);
        hadOffset = PlayerPrefs.HasKey(OffsetKey); savedOffset = PlayerPrefs.GetInt(OffsetKey);
        GameSession.NoteApproachTime = 4f;
        GameSession.JudgmentOffsetMs = 0;
        GameSession.SelectedSongId = "Epilogue";
        GameSession.SelectedDifficulty = "Normal";
        GameSession.IsCalibrationMode = false;
        GameSession.TutorialPending = false;
        SceneManager.sceneLoaded += ConfigureGame;
    }

    void ConfigureGame(Scene scene, LoadSceneMode mode)
    {
        if (scene.name != "Game") return;
        manager = Object.FindFirstObjectByType<GamePlayManager>();
        manager.enableStartCountdown = countdownEnabled;
        manager.noteSpawner.OnNoteSpawned += note =>
        {
            if (firstNote != null) return;
            firstNote = note;
            firstSpawnTime = manager.songPlayer.SongTime;
        };
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        SceneManager.sceneLoaded -= ConfigureGame;
        var scene = SceneManager.GetActiveScene();
        SceneManager.SetActiveScene(SceneManager.CreateScene("SongLeadInCleanup"));
        if (scene.IsValid() && scene.isLoaded) yield return SceneManager.UnloadSceneAsync(scene);
        if (hadSpeed) PlayerPrefs.SetFloat(SpeedKey, savedSpeed); else PlayerPrefs.DeleteKey(SpeedKey);
        if (hadOffset) PlayerPrefs.SetInt(OffsetKey, savedOffset); else PlayerPrefs.DeleteKey(OffsetKey);
        PlayerPrefs.Save();
        foreach (var entry in sessionValues) entry.Key.SetValue(null, entry.Value);
        sessionValues.Clear();
        DisplaySettings.ResetReducedEffectsCacheForTest();
    }

    IEnumerator OpenGame(bool useCountdown)
    {
        countdownEnabled = useCountdown;
        yield return SceneManager.LoadSceneAsync("Game");
        double deadline = Time.realtimeSinceStartupAsDouble + 20;
        var ready = typeof(GamePlayManager).GetField("ready", BindingFlags.Instance | BindingFlags.NonPublic);
        while (Time.realtimeSinceStartupAsDouble < deadline && (manager == null || !(bool)ready.GetValue(manager))) yield return null;
        Assert.NotNull(manager);
        Assert.True((bool)ready.GetValue(manager));
    }

    IEnumerator VerifyFirstNoteTravel(bool useCountdown)
    {
        yield return OpenGame(useCountdown);
        var countdown = Object.FindFirstObjectByType<GameStartCountdown>();
        double countdownEnd = useCountdown ? countdown.EndDspTime : 0;
        if (useCountdown)
        {
            double scheduled = (double)typeof(SongPlayer).GetField("startDspTime", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager.songPlayer);
            Assert.That(scheduled, Is.EqualTo(countdown.SongStartDspTime).Within(.00001), "音と曲時計は同じ開始予約を使う");
            Assert.That(countdown.StartCueDspTime - countdown.FirstBeatDspTime,
                Is.EqualTo(3 * GameStartCountdown.CountSeconds(92)).Within(.00001), "3→2→1→STARTの拍間隔を維持する");
            while (AudioSettings.dspTime < countdownEnd)
            {
                Assert.IsNull(firstNote, "カウント中とSTART演出中にノーツを出さない");
                Assert.Zero(manager.scoreManager.MissCount);
                yield return null;
            }
        }
        double deadline = Time.realtimeSinceStartupAsDouble + 5;
        while (firstNote == null && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        Assert.NotNull(firstNote);
        Assert.Less(firstSpawnTime, 0, "音の開始前から冒頭ノーツを流す");
        Assert.That(firstNote.HitTime - firstSpawnTime, Is.EqualTo(manager.noteSpawner.approachTime).Within(.15),
            "先頭にも設定した4秒の移動時間を確保する");
        Assert.False(manager.songPlayer.IsPlaying, "曲の音が鳴る前に先読みする");
        Assert.That(firstNote.HitTime, Is.EqualTo(1.296 + manager.extraOffsetSeconds).Within(.001), "譜面の時刻は変更しない");
        Assert.AreEqual(0, manager.scoreManager.MissCount);
        if (useCountdown)
        {
            Assert.IsTrue(countdown == null, "最初のノーツが入る前にSTARTは消える");
            Assert.GreaterOrEqual(AudioSettings.dspTime, countdownEnd + GameStartCountdown.NoteEntryGapSeconds);
        }
        // 停止後にSongTime=0へ戻っても、先読みしたノーツを先へ進めない。
        manager.songPlayer.Stop();
        int index = manager.noteSpawner.NextIndex;
        Vector3 position = firstNote.transform.position;
        yield return null; yield return null;
        Assert.AreEqual(index, manager.noteSpawner.NextIndex);
        Assert.AreEqual(position, firstNote.transform.position);
        Assert.AreEqual("Game", SceneManager.GetActiveScene().name);
    }

    // 頭の5秒が無音の escapade は、音源の5秒目から鳴らす(仕様面の点検 7)。譜面の時刻はそのまま、最初のノーツも移動時間を保つ。
    [UnityTest]
    public IEnumerator EscapadeStartsAtFiveSecondsWithItsNotesOnTheSameClock()
    {
        GameSession.SelectedSongId = "escapade";
        GameSession.SelectedDifficulty = "Easy";
        GameSession.NoteApproachTime = 1f;
        // 前の試験が送った剣の位置を持ち越さない(届いていた剣が止まったとみなして曲を止めないように)。
        foreach (var old in Object.FindObjectsByType<InputPoint>(FindObjectsSortMode.None)) Object.DestroyImmediate(old.gameObject);
        yield return OpenGame(true);
        var countdown = Object.FindFirstObjectByType<GameStartCountdown>();
        double zero = (double)typeof(SongPlayer).GetField("startDspTime", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager.songPlayer);
        Assert.AreEqual(1.25f, manager.noteSpawner.approachTime, "Easyのふつうは、この曲だけ1.25秒");
        Assert.AreEqual(1f, GameSession.NoteApproachTime, "保存した速さはそのまま");
        Assert.That(zero + 5.0, Is.EqualTo(countdown.SongStartDspTime).Within(.0001), "鳴らし始めは5秒目、曲の0秒はその5秒前");
        Assert.AreEqual(5.0, manager.songPlayer.PlayStartSeconds, 1 / 44100.0);
        Assert.AreEqual(107.85, manager.songPlayer.ProgressEndSeconds, 1e-9, "音が鳴り終わる秒(stage.json)");
        double deadline = Time.realtimeSinceStartupAsDouble + 12;
        while (!manager.songPlayer.IsPlaying && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        Assert.True(manager.songPlayer.IsPlaying);
        Assert.That(manager.songPlayer.SongTime, Is.InRange(4.99, 5.6), "曲の時計は音源の頭からの秒のまま");
        deadline = Time.realtimeSinceStartupAsDouble + 5;
        while (firstNote == null && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        Assert.NotNull(firstNote);
        Assert.That(firstNote.HitTime - firstSpawnTime, Is.EqualTo(manager.noteSpawner.approachTime).Within(.15), "最初のノーツも移動時間を保つ");
        Assert.That(firstNote.HitTime, Is.EqualTo(5.07 + manager.extraOffsetSeconds).Within(.02), "譜面の時刻は変更しない");
        Assert.AreEqual(0, manager.scoreManager.MissCount);
    }

    [UnityTest]
    public IEnumerator SlowNotesEnterAfterCountInWithTheirFullTravelTime()
    {
        yield return VerifyFirstNoteTravel(true);
    }

    [UnityTest]
    public IEnumerator CountdownOffStillPreservesTheFirstNotesTravelTime()
    {
        yield return VerifyFirstNoteTravel(false);
    }

    [UnityTest]
    public IEnumerator LaterOpeningKeepsTheOriginalThreeBeatCountIn()
    {
        GameSession.SelectedSongId = "Morning";
        GameSession.NoteApproachTime = 1f;
        yield return OpenGame(true);
        var countdown = Object.FindFirstObjectByType<GameStartCountdown>();
        Assert.NotNull(countdown);
        Assert.AreEqual(countdown.StartCueDspTime, countdown.SongStartDspTime, "長い前奏ならSTARTと楽曲を同時に始める");
        double remaining = countdown.SongStartDspTime - AudioSettings.dspTime;
        Assert.LessOrEqual(remaining, .12 + 3 * GameStartCountdown.CountSeconds(118) + .02);
        Assert.Greater(remaining, .5);
        Assert.IsNull(firstNote, "まだ先読み範囲に入らないノーツは生成しない");
        manager.songPlayer.Stop();
        yield return null;
        Assert.IsNull(firstNote);
    }

    [UnityTest]
    public IEnumerator OpeningAtZeroIsReadableAfterStart() => VerifyOpeningAtZero(false);

    [UnityTest]
    public IEnumerator OpeningAtZeroIsReadableWithReducedEffects() => VerifyOpeningAtZero(true);

    IEnumerator VerifyOpeningAtZero(bool reduced)
    {
        DisplaySettings.SetReducedEffectsForTest(reduced);
        GameSession.SelectedSongId = "Aobasou";
        GameSession.NoteApproachTime = 1f;
        GameSession.JudgmentOffsetMs = -100;
        yield return OpenGame(true);
        var countdown = Object.FindFirstObjectByType<GameStartCountdown>();
        double cue = countdown.StartCueDspTime, end = countdown.EndDspTime;
        while (AudioSettings.dspTime < cue + .1) yield return null;
        Assert.IsTrue(countdown.StartWord.Root.gameObject.activeSelf);
        Assert.IsNull(firstNote, "0秒のノーツもSTART表示中は出現しない");
        Capture(reduced, "start");
        while (AudioSettings.dspTime < end + .05) yield return null;
        Assert.IsTrue(countdown == null);
        Assert.IsNull(firstNote, "演出後に短い間を空ける");
        double deadline = Time.realtimeSinceStartupAsDouble + 5;
        while (firstNote == null && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        Assert.NotNull(firstNote);
        Assert.That(firstNote.HitTime, Is.EqualTo(-.1 + manager.extraOffsetSeconds).Within(.001), "判定補正と譜面時刻を維持する");
        Assert.That(firstNote.HitTime - firstSpawnTime, Is.EqualTo(1).Within(.15));
        Assert.Zero(manager.scoreManager.MissCount);
        while (manager.songPlayer.SongTime < firstNote.HitTime - .25 && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        Assert.IsNull(Object.FindFirstObjectByType<GameStartCountdown>(), "接近中の視界にSTARTを残さない");
        Capture(reduced, "approach");
        var source = manager.songPlayer.GetComponent<AudioSource>();
        while (source.timeSamples < 4800 && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        Assert.Greater(source.timeSamples, 0, "演出を消した後も予約した音源は再生される");
        AudioSettings.GetDSPBufferSize(out int frames, out _);
        Assert.That(manager.songPlayer.SongTime, Is.EqualTo(source.timeSamples / (double)source.clip.frequency)
            .Within(frames / (double)AudioSettings.outputSampleRate + .01), "音声とノーツの曲時計が同期する");
    }

    // 指定時のみ実Gameの通常/LOW画像を保存して、文字とノーツの重なりを目視確認する。
    static void Capture(bool reduced, string phase)
    {
        string output = System.Environment.GetEnvironmentVariable("SABER_START_VISIBILITY_CAPTURES");
        if (string.IsNullOrEmpty(output)) return;
        Directory.CreateDirectory(output);
        var camera = Camera.main;
        var target = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32);
        var pixels = new Texture2D(1280, 720, TextureFormat.RGB24, false);
        var oldTarget = camera.targetTexture; var oldActive = RenderTexture.active;
        float oldAspect = camera.aspect;
        var canvases = Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None)
            .Where(c => c.isRootCanvas && c.renderMode == RenderMode.ScreenSpaceOverlay)
            .Select(c => (canvas: c, camera: c.worldCamera, distance: c.planeDistance)).ToArray();
        try
        {
            target.Create(); camera.targetTexture = target; camera.aspect = 16f / 9;
            foreach (var item in canvases) { item.canvas.renderMode = RenderMode.ScreenSpaceCamera; item.canvas.worldCamera = camera; item.canvas.planeDistance = 1; }
            Canvas.ForceUpdateCanvases();
            RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
            RenderTexture.active = target;
            pixels.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0); pixels.Apply();
            File.WriteAllBytes(Path.Combine(output, $"{(reduced ? "low" : "normal")}-{phase}.png"), pixels.EncodeToPNG());
        }
        finally
        {
            foreach (var item in canvases) { item.canvas.renderMode = RenderMode.ScreenSpaceOverlay; item.canvas.worldCamera = item.camera; item.canvas.planeDistance = item.distance; }
            camera.targetTexture = oldTarget; camera.aspect = oldAspect; RenderTexture.active = oldActive;
            target.Release(); Object.Destroy(target); Object.Destroy(pixels);
        }
    }

    // 2026-10-05 の新しい 3・2・1: 拍ごとにゲートの辺が点き、数字は次の拍で斬られ、START の後は元の見た目に戻る。
    [UnityTest]
    public IEnumerator CountInLightsTheGateEdgesPerBeatAndRestoresThemAfterStart()
    {
        yield return OpenGame(true);
        var countdown = Object.FindFirstObjectByType<GameStartCountdown>();
        Assert.NotNull(countdown);
        var gate = Object.FindFirstObjectByType<JudgeGateFrame>();
        Assert.NotNull(gate, "本編シーンに判定ゲートがある");
        var left = gate.transform.Find("GateLeft").GetComponent<MeshRenderer>();
        var right = gate.transform.Find("GateRight").GetComponent<MeshRenderer>();
        var floor = manager.noteSpawner.FloorGuide;
        double first = countdown.FirstBeatDspTime, c = countdown.CountLength, end = countdown.EndDspTime;
        var block = new MaterialPropertyBlock();

        while (AudioSettings.dspTime < first + c * .5) yield return null;
        left.GetPropertyBlock(block); Color l = block.GetColor("_EmissionColor");
        right.GetPropertyBlock(block); Color r = block.GetColor("_EmissionColor");
        Assert.Greater(l.b, l.r, "3: 左の辺が青く点く");
        Assert.Less(r.maxColorComponent, l.maxColorComponent, "まだ点かない右の辺は暗い");
        if (floor != null) Assert.Greater(floor.CountTintAmount, 0f, "床の判定線も色付く");
        Assert.IsTrue(countdown.NumberAt(0).Root.gameObject.activeSelf, "「3」が出ている");

        while (AudioSettings.dspTime < first + c * 1.15) yield return null;
        Assert.IsTrue(countdown.NumberAt(0).ShowingPieces, "次の拍で「3」が斬られて割れる");
        while (AudioSettings.dspTime < first + c * 1.5) yield return null;
        right.GetPropertyBlock(block); r = block.GetColor("_EmissionColor");
        Assert.Greater(r.r, r.b, "2: 右の辺が赤く点く");

        while (countdown != null && AudioSettings.dspTime < end + .5) yield return null;
        yield return null;
        Assert.IsTrue(countdown == null, "START の演出が終わったら消える");
        Assert.IsFalse(left.HasPropertyBlock(), "ゲートの辺は元の材質の見た目に戻る");
        Assert.IsFalse(right.HasPropertyBlock());
        if (floor != null) Assert.AreEqual(0f, floor.CountTintAmount, "床も白に戻る");
    }

    [UnityTest]
    public IEnumerator SceneExitReleasesBarLineMaterials()
    {
        yield return OpenGame(true);
        var bars = manager.barLineSpawner;
        Assert.NotNull(bars);
        bars.Tick(-1);
        var renderer = bars.root.GetComponentsInChildren<MeshRenderer>().FirstOrDefault(r =>
            r.gameObject.name == "Line" && r.transform.parent.name == bars.barLinePrefab.name + "(Clone)");
        Assert.NotNull(renderer);
        Material material = renderer.sharedMaterial;
        Assert.NotNull(material, "実シーンの素材が空の小節線にも表示素材を割り当てる");
        Assert.AreEqual("Universal Render Pipeline/Unlit", material.shader.name);
        Assert.AreEqual(GameStageSkin.BarLineAlpha, material.GetColor("_BaseColor").a, .0001f);
        var scene = SceneManager.GetActiveScene();
        SceneManager.SetActiveScene(SceneManager.CreateScene("BarLineResourceCleanup"));
        yield return SceneManager.UnloadSceneAsync(scene);
        yield return null;
        Assert.True(material == null, "ゲーム終了時に実際のOnDestroy経路で素材を解放する");
    }
}
