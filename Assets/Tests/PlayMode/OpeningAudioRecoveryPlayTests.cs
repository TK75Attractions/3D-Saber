using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

// 出力先の変更で曲を止めたとき、古い開始演出を「再開まで」と同時に出さない。
// 開幕の 3・2・1・START は文字・ゲートと床の色・クリック音ごと取り除き、読み直したあとの 3・2・1 で続きから始める。
// 曲は残りの待ち時間ごと止めるので、最初のノーツの到着時刻と移動時間、曲の音とノーツの時計はそのまま。
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
            spawnedWhileHeld = manager.AudioChangeHold;
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
    public IEnumerator OutputChangeBeforeStartRemovesOldCountIn() => Verify(false, false);

    [UnityTest]
    public IEnumerator OutputChangeDuringStartRemovesOldStartWord() => Verify(true, false);

    // 読み直したあとの 3・2・1 の途中でまた変わっても、読み直しからやり直す。開始演出は戻らず、止めた位置から始める。
    [UnityTest]
    public IEnumerator OutputChangingAgainDuringTheResumeCountKeepsThePlace() => Verify(true, true);

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
        Assert.True(manager.AudioChangeHold);
        Assert.True(manager.songPlayer.IsPaused);
        double heldSongTime = manager.songPlayer.SongTime;
        Assert.Less(heldSongTime, 0.0, "曲が鳴る前(開幕の待ち時間の途中)で止めた");
        double observeUntil = Time.realtimeSinceStartupAsDouble + .18;
        while (Time.realtimeSinceStartupAsDouble < observeUntil) yield return null;
        bool oldStartShown = countdown != null && countdown.StartWord.Root.gameObject.activeSelf;
        Debug.Log("[OPENING_AUDIO_RECOVERY] phase=" + (changeAgain ? "again" : afterStart ? "start" : "count")
            + " oldExists=" + (countdown != null) + " oldStartShown=" + oldStartShown
            + " recoveryHeadline=" + manager.TrackingView.HeadlineShown
            + " gateTinted=" + GateTinted(gate) + " countInSounds=" + CountInSounds());
        if (!changeAgain) UICatalogPlayTests.Capture(afterStart ? "audio-recovery-during-start" : "audio-recovery-before-start");
        Assert.IsTrue(countdown == null, "出力先変更で曲を止めたら、古い開始演出と予約効果音を取り除く");
        Assert.AreEqual(GamePlayManager.AudioReloadingText, manager.TrackingView.HeadlineShown, "止めている間は読み直しの知らせだけ");
        Assert.False(GateTinted(gate), "ゲートの辺と四隅は元の見た目に戻す");
        var floor = manager.noteSpawner.FloorGuide;
        if (floor != null) Assert.AreEqual(0f, floor.CountTintAmount, "床の判定線も白に戻す");
        Assert.Zero(CountInSounds(), "開始演出のクリック音を鳴らさない");
        Assert.AreEqual(heldSongTime, manager.songPlayer.SongTime, 1e-9);
        if (changeAgain)
        {
            // 読み直して 3・2・1 を数え始めたところで、もう一度変わる。
            string three = PlayTrackingStatus.ResumeText(PlayTrackingWatch.ResumeCountdownSeconds);
            deadline = Time.realtimeSinceStartupAsDouble + 10;
            while (manager.TrackingView.HeadlineShown != three && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.AreEqual(three, manager.TrackingView.HeadlineShown, "読み直して数え始める");
            int firstThreeCueCount = manager.TrackingView.CueCount;
            AudioOutputRecovery.SimulateChangeForTest();
            yield return null; yield return null;
            Assert.True(manager.AudioChangeHold);
            Assert.AreEqual(GamePlayManager.AudioReloadingText, manager.TrackingView.HeadlineShown, "読み直しからやり直す");
            Assert.AreEqual(heldSongTime, manager.songPlayer.SongTime, 1e-9, "もう一度変わっても止めた位置のまま");
            Assert.IsNull(Object.FindFirstObjectByType<GameStartCountdown>(), "開始演出を作り直さない");
            UICatalogPlayTests.Capture("audio-recovery-changed-again");
            deadline = Time.realtimeSinceStartupAsDouble + 10;
            while (manager.TrackingView.HeadlineShown != three && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.AreEqual(three, manager.TrackingView.HeadlineShown, "2回目の読み直し後も3から数える");
            Assert.AreEqual(firstThreeCueCount + 1, manager.TrackingView.CueCount, "数え直した最初の3にも合図の音が一度鳴る");
        }
        deadline = Time.realtimeSinceStartupAsDouble + 15;
        while (manager.AudioChangeHold && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        Assert.False(manager.AudioChangeHold, "音を読み直して再開する");
        Assert.False(manager.songPlayer.IsPaused);
        Assert.Greater(manager.songPlayer.Clip.samples, 0);
        Assert.AreEqual("Game", SceneManager.GetActiveScene().name);
        Assert.AreEqual(runId, GameSession.AchievementRunId, "同じ回を続ける");
        // DSP時刻の差を再計算すると停止位置を数ulp下回ることがある。1µsだけ丸め誤差を許す。
        Assert.That(manager.songPlayer.SongTime, Is.InRange(heldSongTime - .000001, heldSongTime + .3));
        Assert.AreEqual(oldFirstHit, manager.noteSpawner.CurrentChart.notes[0].time, "譜面の到着時刻を変えない");
        Assert.IsNull(Object.FindFirstObjectByType<GameStartCountdown>());
        // 残りの待ち時間のあと、最初のノーツは設定どおりの移動時間で来て、曲の音とノーツの時計がそろう。
        deadline = Time.realtimeSinceStartupAsDouble + 5;
        while (firstNote == null && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        Assert.NotNull(firstNote, "再開したあと最初のノーツが流れてくる");
        Assert.False(spawnedWhileHeld, "止めている間はノーツを出さない");
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
