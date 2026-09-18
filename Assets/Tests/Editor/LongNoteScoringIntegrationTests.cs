using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

// 実際の生成・カット・時間切れをつなぎ、ロング1個から判定が重複して出ないことを確認する。
public class LongNoteScoringIntegrationTests
{
    GameObject root;
    NoteSpawner spawner;
    ScoreManager score;
    CuttableNote note;
    readonly List<JudgmentTier> judgments = new List<JudgmentTier>();
    Random.State randomState;

    [SetUp]
    public void SetUp()
    {
        randomState = Random.state;
        root = new GameObject("LongScoringTest");
        spawner = root.AddComponent<NoteSpawner>();
        score = root.AddComponent<ScoreManager>();
        score.Bind(spawner);
        score.OnJudgment += (tier, _) => judgments.Add(tier);
        spawner.noteRoot = root.transform;
        spawner.buildTimingCues = false;
        spawner.simultaneousGuideEnabled = false;
        spawner.despawnAfterMissSeconds = .2f;
        var prefab = new GameObject("LongScoringPrefab", typeof(CuttableNote));
        prefab.transform.SetParent(root.transform);
        prefab.GetComponent<CuttableNote>().shatterDebrisCount = 0;
        spawner.notePrefab = prefab;
        spawner.OnNoteSpawned += spawned =>
        {
            note = spawned;
            // 実ゲームでは追従オブジェクトが破棄するラベルを、同期試験では親と一緒に片付ける。
            if (spawned.countLabel != null) spawned.countLabel.transform.SetParent(root.transform, true);
        };
    }

    [TearDown]
    public void TearDown()
    {
        if (root != null) Object.DestroyImmediate(root);
        judgments.Clear();
        Random.state = randomState;
    }

    [TestCase(10, 0, JudgmentTier.Miss)]
    [TestCase(10, 1, JudgmentTier.Miss)]
    [TestCase(10, 3, JudgmentTier.Bad)]
    [TestCase(10, 6, JudgmentTier.Good)]
    [TestCase(10, 9, JudgmentTier.Great)]
    [TestCase(10, 10, JudgmentTier.Perfect)]
    [TestCase(1, 0, JudgmentTier.Miss)]
    public void EachNoteProducesOneFinalJudgment(int required, int achieved, JudgmentTier expected)
    {
        Spawn(required);
        for (int index = 0; index < achieved; index++)
            note.Cut(Vector3.zero, Vector3.right * 5);
        if (achieved < required) Assert.AreEqual(0, judgments.Count, "途中カットだけでは最終判定を出さない");
        double afterDeadline = note.HitTime + spawner.LateWindowFor(note) + spawner.missGrace + .01;
        spawner.Tick(afterDeadline);
        AssertOutcome(expected);
        // 同じ時刻の再評価や、後方へ流して回収する処理でも追加判定を出さない。
        spawner.Tick(afterDeadline);
        spawner.Tick(afterDeadline + 1);
        AssertOutcome(expected);
        Assert.AreEqual(0, spawner.AliveCount);
    }

    [Test]
    public void WrongDirectionPartialCompletionStillUsesOneDowngradedJudgment()
    {
        Spawn(10);
        note.RequiredDirection = CutDirection.Right;
        // 真逆の拒否とは別に、90度違う受理可能なカットの降格を確認する。
        for (int index = 0; index < 9; index++) note.Cut(Vector3.zero, Vector3.up * 5);
        Assert.AreEqual(9, note.CutsAchieved);
        spawner.Tick(note.HitTime + spawner.LateWindowFor(note) + spawner.missGrace + .01);
        AssertOutcome(JudgmentTier.Good);
        Assert.True(score.LastWasWrongFlick);
    }

    void Spawn(int required)
    {
        spawner.SetChart(new ChartData
        {
            bpm = 120,
            notes = new List<NoteData> { new NoteData { time = 1000, count = required, type = "tap", direction = "none" } }
        });
        spawner.Tick(1);
        Assert.NotNull(note);
        Assert.True(note.IsJudgeable);
    }

    void AssertOutcome(JudgmentTier expected)
    {
        CollectionAssert.AreEqual(new[] { expected }, judgments, "1ノーツの最終判定は1回だけ");
        Assert.AreEqual(1, score.HitCount + score.MissCount, "結果画面の総判定数を水増ししない");
        Assert.AreEqual(expected == JudgmentTier.Miss ? 1 : 0, score.MissCount);
        Assert.AreEqual(expected == JudgmentTier.Miss ? 0 : JudgmentTierHelper.BasePoints(expected), score.Score);
        int combo = expected == JudgmentTier.Miss || expected == JudgmentTier.Bad ? 0 : 1;
        Assert.AreEqual(combo, score.Combo, "Good以上の達成率判定の直後に架空のMissでコンボを切らない");
        Assert.AreEqual(expected, score.LastTier);
        float expectedAccuracy = JudgmentTierHelper.BasePoints(expected) / (float)JudgmentTierHelper.BasePoints(JudgmentTier.Perfect);
        float resultAccuracy = PlayRankHelper.Accuracy(score.PerfectCount, score.GreatCount,
            score.GoodCount, score.BadCount, score.MissCount);
        Assert.That(resultAccuracy, Is.EqualTo(expectedAccuracy).Within(.0001f), "結果の精度に架空のMISSを含めない");
    }
}
