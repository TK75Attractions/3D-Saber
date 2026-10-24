using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

// 開幕の 3・2・1・START の途中で音の出力先が変わっても、曲も開始演出も止めない(2026-10-24 ユーザー「曲止めないで上の表示だけにして」)。
// 曲の音は裏で読み直し、予約していた時刻(過ぎていれば今の位置)から鳴らす。最初のノーツの到着時刻と移動時間、曲の音とノーツの時計はそのまま。
public class OpeningAudioRecoveryPlayTests
{
    readonly Dictionary<FieldInfo, object> savedSession = new Dictionary<FieldInfo, object>();
    GamePlayManager manager;
    CuttableNote firstNote;
    double firstSpawnTime;
    bool spawnedWhileHeld;
    bool hadSpeed, hadOffset;
    float savedSpeed;
    int savedOffset;

    [SetUp]
    public void SetUp()
    {
        manager = null; firstNote = null; firstSpawnTime = double.NaN; spawnedWhileHeld = false;
        foreach (var field in typeof(GameSession).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (!field.IsLiteral && !field.IsInitOnly) savedSession[field] = field.GetValue(null);
        hadSpeed = PlayerPrefs.HasKey("noteApproachTime");
        savedSpeed = PlayerPrefs.GetFloat("noteApproachTime");
        hadOffset = PlayerPrefs.HasKey("judgmentOffsetMs");
        savedOffset = PlayerPrefs.GetInt("judgmentOffsetMs");
        GameSession.SelectedSongId = "Epilogue";
        GameSession.SelectedDifficulty = "Normal";
        GameSession.NoteApproachTime = 4f;
        GameSession.JudgmentOffsetMs = 0;
        GameSession.IsCalibrationMode = false;
        GameSession.TutorialPending = false;
        SceneManager.sceneLoaded += Configure;
    }

    void Configure(Scene scene, LoadSceneMode mode)
    {
        if (scene.name != "Game") return;
        manager = Object.FindFirstObjectByType<GamePlayManager>();
        manager.enableStartCountdown = true;
        manager.noteSpawner.OnNoteSpawned += note =>
        {
            if (firstNote != null) return;
            firstNote = note;
            firstSpawnTime = manager.songPlayer.SongTime;
            spawnedWhileHeld = manager.songPlayer.IsPaused;
        };
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        SceneManager.sceneLoaded -= Configure;
        var scene = SceneManager.GetActiveScene();
        SceneManager.SetActiveScene(SceneManager.CreateScene("OpeningAudioRecoveryCleanup"));
        if (scene.IsValid() && scene.isLoaded) yield return SceneManager.UnloadSceneAsync(scene);
        if (hadSpeed) PlayerPrefs.SetFloat("noteApproachTime", savedSpeed); else PlayerPrefs.DeleteKey("noteApproachTime");
        if (hadOffset) PlayerPrefs.SetInt("judgmentOffsetMs", savedOffset); else PlayerPrefs.DeleteKey("judgmentOffsetMs");
        PlayerPrefs.Save();
        foreach (var item in savedSession) item.Key.SetValue(null, item.Value);
        savedSession.Clear();
    }

    [UnityTest]
    public IEnumerator OutputChangeBeforeStartKeepsTheCountInAndTheSong() => Verify(false, false);

    [UnityTest]
    public IEnumerator OutputChangeDuringStartKeepsTheStartWordAndTheSong() => Verify(true, false);

    // 読み直しの途中でまた変わっても、読み直しからやり直すだけ。曲も開始演出も止めない。
    [UnityTest]
    public IEnumerator OutputChangingAgainDuringTheReloadKeepsThePlace() => Verify(true, true);

    IEnumerator Verify(bool afterStart, bool changeAgain)
    {
        // 前の試験が送った剣の位置を持ち越さない(曲が鳴り始めたとき、届いていた剣が止まったとみなして曲を止めないように)。
        foreach (var old in Object.FindObjectsByType<InputPoint>(FindObjectsSortMode.None)) Object.DestroyImmediate(old.gameObject);
        yield return SceneManager.LoadSceneAsync("Game");
        double deadline = Time.realtimeSinceStartupAsDouble + 20;
        var ready = typeof(GamePlayManager).GetField("ready", BindingFlags.Instance | BindingFlags.NonPublic);
        while (Time.realtimeSinceStartupAsDouble < deadline && (manager == null || !(bool)ready.GetValue(manager))) yield return null;
        Assert.NotNull(manager);
        Assert.True((bool)ready.GetValue(manager));
        var countdown = Object.FindFirstObjectByType<GameStartCountdown>();
        Assert.NotNull(countdown);
        var gate = Object.FindFirstObjectByType<JudgeGateFrame>();
        Assert.NotNull(gate, "本編シーンに判定ゲートがある");
        double target = countdown.StartCueDspTime + (afterStart ? .08 : -.10);
        deadline = Time.realtimeSinceStartupAsDouble + 5;
        while (AudioSettings.dspTime < target && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        Assert.Less(AudioSettings.dspTime, countdown.EndDspTime, "開始演出がまだ動いている並びで確かめる");
        Assert.True(GateTinted(gate), "開始演出がゲートを光らせている並びで確かめる");
        Assert.IsNull(firstNote, "最初のノーツが出る前に確かめる");
        string runId = GameSession.AchievementRunId;
        double oldFirstHit = manager.noteSpawner.CurrentChart.notes[0].time;
        double firstHit = manager.noteSpawner.EffectiveTime(manager.noteSpawner.CurrentChart.notes[0]);
        AudioOutputRecovery.SimulateChangeForTest();
        Assert.False(manager.songPlayer.IsPaused, "出力先が変わっても曲は止めない");
        double changedSongTime = manager.songPlayer.SongTime;
        Assert.Less(changedSongTime, manager.songPlayer.PlayStartSeconds, "曲が鳴る前(開幕の待ち時間の途中)で変わった");
        double observeUntil = Time.realtimeSinceStartupAsDouble + .18;
        while (Time.realtimeSinceStartupAsDouble < observeUntil) yield return null;
        Debug.Log("[OPENING_AUDIO_RECOVERY] phase=" + (changeAgain ? "again" : afterStart ? "start" : "count")
            + " countdownKept=" + (countdown != null) + " headline=" + manager.TrackingView.HeadlineShown
            + " gateTinted=" + GateTinted(gate) + " countInSounds=" + CountInSounds());
        if (!changeAgain) UICatalogPlayTests.Capture(afterStart ? "audio-recovery-during-start" : "audio-recovery-before-start");
        Assert.IsTrue(countdown != null, "開始演出は消さない(曲を止めないので数え直さない)");
        Assert.Greater(manager.songPlayer.SongTime, changedSongTime, "時計は止めない");
        StringAssert.DoesNotContain("音の出力先", manager.TrackingView.HeadlineShown, "知らせは上の1行だけ(中央には出さない)");
        // 知らせが落ち着いたら(0.5 秒後)、裏で曲の音を読み直す。
        bool reloadSeen = false;
        deadline = Time.realtimeSinceStartupAsDouble + 10;
        while (Time.realtimeSinceStartupAsDouble < deadline)
        {
            reloadSeen |= manager.AudioReloading;
            if (changeAgain && reloadSeen && manager.AudioReloading)
            {
                // 読み直しの途中でもう一度変わる。読み直しはやり直しになり、曲は止めない。
                AudioOutputRecovery.SimulateChangeForTest();
                Assert.False(manager.AudioReloading, "読み直しかけの曲は取り消す");
                Assert.False(manager.songPlayer.IsPaused);
                changeAgain = false; reloadSeen = false;
                UICatalogPlayTests.Capture("audio-recovery-changed-again");
            }
            else if (reloadSeen && !manager.AudioReloading) break;
            yield return null;
        }
        Assert.True(reloadSeen && !manager.AudioReloading, "曲の音を裏で読み直し終える");
        Assert.False(changeAgain, "もう一度変わる並びも確かめた");
        Assert.False(manager.songPlayer.IsPaused);
        Assert.Greater(manager.songPlayer.Clip.samples, 0);
        Assert.AreEqual("Game", SceneManager.GetActiveScene().name);
        Assert.AreEqual(runId, GameSession.AchievementRunId, "同じ回を続ける");
        Assert.AreEqual(oldFirstHit, manager.noteSpawner.CurrentChart.notes[0].time, "譜面の到着時刻を変えない");
        // 最初のノーツは設定どおりの移動時間で、もとの到着時刻に来る。曲の音とノーツの時計もそろう。
        deadline = Time.realtimeSinceStartupAsDouble + 8;
        while (firstNote == null && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        Assert.NotNull(firstNote, "最初のノーツが流れてくる");
        Assert.False(spawnedWhileHeld, "曲を止めずにノーツが出る");
        Assert.That(firstNote.HitTime, Is.EqualTo(firstHit).Within(.001), "最初のノーツの到着時刻を変えない");
        Assert.That(firstNote.HitTime - firstSpawnTime, Is.EqualTo(manager.noteSpawner.approachTime).Within(.15),
            "最初のノーツも設定した4秒の移動時間で来る");
        var source = manager.songPlayer.GetComponent<AudioSource>();
        deadline = Time.realtimeSinceStartupAsDouble + 8;
        while (source.timeSamples < 4800 && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        Assert.Greater(source.timeSamples, 0, "読み直した曲の音が鳴り始める");
        AudioSettings.GetDSPBufferSize(out int frames, out _);
        Assert.That(manager.songPlayer.SongTime, Is.EqualTo(source.timeSamples / (double)source.clip.frequency)
            .Within(frames / (double)AudioSettings.outputSampleRate + .01), "曲の音とノーツの時計がそろう");
        Assert.Zero(manager.scoreManager.MissCount);
    }

    // ゲートの辺と四隅に、開始演出の色の上書き(CountInGateCue の MaterialPropertyBlock)が残っているか。
    static bool GateTinted(JudgeGateFrame gate)
    {
        foreach (var renderer in gate.GetComponentsInChildren<MeshRenderer>())
            if (renderer.HasPropertyBlock()) return true;
        return false;
    }

    // 開始演出のクリック音(GameStartCountdown の CountInTick・CountInSwish・CountInStart)を鳴らしている音源の数。
    static int CountInSounds()
    {
        int count = 0;
        foreach (var source in Object.FindObjectsByType<AudioSource>(FindObjectsSortMode.None))
            if (source.isPlaying && source.clip != null && source.clip.name.StartsWith("CountIn")) count++;
        return count;
    }
}
