using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

// 実譜面を本編で読み、検査する時刻は譜面と実行時の移動時間から決める。
// 結果画面には進めず、失敗時もUnityTearDownで片付ける。
public class WindNotePlayTests
{
    const string SongId = "Aobasou";
    readonly Dictionary<FieldInfo, object> sessionValues = new Dictionary<FieldInfo, object>();
    GamePlayManager manager;
    Action<CuttableNote> spawnObserver;

    [SetUp]
    public void SetUp()
    {
        sessionValues.Clear();
        foreach (var field in typeof(GameSession).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (!field.IsLiteral && !field.IsInitOnly)
                sessionValues[field] = field.GetValue(null);
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        if (manager != null)
        {
            manager.enabled = false;
            if (manager.noteSpawner != null && spawnObserver != null)
                manager.noteSpawner.OnNoteSpawned -= spawnObserver;
            if (manager.songPlayer != null) manager.songPlayer.Stop();
        }
        if (SceneManager.GetActiveScene().name == "Game")
            yield return SceneManager.LoadSceneAsync("Title", LoadSceneMode.Single);
        foreach (var pair in sessionValues) pair.Key.SetValue(null, pair.Value);
        spawnObserver = null;
        manager = null;
    }

    [UnityTest]
    public IEnumerator WindChartSpawnsOffsetLandsBeforeJudgmentAndReusesCleanly()
    {
        string path = Path.Combine(Application.streamingAssetsPath, "Songs", SongId, "chart_w.json");
        Assert.IsTrue(File.Exists(path), "chart_w.jsonは必須。通常譜面へのフォールバックで通さない");

        if (InputPoint.Instance != null)
        {
            Object.Destroy(InputPoint.Instance.gameObject);
            yield return null;
        }

        GameSession.SelectedSongId = SongId;
        GameSession.SelectedDifficulty = "W";
        GameSession.IsCalibrationMode = false;
        GameSession.TutorialPending = false;
        // 保存された速度に依存せず、風の区間を測れる移動時間で開始する。
        GameSession.NoteApproachTime = 1f;
        yield return SceneManager.LoadSceneAsync("Game", LoadSceneMode.Single);

        manager = Object.FindFirstObjectByType<GamePlayManager>();
        Assert.NotNull(manager);
        double deadline = Time.realtimeSinceStartupAsDouble + 30;
        while ((manager.songPlayer == null || !manager.songPlayer.IsScheduled
            || manager.noteSpawner.CurrentChart == null)
            && Time.realtimeSinceStartupAsDouble < deadline)
            yield return null;

        Assert.IsTrue(manager.songPlayer.IsScheduled);
        var spawner = manager.noteSpawner;
        var chart = spawner.CurrentChart;
        Assert.NotNull(chart);
        Assert.IsTrue(spawner.HasWindNotes);
        Assert.IsFalse(manager.scoreManager.AimScoring);
        Assert.IsNull(manager.AimMode);
        Assert.IsNull(manager.MemoryMode);
        Assert.Greater(spawner.approachTime, WindRules.CalmSeconds);

        Assert.IsNull(GameObject.Find("FirstHandHint"), "裏譜面は始まりに決まりの一言も手の予告も出さない");

        var tempo = new ChartTempoMap(chart.bpm, chart.beatZeroMs, chart.editorTempoMap);
        double current = manager.songPlayer.SongTime;
        var source = chart.notes.FirstOrDefault(data =>
        {
            double spawnAt = spawner.EffectiveTime(data) - spawner.approachTime;
            if (!data.HasWind || spawnAt <= current + .1) return false;
            double beat = tempo.BeatAt((spawnAt - spawner.TotalOffsetSeconds) * 1000);
            return WindRules.Offset(data.windX, data.windY, data.windSpin, data.windSway,
                spawner.approachTime, spawner.approachTime, beat).sqrMagnitude > .04f;
        });
        Assert.NotNull(source, "出現時のずれを十分に測れる、これから来るノーツが必要");

        double hit = spawner.EffectiveTime(source);
        Vector2 landing = new Vector2(source.x, source.y) * chart.coordScale;
        CuttableNote target = null;
        uint version = 0;
        Vector2 spawnPosition = Vector2.zero;
        Vector2 spawnGhost = Vector2.zero;
        Vector2 spawnApproach = Vector2.zero;
        double spawnRemaining = 0;
        spawnObserver = note =>
        {
            if (target != null || Math.Abs(note.HitTime - hit) > 1e-6
                || !note.LandingPosition.Equals(landing)
                || note.RequiredHand != SaberHandHelper.FromColor(source.color))
                return;
            target = note;
            version = note.SpawnVersion;
            spawnPosition = note.VisualTransform.position;
            spawnRemaining = note.HitTime - manager.songPlayer.SongTime;
            if (note.TimingCue != null)
            {
                spawnGhost = note.TimingCue.GhostRoot.transform.position;
                spawnApproach = note.TimingCue.ApproachRoot.transform.position;
            }
        };
        spawner.OnNoteSpawned += spawnObserver;

        double spawnTime = hit - spawner.approachTime;
        deadline = Time.realtimeSinceStartupAsDouble
            + Math.Max(0, spawnTime - manager.songPlayer.SongTime) + 20;
        while (target == null && Time.realtimeSinceStartupAsDouble < deadline)
            yield return null;

        Assert.NotNull(target);
        Assert.NotNull(target.TimingCue);
        Assert.Greater(spawnRemaining, WindRules.CalmSeconds, "処理落ちで風の区間を飛び越えていない");
        Assert.Greater((spawnPosition - landing).sqrMagnitude, 1e-6f,
            "OnNoteSpawnedの時点から着地点とは異なる位置にいる");
        Assert.AreEqual(landing, spawnGhost);
        Assert.AreEqual(landing, spawnApproach);

        double movingRemaining = Math.Min(.6,
            (spawner.approachTime + WindRules.CalmSeconds) * .5);
        yield return WaitForSong(hit - movingRemaining);
        Assert.AreEqual(version, target.SpawnVersion);
        Assert.Greater(hit - manager.songPlayer.SongTime, WindRules.CalmSeconds);
        AssertGhosts(target);

        double insideWindow = hit + spawner.JudgeLatencySeconds - spawner.earlyJudgeWindow * .5;
        yield return WaitForSong(insideWindow);
        Assert.AreEqual(version, target.SpawnVersion);
        Assert.IsFalse(target.IsCut);
        Assert.IsTrue(target.IsJudgeable, "実際の判定窓の中で検査する");
        double judgeRemaining = hit - spawner.JudgeClock(manager.songPlayer.SongTime);
        Assert.That(judgeRemaining,
            Is.InRange(-(double)spawner.LateWindowFor(target), (double)spawner.earlyJudgeWindow));
        Assert.AreEqual(landing.x, target.transform.position.x);
        Assert.AreEqual(landing.y, target.transform.position.y);
        AssertGhosts(target);

        spawner.OnNoteSpawned -= spawnObserver;
        spawnObserver = null;
        manager.enabled = false;
        manager.songPlayer.Stop();
        CheckActualPoolReuse(spawner);
    }

    static void AssertGhosts(CuttableNote note)
    {
        Assert.NotNull(note.TimingCue);
        Assert.AreEqual(note.LandingPosition, (Vector2)note.TimingCue.GhostRoot.transform.position);
        Assert.AreEqual(note.LandingPosition, (Vector2)note.TimingCue.ApproachRoot.transform.position);
    }

    IEnumerator WaitForSong(double target)
    {
        double deadline = Time.realtimeSinceStartupAsDouble
            + Math.Max(0, target - manager.songPlayer.SongTime) + 20;
        while (manager.songPlayer.SongTime < target && Time.realtimeSinceStartupAsDouble < deadline)
            yield return null;
        Assert.GreaterOrEqual(manager.songPlayer.SongTime, target, "曲時計が検査する時刻まで進む");
    }

    static CuttableNote SpawnSingle(NoteSpawner spawner, NoteData data)
    {
        var chart = new ChartData { bpm = 140 };
        chart.notes.Add(data);
        spawner.SetChart(chart);
        spawner.Tick(spawner.EffectiveTime(data) - spawner.approachTime + .05);
        Assert.AreEqual(1, spawner.AliveCount);
        return spawner.LiveNotes[0];
    }

    static void CheckActualPoolReuse(NoteSpawner spawner)
    {
        spawner.reuseNotes = true;
        spawner.SetExtraOffsetSeconds(0);
        var first = SpawnSingle(spawner, new NoteData {
            time = 4000, color = "red", x = .4f, y = -.3f,
            windX = .9f, windY = .3f, windSpin = .7f, windSway = .2f
        });
        Assert.IsTrue(first.HasWind);
        AssertGhosts(first);
        spawner.JudgeLatencySeconds = -.15;
        spawner.Tick(first.HitTime - .2);
        Assert.IsTrue(first.IsJudgeable);
        Assert.AreEqual(first.LandingPosition, (Vector2)first.transform.position);
        Assert.Greater(Mathf.Abs(first.VisualTransform.position.x - first.transform.position.x), .001f);
        spawner.RefreshJudgmentWindows(first.HitTime - WindRules.StillSeconds);
        Assert.AreEqual(Vector3.zero, first.VisualTransform.localPosition);
        // 揺れたまま回収しても、次の通常ノーツへ持ち越さない。
        spawner.Tick(first.HitTime - .12);
        Assert.AreNotEqual(Vector3.zero, first.VisualTransform.localPosition);
        int created = spawner.CreatedNoteCount;

        var ordinary = SpawnSingle(spawner, new NoteData {
            time = 8000, color = "red", x = -.7f, y = .2f
        });
        Assert.AreSame(first, ordinary, "同じプールの本体を再利用する");
        Assert.AreEqual(created, spawner.CreatedNoteCount);
        Assert.IsFalse(ordinary.HasWind);
        Assert.AreEqual(0f, ordinary.WindX);
        Assert.AreEqual(0f, ordinary.WindY);
        Assert.AreEqual(0f, ordinary.WindSpin);
        Assert.AreEqual(0f, ordinary.WindSway);
        Assert.AreEqual(new Vector2(-.7f, .2f), ordinary.LandingPosition);
        Assert.AreEqual(ordinary.LandingPosition, (Vector2)ordinary.transform.position);
        Assert.AreEqual(Vector3.zero, ordinary.VisualTransform.localPosition);
        Assert.AreEqual(ordinary.transform.position, ordinary.VisualTransform.position);
        Assert.IsTrue(ordinary.BodyRenderer.enabled);
        AssertGhosts(ordinary);

        var windyAgain = SpawnSingle(spawner, new NoteData {
            time = 12000, color = "red", x = .9f, y = -.1f,
            windX = -.8f, windY = .4f, windSpin = -.5f, windSway = .1f
        });
        Assert.AreSame(first, windyAgain);
        Assert.AreEqual(created, spawner.CreatedNoteCount);
        Assert.IsTrue(windyAgain.HasWind);
        Assert.AreEqual(new Vector2(.9f, -.1f), windyAgain.LandingPosition);
        Assert.AreNotEqual(windyAgain.LandingPosition, (Vector2)windyAgain.transform.position);
        AssertGhosts(windyAgain);
    }
}
