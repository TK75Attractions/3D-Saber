using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

// 実際の生成・カット・時間切れをつなぎ、ロングを1回切るごとに判定1つ・コンボ +1、
// 1回でも残すと残りの回が MISS でコンボが切れることと、判定が重複して出ないことを確認する(2026-10-20 の仕様)。
public class LongNoteScoringIntegrationTests
{
    GameObject root;
    NoteSpawner spawner;
    ScoreManager score;
    CuttableNote note;
    readonly List<CuttableNote> spawnedNotes = new List<CuttableNote>();
    readonly List<JudgmentTier> judgments = new List<JudgmentTier>();
    readonly List<JudgmentTier> longCuts = new List<JudgmentTier>();
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
        score.OnLongCut += (tier, _, __) => longCuts.Add(tier);
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
            spawnedNotes.Add(spawned);
            // 実ゲームでは追従オブジェクトが破棄するラベルを、同期試験では親と一緒に片付ける。
            if (spawned.countLabel != null) spawned.countLabel.transform.SetParent(root.transform, true);
        };
    }

    [TearDown]
    public void TearDown()
    {
        if (root != null) Object.DestroyImmediate(root);
        judgments.Clear();
        longCuts.Clear();
        spawnedNotes.Clear();
        Random.state = randomState;
    }

    [TestCase(10, 0)]
    [TestCase(10, 1)]
    [TestCase(10, 3)]
    [TestCase(10, 9)]
    [TestCase(10, 10)]
    [TestCase(1, 0)]
    public void EachCutIsOneJudgmentAndLeavingOneMakesTheRestMiss(int required, int achieved)
    {
        Spawn(required);
        for (int index = 0; index < achieved; index++)
        {
            note.Cut(Vector3.zero, Vector3.right * 5);
            Assert.AreEqual(index + 1, score.Combo, "1回切るごとにコンボ +1");
            Assert.AreEqual(index + 1, score.PerfectCount);
        }
        if (achieved < required)
        {
            Assert.AreEqual(0, judgments.Count, "途中の回は効果音・光の通知(ノーツの判定)を出さない");
            Assert.AreEqual(achieved, longCuts.Count, "途中の回は表示用の通知だけ");
        }
        double afterDeadline = note.HitTime + spawner.LateWindowFor(note) + spawner.missGrace + .01;
        spawner.Tick(afterDeadline);
        AssertOutcome(required, achieved);
        // 同じ時刻の再評価や、後方へ流して回収する処理でも追加判定を出さない。
        spawner.Tick(afterDeadline);
        spawner.Tick(afterDeadline + 1);
        AssertOutcome(required, achieved);
        Assert.AreEqual(0, spawner.AliveCount);
    }

    [Test]
    public void WrongDirectionLowersOnlyThatCut()
    {
        Spawn(3);
        note.RequiredDirection = CutDirection.Right;
        // 真逆の拒否とは別に、90度違う受理可能なカットの降格を確認する。
        note.Cut(Vector3.zero, Vector3.right * 5);
        note.Cut(Vector3.zero, Vector3.up * 5);
        note.Cut(Vector3.zero, Vector3.right * 5);
        CollectionAssert.AreEqual(new[] { JudgmentTier.Perfect, JudgmentTier.Great }, longCuts);
        CollectionAssert.AreEqual(new[] { JudgmentTier.Perfect }, judgments, "最後の回の向きは合っている");
        Assert.AreEqual(2, score.PerfectCount); Assert.AreEqual(1, score.GreatCount);
        Assert.AreEqual(3, score.Combo, "Great はコンボを切らない");
        Assert.AreEqual(90000 * 800 / 900, score.Score);
    }

    [Test]
    public void GoldLongCutsAreWorthTwiceAndTheFullChartIsExactlyOneHundredThousand()
    {
        spawner.SetChart(new ChartData
        {
            bpm = 120,
            notes = new List<NoteData>
            {
                new NoteData { time = 1000, count = 2, color = "gold", type = "tap", direction = "none" },
                new NoteData { time = 1000, count = 2, color = "red", x = 1, type = "tap", direction = "none" },
            }
        });
        spawner.Tick(1);
        Assert.AreEqual(2, spawnedNotes.Count);
        var gold = spawnedNotes.Find(n => n.IsGold);
        var normal = spawnedNotes.Find(n => !n.IsGold);
        // 重みは 金 2×2 回 + 普通 1×2 回 = 6。
        gold.Cut(Vector3.zero, Vector3.right * 5, CutDirection.None, SaberHand.Any);
        Assert.AreEqual(30000, score.Score, "金の1回 = 90000 × 2/6");
        gold.Cut(Vector3.zero, Vector3.right * 5, CutDirection.None, SaberHand.Any);
        Assert.AreEqual(60000, score.Score);
        normal.Cut(Vector3.zero, Vector3.right * 5, CutDirection.None, SaberHand.Right);
        Assert.AreEqual(75000, score.Score, "普通の1回 = 90000 × 1/6");
        normal.Cut(Vector3.zero, Vector3.right * 5, CutDirection.None, SaberHand.Right);
        Assert.AreEqual(ScoreManager.NoteScore, score.Score);
        Assert.AreEqual(4, score.Combo); Assert.AreEqual(4, score.ComboUnits);
        Assert.True(score.IsAllPerfect);
        Assert.AreEqual(ScoreManager.ComboScore, score.FinalizeScore());
        Assert.AreEqual(ScoreManager.MaxScore, score.Score);
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

    void AssertOutcome(int required, int achieved)
    {
        bool complete = achieved == required;
        CollectionAssert.AreEqual(new[] { complete ? JudgmentTier.Perfect : JudgmentTier.Miss }, judgments,
            "ノーツの終わりの通知は1回だけ(全部切れたら Perfect、残したら MISS)");
        Assert.AreEqual(required, score.HitCount + score.MissCount, "判定はロングの回ごとに1つ");
        Assert.AreEqual(achieved, score.PerfectCount);
        Assert.AreEqual(required - achieved, score.MissCount, "残した回だけ MISS。期限切れの通知で二重に数えない");
        Assert.AreEqual(1, score.JudgedNoteCount);
        Assert.AreEqual(complete ? required : 0, score.Combo, "1回でも残すとコンボが切れる");
        Assert.AreEqual(achieved, score.MaxCombo);
        Assert.AreEqual(90000 * achieved / required, score.Score, "切った回の点は残す");
        Assert.AreEqual(complete ? JudgmentTier.Perfect : JudgmentTier.Miss, score.LastTier);
    }
}
