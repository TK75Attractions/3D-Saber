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

// 本編のPを読み、実際の生成・移動・着地と、プールから通常譜面への復帰を確認する。
public class YurikagoOvertakePlayTests
{
    readonly Dictionary<FieldInfo, object> sessionValues = new Dictionary<FieldInfo, object>();
    GamePlayManager manager;
    float oldApproach;
    bool hadApproach;

    [SetUp]
    public void SetUp()
    {
        sessionValues.Clear();
        foreach (var field in typeof(GameSession).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (!field.IsLiteral && !field.IsInitOnly)
                sessionValues[field] = field.GetValue(null);
        hadApproach = PlayerPrefs.HasKey("noteApproachTime");
        oldApproach = GameSession.NoteApproachTime;
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        if (manager != null)
        {
            manager.enabled = false;
            if (manager.songPlayer != null) manager.songPlayer.Stop();
        }
        if (SceneManager.GetActiveScene().name == "Game")
            yield return SceneManager.LoadSceneAsync("Title", LoadSceneMode.Single);
        foreach (var pair in sessionValues) pair.Key.SetValue(null, pair.Value);
        if (hadApproach) GameSession.NoteApproachTime = oldApproach;
        else GameSession.ResetNoteApproachTime();
        manager = null;
    }

    [UnityTest]
    public IEnumerator MainGameLoadsPAndShowsAnOvertakeBeforeBothNotesLandOnTime()
    {
        Assert.IsTrue(File.Exists(Path.Combine(Application.streamingAssetsPath,
            "Songs", "揺籠", "chart_p.json")), "先に生成する");

        if (InputPoint.Instance != null)
        {
            Object.Destroy(InputPoint.Instance.gameObject);
            yield return null;
        }
        GameSession.SelectedSongId = "揺籠";
        GameSession.SelectedSongTitle = "揺籠";
        GameSession.SelectedDifficulty = "P";
        GameSession.IsCalibrationMode = false;
        GameSession.TutorialPending = false;
        GameSession.NoteApproachTime = 1;
        yield return SceneManager.LoadSceneAsync("Game", LoadSceneMode.Single);

        manager = Object.FindFirstObjectByType<GamePlayManager>();
        Assert.NotNull(manager);
        double deadline = Time.realtimeSinceStartupAsDouble + 30;
        while ((manager.songPlayer == null || !manager.songPlayer.IsScheduled
            || manager.noteSpawner == null || manager.noteSpawner.CurrentChart == null)
            && Time.realtimeSinceStartupAsDouble < deadline)
            yield return null;

        Assert.NotNull(manager.songPlayer);
        Assert.IsTrue(manager.songPlayer.IsScheduled);
        var spawner = manager.noteSpawner;
        Assert.NotNull(spawner);
        Assert.IsTrue(spawner.HasOvertakeNotes);
        Assert.IsFalse(spawner.HasScroll);
        Assert.IsFalse(spawner.HasFall);
        Assert.IsFalse(spawner.HasWindNotes);
        Assert.AreEqual(331, spawner.CurrentChart.notes.Count);

        Assert.IsNull(GameObject.Find("FirstHandHint"), "裏譜面は始まりに決まりの一言も手の予告も出さない");

        foreach (var judge in Object.FindObjectsByType<SaberCutJudge>(FindObjectsSortMode.None))
            judge.enabled = false;
        manager.cameraImuJudgment = null;
        manager.enabled = false;
        manager.songPlayer.Stop();

        // 音声フレームの端数に依存せず、本編が読み込んだ譜面とSpawnerへ時計を渡す。
        var chart = spawner.CurrentChart;
        spawner.SetChart(chart);
        var pair = FindPair(spawner);
        var a = chart.notes[pair.a];
        var b = chart.notes[pair.b];
        double ha = spawner.EffectiveTime(a), hb = spawner.EffectiveTime(b);
        int missed = 0;
        Action<CuttableNote> observer = _ => missed++;
        spawner.OnNoteMissed += observer;
        try
        {
            spawner.Tick(spawner.SpawnTimeFor(b) + .001);
            Assert.IsTrue(spawner.LiveNotes.Any(n => Matches(n, b, hb)));
            Assert.IsFalse(spawner.LiveNotes.Any(n => Matches(n, a, ha)),
                "後に切る遅いノーツが先に現れる");

            spawner.Tick(pair.before);
            var fast = spawner.LiveNotes.Single(n => Matches(n, a, ha));
            var slow = spawner.LiveNotes.Single(n => Matches(n, b, hb));
            uint fastVersion = fast.SpawnVersion, slowVersion = slow.SpawnVersion;
            Assert.Greater(fast.transform.position.z, slow.transform.position.z);

            spawner.Tick(pair.after);
            Assert.AreEqual(fastVersion, fast.SpawnVersion);
            Assert.AreEqual(slowVersion, slow.SpawnVersion);
            Assert.Greater(ha - pair.after, .73);
            Assert.Less(fast.transform.position.z, slow.transform.position.z,
                "実際の本体の奥行きが逆転した");
            Assert.AreEqual(1, fast.BodyRenderer.sharedMaterial.GetFloat("_ZWrite"));
            Assert.AreEqual(2450, fast.BodyRenderer.sharedMaterial.renderQueue);

            spawner.Tick(ha - .7);
            Assert.AreEqual(a.x, fast.transform.position.x, 1e-5);
            Assert.AreEqual(a.y, fast.transform.position.y, 1e-5);
            Assert.AreEqual(a.x, fast.TimingCue.GhostRoot.transform.position.x, 1e-5);
            spawner.Tick(ha);
            Assert.Less(Vector3.Distance(new Vector3(a.x, a.y, spawner.judgeZ),
                fast.transform.position), 1e-4);
            spawner.Tick(hb);
            Assert.AreEqual(slowVersion, slow.SpawnVersion);
            Assert.Less(Vector3.Distance(new Vector3(b.x, b.y, spawner.judgeZ),
                slow.transform.position), 1e-4);

            // ミス通知後も後続のノーツを生成する。
            spawner.Tick(hb + spawner.LateWindowFor(slow) + spawner.missGrace + 1);
            Assert.Greater(missed, 0);
            int previous = spawner.NextIndex;
            spawner.Tick(hb + 8);
            Assert.Greater(spawner.NextIndex, previous);
        }
        finally
        {
            spawner.OnNoteMissed -= observer;
        }

        // Pと通常は同じプールを使える。個別の飛来・数字非表示を持ち越さない。
        var plain = new NoteData { time = 10000, x = .5f, y = .2f, color = "red" };
        spawner.SetChart(new ChartData { bpm = 120, notes = new List<NoteData> { plain } });
        double plainHit = spawner.EffectiveTime(plain);
        spawner.Tick(plainHit - .5);
        var normal = spawner.LiveNotes.Single();
        Assert.IsFalse(normal.HasOvertake);
        Assert.IsFalse(spawner.HasOvertakeNotes);
        Assert.AreEqual(.5f, normal.transform.position.x);
        Assert.AreEqual(spawner.judgeZ + spawner.Speed * .5f,
            normal.transform.position.z, 1e-4);
    }

    static bool Matches(CuttableNote note, NoteData source, double hit) =>
        Math.Abs(note.HitTime - hit) < 1e-6
        && Vector2.Distance(note.LandingPosition, new Vector2(source.x, source.y)) < 1e-5;

    static (int a, int b, double before, double after) FindPair(NoteSpawner spawner)
    {
        var notes = spawner.CurrentChart.notes;
        double approach = spawner.OvertakeApproach;
        for (int i = 0; i < notes.Count; i++)
        for (int j = i + 1; j < notes.Count; j++)
        {
            var a = notes[i];
            var b = notes[j];
            double ha = spawner.EffectiveTime(a), hb = spawner.EffectiveTime(b);
            if (hb <= ha || hb - ha > 4) continue;
            double sa = spawner.SpawnTimeFor(a), sb = spawner.SpawnTimeFor(b);
            if (sb >= sa - .1) continue;
            float fa = OvertakeRules.FlightScale(a.approachScale, approach);
            float fb = OvertakeRules.FlightScale(b.approachScale, approach);
            if (fb <= fa) continue;
            double crossing = (fb * (ha - .73) - fa * (hb - .73)) / (fb - fa);
            double margin = Math.Min(.12, Math.Min(crossing - sa - .04, ha - .73 - crossing));
            if (margin < .06) continue;
            return (i, j, crossing - margin, crossing + margin);
        }
        Assert.Fail("本編のPに、共通区間より奥の追い越しがない");
        return default;
    }
}
