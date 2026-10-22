using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

// 2026-10-22 から、向きの合わない振りではフリックを切らない(1段下げはやめた)。
// その振りでは判定もミスも出ず、ノーツは残って、向きの合う次の振りの時刻だけで採点される。
public class DirectionFlickScoreTests
{
    GameObject root;
    NoteSpawner spawner;
    ScoreManager score;
    CuttableNote note;
    readonly List<JudgmentTier> judgments = new List<JudgmentTier>();
    static float Tolerance => Mathf.Cos(SaberCutJudge.DefaultDirectionToleranceDegrees * Mathf.Deg2Rad);

    [SetUp]
    public void Setup()
    {
        root = new GameObject("DirectionFlickScore");
        spawner = root.AddComponent<NoteSpawner>();
        spawner.noteRoot = root.transform;
        spawner.buildTimingCues = false;
        spawner.simultaneousGuideEnabled = false;
        var prefab = new GameObject("NotePrefab");
        prefab.transform.SetParent(root.transform);
        prefab.AddComponent<CuttableNote>().shatterDebrisCount = 0;
        prefab.SetActive(false);
        spawner.notePrefab = prefab;
        score = root.AddComponent<ScoreManager>();
        score.Bind(spawner);
        score.OnJudgment += (tier, _) => judgments.Add(tier);
        spawner.SetChart(new ChartData { notes = new List<NoteData> {
            new NoteData { time = 1000, color = "red", direction = "right" } } });
        spawner.Tick(1);
        note = spawner.LiveNotes[0];
    }

    [TearDown]
    public void Cleanup()
    {
        if (root != null) Object.DestroyImmediate(root);
        judgments.Clear();
    }

    bool Swing(Vector2 direction, double offsetSeconds = 0) =>
        note.CutAtSongTime(note.transform.position, new Vector3(direction.x, direction.y, 0f).normalized * 8,
            CutDirection.None, SaberHand.Right, note.HitTime + offsetSeconds, Tolerance);

    [TestCase(0f, 1f)]    // 真上(90°)
    [TestCase(-1f, -.2f)] // ほぼ逆向き
    public void WrongWaySwingNeitherCutsNorMisses(float x, float y)
    {
        Assert.False(Swing(new Vector2(x, y)));
        Assert.False(note.IsCut); Assert.False(note.IsMissed);
        Assert.AreEqual(1, note.RemainingCuts);
        Assert.IsEmpty(judgments, "向きの合わない振りでは判定もミスも出さない");
        Assert.AreEqual(0, score.CutCount);
        Assert.AreEqual(0, score.MissCount);
    }

    [Test]
    public void TheNextSwingInTheRightDirectionIsScoredOnItsOwnTiming()
    {
        Assert.False(Swing(Vector2.up, -.02));
        Assert.True(Swing(Vector2.right, .02));
        CollectionAssert.AreEqual(new[] { JudgmentTier.Perfect }, judgments, "向きで1段下げない");
        Assert.AreEqual(20, score.LastErrorMs, 1e-6, "採点は切れた振りの時刻");
        Assert.AreEqual(1, score.Combo);
    }

    [TestCase(65f, true)]
    [TestCase(75f, false)]
    public void ToleranceIsSeventyDegrees(float degrees, bool cut)
    {
        float rad = degrees * Mathf.Deg2Rad;
        Assert.AreEqual(cut, Swing(new Vector2(Mathf.Cos(rad), Mathf.Sin(rad)))); // 右から degrees だけ上へ傾けた振り
        CollectionAssert.AreEqual(cut ? new[] { JudgmentTier.Perfect } : new JudgmentTier[0], judgments);
    }
}
