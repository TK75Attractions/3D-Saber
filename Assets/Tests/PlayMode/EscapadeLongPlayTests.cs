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

// 本編のLを読み、実際のSaberCutJudgeで追跡・連続切断・曲末の残数確定を通す。
public class EscapadeLongPlayTests
{
    readonly Dictionary<FieldInfo, object> sessionValues = new Dictionary<FieldInfo, object>();
    GamePlayManager manager;
    GameObject saberObject;

    [SetUp]
    public void SetUp()
    {
        sessionValues.Clear();
        foreach (var field in typeof(GameSession).GetFields(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            if (!field.IsLiteral && !field.IsInitOnly) sessionValues[field] = field.GetValue(null);
        // 実際の保存値を変更せず、譜面の5.0〜107.85秒を検査する。
        GameSession.SetPlayerSettingsForTest(true);
        GameSession.JudgmentOffsetMs = 0;
        GameSession.NoteApproachTime = 1f;
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        if (manager != null)
        {
            manager.enabled = false;
            if (manager.songPlayer != null) manager.songPlayer.Stop();
        }
        if (saberObject != null) Object.Destroy(saberObject);
        if (SceneManager.GetActiveScene().name == "Game")
            yield return SceneManager.LoadSceneAsync("Title", LoadSceneMode.Single);
        foreach (var pair in sessionValues) pair.Key.SetValue(null, pair.Value);
        manager = null;
        saberObject = null;
    }

    [UnityTest]
    public IEnumerator LLoadsMovesCanBeCutByEitherHandAndKeepsRemainderUntilSongEnd()
    {
        Assert.IsTrue(File.Exists(Path.Combine(Application.streamingAssetsPath,
            "Songs", "escapade", "chart_l.json")), "先に gen_long.py --write");
        if (InputPoint.Instance != null)
        {
            Object.Destroy(InputPoint.Instance.gameObject);
            yield return null;
        }
        GameSession.SelectedSongId = "escapade";
        GameSession.SelectedSongTitle = "escapade";
        GameSession.SelectedDifficulty = "L";
        GameSession.IsCalibrationMode = false;
        GameSession.TutorialPending = false;
        yield return SceneManager.LoadSceneAsync("Game", LoadSceneMode.Single);
        manager = Object.FindFirstObjectByType<GamePlayManager>();
        Assert.NotNull(manager);
        double deadline = Time.realtimeSinceStartupAsDouble + 30;
        while ((manager.songPlayer == null || !manager.songPlayer.IsScheduled
            || manager.noteSpawner == null || manager.noteSpawner.CurrentChart == null
            || ScreenTransition.IsBusy) && Time.realtimeSinceStartupAsDouble < deadline)
            yield return null;
        Assert.NotNull(manager.songPlayer);
        Assert.IsTrue(manager.songPlayer.IsScheduled);
        Assert.IsFalse(ScreenTransition.IsBusy);
        var spawner = manager.noteSpawner;
        Assert.NotNull(spawner);
        Assert.IsTrue(spawner.HasSlideNotes);
        Assert.IsFalse(spawner.HasFall);
        Assert.IsNull(GameObject.Find("FirstHandHint"), "裏譜面の始まりに文字を出さない");
        var chart = spawner.CurrentChart;
        Assert.AreEqual(250, chart.notes.Count, "本編の切り詰めでも追加ロングを落とさない");
        var source = chart.notes.Single(n => n.slide);
        Assert.AreSame(source, chart.notes[0]);
        Assert.AreEqual(1000, source.count);
        Assert.AreEqual("gold", source.color);

        foreach (var judge in Object.FindObjectsByType<SaberCutJudge>(FindObjectsSortMode.None))
            judge.enabled = false;
        manager.cameraImuJudgment = null;
        manager.enabled = false;
        manager.songPlayer.Stop();

        // 読込済みの本編データ・プレハブを使い、曲時計だけを手動で進める。
        spawner.SetChart(chart);
        spawner.JudgeLatencySeconds = 0;
        spawner.ShouldDeferMiss = null;
        spawner.HoldMiss = null;
        manager.scoreManager.Reset();
        manager.scoreManager.Bind(spawner);
        double hit = spawner.EffectiveTime(source);
        double end = hit + source.lengthMs / 1000.0;
        Assert.AreEqual(5, hit, .0001);
        Assert.AreEqual(107.85, end, .0001);
        spawner.Tick(hit - .5);
        var target = spawner.LiveNotes.Single(n => n.HasSlidePath);
        uint version = target.SpawnVersion;
        var start = target.LandingPosition;
        Assert.AreEqual("1000", target.countLabel.text);
        Assert.AreEqual(SaberHand.Any, target.RequiredHand);
        Assert.IsTrue(target.IsGold);
        Assert.AreEqual(spawner.notePrefab.transform.localScale.z, target.transform.localScale.z, 1e-5f);
        spawner.Tick(hit);
        Assert.AreEqual(start, target.LandingPosition);

        // 複数の折れ点を実際の生成物で確認する。数字・着地枠も追従する。
        foreach (var point in source.slidePath.Take(8))
        {
            spawner.Tick(hit + point.ms / 1000.0);
            var expected = new Vector2(point.x, point.y) * chart.coordScale;
            Assert.Less(Vector2.Distance(expected, target.LandingPosition), .001f);
            Assert.Less(Vector2.Distance(expected, target.transform.position), .001f);
            Assert.Less(Vector2.Distance(expected, target.TimingCue.GhostRoot.transform.position), .001f);
            Assert.AreEqual(target.transform.position + LongNoteCountStyle.WorldOffset, target.countLabel.transform.position);
        }
        Assert.Greater(Vector2.Distance(start, target.LandingPosition), .1f);

        saberObject = new GameObject("LongSlideSaber");
        var tracker = saberObject.AddComponent<SaberTracker>();
        tracker.enabled = false;
        var testJudge = saberObject.AddComponent<SaberCutJudge>();
        testJudge.autonomous = false;
        testJudge.saber = tracker;
        const int cuts = 24;
        double firstCut = hit + source.slidePath[7].ms / 1000.0 + .2;
        for (int cut = 0; cut < cuts; cut++)
        {
            double now = firstCut + (end - .2 - firstCut) * cut / (cuts - 1);
            spawner.RefreshJudgmentWindows(now);
            spawner.Tick(now);
            Assert.AreEqual(version, target.SpawnVersion);
            Assert.IsTrue(target.IsJudgeable);
            Assert.IsFalse(target.IsMissed);
            // 接触試験はこの1本を対象にする。混合元との離隔はEditModeで線分全体を検査する。
            foreach (var other in spawner.LiveNotes)
                if (other != target) other.IsJudgeable = false;
            testJudge.hand = cut % 2 == 0 ? SaberHand.Left : SaberHand.Right;
            Vector3 center = target.transform.position;
            tracker.ResetTo(center - Vector3.up * 2);
            for (int step = 1; step <= 8; step++)
            {
                tracker.Tick(center + Vector3.up * (-2 + step * .5f), .025f);
                testJudge.RunJudge(now);
            }
            Assert.AreEqual(cut + 1, target.CutsAchieved, "左右どちらでも現在位置で1振り1カット");
            Assert.AreEqual(testJudge.hand, target.LastCutterHand);
            Assert.AreEqual((1000 - cut - 1).ToString(), target.countLabel.text);
            yield return null;
        }

        spawner.Tick(end - .001);
        Assert.AreEqual(version, target.SpawnVersion);
        Assert.IsTrue(target.gameObject.activeInHierarchy);
        Assert.IsTrue(target.IsJudgeable);
        Assert.IsFalse(target.IsMissed);
        Assert.AreEqual("976", target.countLabel.text);
        int misses = manager.scoreManager.MissCount;
        spawner.Tick(end + .001);
        Assert.IsTrue(target.IsMissed);
        Assert.IsFalse(target.IsJudgeable);
        Assert.AreEqual(misses + 976, manager.scoreManager.MissCount);
        spawner.Tick(end + .002);
        Assert.AreEqual(misses + 976, manager.scoreManager.MissCount);

        // 同じプールの1000回ロングへ戻しても経路・位置・薄いZを持ち越さない。
        var plain = JsonUtility.FromJson<NoteData>(JsonUtility.ToJson(source));
        plain.slide = false;
        plain.slidePath.Clear();
        spawner.SetChart(new ChartData
        {
            bpm = chart.bpm, coordScale = chart.coordScale, offsetMs = chart.offsetMs,
            notes = new List<NoteData> { plain }
        });
        spawner.Tick(hit);
        var reused = spawner.LiveNotes.Single();
        Assert.AreSame(target, reused);
        Assert.IsFalse(reused.HasSlide);
        Assert.IsFalse(reused.HasSlidePath);
        Assert.AreEqual(start, reused.LandingPosition);
        spawner.Tick(hit + 2);
        Assert.AreEqual(start, (Vector2)reused.transform.position);
        Assert.Greater(reused.transform.localScale.z, spawner.notePrefab.transform.localScale.z);
        Assert.AreEqual("1000", reused.countLabel.text);
    }
}
