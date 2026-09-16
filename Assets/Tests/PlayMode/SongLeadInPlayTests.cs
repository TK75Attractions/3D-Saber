using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
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
            var countdown = Object.FindFirstObjectByType<GameStartCountdown>();
            Assert.NotNull(countdown);
            double scheduled = (double)typeof(SongPlayer).GetField("startDspTime", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager.songPlayer);
            Assert.That(scheduled, Is.EqualTo(countdown.SongStartDspTime).Within(.00001), "STARTと音の開始は同じDSP時計");
            double firstBeat = (double)typeof(GameStartCountdown).GetField("firstBeatDspTime", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(countdown);
            Assert.That(scheduled - firstBeat, Is.EqualTo(3 * GameStartCountdown.BeatSeconds(92)).Within(.00001), "待ち時間を足しても拍間隔を引き伸ばさない");
            Assert.Less(AudioSettings.dspTime, firstBeat, "冒頭ノーツはカウント前から助走する");
            Assert.AreEqual(0f, countdown.GetComponent<CanvasGroup>().alpha, "3の表示だけがクリック音より先行しない");
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

    [UnityTest]
    public IEnumerator SlowNotesEnterDuringCountInWithTheirFullTravelTime()
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
        yield return OpenGame(true);
        var countdown = Object.FindFirstObjectByType<GameStartCountdown>();
        Assert.NotNull(countdown);
        double remaining = countdown.SongStartDspTime - AudioSettings.dspTime;
        Assert.LessOrEqual(remaining, .12 + 3 * GameStartCountdown.BeatSeconds(118) + .02);
        Assert.Greater(remaining, .5);
        Assert.IsNull(firstNote, "まだ先読み範囲に入らないノーツは生成しない");
        manager.songPlayer.Stop();
        yield return null;
        Assert.IsNull(firstNote);
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
