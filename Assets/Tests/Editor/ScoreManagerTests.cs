using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

// 満点 100000 = ノーツ 90000(譜面の重みで配分) + コンボ 10000(最大コンボの割合)。2026-10-20 の仕様。
public class ScoreManagerTests
{
    readonly List<GameObject> created = new List<GameObject>();

    [TearDown]
    public void TearDown()
    {
        foreach (var go in created) if (go != null) Object.DestroyImmediate(go);
        created.Clear();
    }

    // notes 個のタップの譜面を流す採点。notes = 0 なら譜面なし(判定済みの数が満点の分母になる)。
    private ScoreManager Make(int notes = 0)
    {
        var go = new GameObject("score");
        created.Add(go);
        var s = go.AddComponent<ScoreManager>();
        if (notes > 0)
        {
            var owner = new GameObject("spawner");
            created.Add(owner);
            var spawner = owner.AddComponent<NoteSpawner>();
            var chart = new ChartData { bpm = 120, notes = new List<NoteData>() };
            for (int i = 0; i < notes; i++) chart.notes.Add(new NoteData { time = 1000 + i * 500 });
            spawner.SetChart(chart);
            s.Bind(spawner);
        }
        return s;
    }

    [Test]
    public void RegisterHit_Perfect_IncreasesScoreAndCombo()
    {
        var s = Make(4);
        s.RegisterHit(JudgmentTier.Perfect);
        Assert.AreEqual(1, s.HitCount);
        Assert.AreEqual(1, s.Combo);
        Assert.AreEqual(22500, s.Score, "4ノーツの譜面の1つ = 90000 / 4");
        Assert.AreEqual(1, s.MaxCombo);
        Assert.AreEqual(1, s.PerfectCount);
    }

    [Test]
    public void RegisterHit_OnlyNotePointsAccumulateDuringPlay()
    {
        var s = Make(4);
        s.RegisterHit(JudgmentTier.Perfect);
        s.RegisterHit(JudgmentTier.Perfect);
        s.RegisterHit(JudgmentTier.Perfect);
        Assert.AreEqual(67500, s.Score, "コンボ点は曲の終わりまで足さない");
        Assert.AreEqual(7500, s.ComboBonus, "今の最大コンボ 3/4 で終わったときのコンボ点");
        Assert.AreEqual(75000, s.ScoreWithCombo);
        Assert.AreEqual(3, s.Combo);
        Assert.AreEqual(3, s.MaxCombo);
    }

    [Test]
    public void AllPerfectFullComboIsExactlyOneHundredThousand()
    {
        var s = Make(7);
        for (int i = 0; i < 7; i++) s.RegisterHit(JudgmentTier.Perfect);
        Assert.AreEqual(ScoreManager.NoteScore, s.Score);
        Assert.AreEqual(ScoreManager.ComboScore, s.FinalizeScore());
        Assert.AreEqual(ScoreManager.MaxScore, s.Score);
    }

    [Test]
    public void TierRatiosAreKeptAndPointsAreRoundedDown()
    {
        var s = Make(3);
        s.RegisterHit(JudgmentTier.Perfect);
        s.RegisterHit(JudgmentTier.Great);
        s.RegisterHit(JudgmentTier.Good);
        Assert.AreEqual(60000, s.Score, "(300+200+100)/900 × 90000");
        var t = Make(7);
        for (int i = 0; i < 6; i++) t.RegisterHit(JudgmentTier.Perfect);
        t.RegisterHit(JudgmentTier.Great);
        Assert.AreEqual(85714, t.Score, "1 つでも Perfect でなければ 90000 に切り上げない");
    }

    [Test]
    public void FinalizeScore_UsesMaximumDespiteLateMiss_AndAwardsOnce()
    {
        var s = Make(14);
        for (int i = 0; i < 12; i++) s.RegisterHit(JudgmentTier.Perfect);
        s.RegisterMiss();
        s.RegisterHit(JudgmentTier.Good);
        int baseScore = s.Score;
        int judgments = 0;
        s.OnJudgment += (_, __) => judgments++;
        Assert.AreEqual(8571, s.FinalizeScore(), "最大コンボ 12/14 × 10000 の切り捨て");
        Assert.AreEqual(baseScore + 8571, s.Score);
        Assert.AreEqual(0, s.FinalizeScore());
        s.RegisterHit(JudgmentTier.Perfect);
        s.RegisterMiss();
        Assert.AreEqual(baseScore + 8571, s.Score);
        Assert.AreEqual(8571, s.ComboBonus);
        Assert.AreEqual(1, s.Combo);
        Assert.AreEqual(12, s.MaxCombo);
        Assert.AreEqual(0, judgments, "精算や終了後の入力を判定イベントにしない");
    }

    [Test]
    public void FinalizeScore_AllMissAndReplayDoNotKeepOldBonus()
    {
        var s = Make(1);
        s.RegisterMiss();
        Assert.AreEqual(0, s.FinalizeScore());
        Assert.AreEqual(0, s.Score);
        s.Reset();
        Assert.False(s.IsFinalized);
        s.RegisterHit(JudgmentTier.Great);
        Assert.AreEqual(ScoreManager.ComboScore, s.FinalizeScore());
        Assert.AreEqual(60000 + ScoreManager.ComboScore, s.Score);
        s.Reset();
        Assert.AreEqual(0, s.ComboBonus);
        Assert.AreEqual(0, s.FinalizeScore());
    }

    [Test]
    public void WithoutAChartTheJudgedNotesAreTheWholeSong()
    {
        // 練習・ツールなど譜面を渡さないときも 0 除算せず、満点を超えない。
        var s = Make();
        Assert.AreEqual(0, s.Score); Assert.AreEqual(0, s.ComboBonus);
        s.RegisterHit(JudgmentTier.Perfect);
        s.RegisterHit(JudgmentTier.Perfect);
        Assert.AreEqual(ScoreManager.NoteScore, s.Score);
        s.RegisterMiss();
        Assert.AreEqual(60000, s.Score);
        Assert.AreEqual(6666, s.FinalizeScore());
        Assert.AreEqual(66666, s.Score);
    }

    [Test]
    public void ChartUnitsCountGoldTwiceAndEveryCutOfALong()
    {
        var chart = new ChartData { notes = new List<NoteData>
        {
            new NoteData { color = "red" },
            new NoteData { color = "gold" },
            new NoteData { color = "blue", count = 3 },
            new NoteData { color = "Gold", count = 2 },
            null,
        } };
        ScoreManager.CountUnits(chart, out int weight, out int combo);
        Assert.AreEqual(1 + 2 + 3 + 4, weight, "普通1・金2、ロングは回数ぶん(金のロングは1回ごとに2)");
        Assert.AreEqual(1 + 1 + 3 + 2, combo, "満コンボは切る回数の合計");
        ScoreManager.CountUnits(null, out weight, out combo);
        Assert.AreEqual(0, weight); Assert.AreEqual(0, combo);
    }

    [Test]
    public void RegisterHit_GreatGoodBad_AllCountAsHit_ButBadBreaksCombo()
    {
        var s = Make();
        s.RegisterHit(JudgmentTier.Great);
        s.RegisterHit(JudgmentTier.Good);
        s.RegisterHit(JudgmentTier.Bad);
        Assert.AreEqual(3, s.HitCount, "Bad もヒット数には入る");
        Assert.AreEqual(1, s.GreatCount);
        Assert.AreEqual(1, s.GoodCount);
        Assert.AreEqual(1, s.BadCount);
        Assert.AreEqual(0, s.Combo, "Bad でコンボは切れる");
        Assert.AreEqual(2, s.MaxCombo, "MaxCombo は Bad 前の値を維持");
    }

    [Test]
    public void RegisterHit_Bad_StillAddsScore_ButResetsCombo()
    {
        var s = Make(3);
        s.RegisterHit(JudgmentTier.Perfect); // 30000, Combo 1
        s.RegisterHit(JudgmentTier.Perfect); // 60000, Combo 2
        int scoreBeforeBad = s.Score;
        s.RegisterHit(JudgmentTier.Bad);     // Combo 0 でリセット、Bad は Perfect の 50/300
        Assert.AreEqual(0, s.Combo, "Bad でコンボは0に");
        Assert.AreEqual(scoreBeforeBad + 5000, s.Score, "Bad は基礎点の割合(50/300)ぶんを加算");
        Assert.AreEqual(1, s.BadCount);
    }

    [Test]
    public void RegisterHit_GoodKeepsCombo()
    {
        var s = Make();
        s.RegisterHit(JudgmentTier.Perfect);
        s.RegisterHit(JudgmentTier.Good); // 切れない
        s.RegisterHit(JudgmentTier.Perfect);
        Assert.AreEqual(3, s.Combo, "Good ではコンボが継続");
    }

    [Test]
    public void RegisterHit_MissTier_BreaksCombo()
    {
        var s = Make();
        s.RegisterHit(JudgmentTier.Perfect);
        s.RegisterHit(JudgmentTier.Miss);
        Assert.AreEqual(0, s.Combo);
        Assert.AreEqual(1, s.MissCount);
        Assert.AreEqual(1, s.HitCount);
        Assert.AreEqual(2, s.JudgedNoteCount);
    }

    [Test]
    public void RegisterMiss_BreaksComboAndKeepsMaxCombo()
    {
        var s = Make();
        s.RegisterHit(JudgmentTier.Perfect);
        s.RegisterHit(JudgmentTier.Perfect);
        s.RegisterMiss();
        Assert.AreEqual(0, s.Combo);
        Assert.AreEqual(2, s.MaxCombo);
        Assert.AreEqual(1, s.MissCount);
    }

    [Test]
    public void Reset_ClearsAllState()
    {
        var s = Make(2);
        s.RegisterHit(JudgmentTier.Great);
        s.RegisterMiss();
        s.Reset();
        Assert.AreEqual(0, s.Score);
        Assert.AreEqual(0, s.Combo);
        Assert.AreEqual(0, s.MaxCombo);
        Assert.AreEqual(0, s.HitCount);
        Assert.AreEqual(0, s.MissCount);
        Assert.AreEqual(0, s.PerfectCount);
        Assert.AreEqual(0, s.GreatCount);
        Assert.AreEqual(0, s.JudgedNoteCount);
        s.RegisterHit(JudgmentTier.Perfect);
        Assert.AreEqual(45000, s.Score, "前のプレイの点を持ち越さない");
    }

    [Test]
    public void OnJudgment_FiresWithTierAndTheAddedPoints()
    {
        var s = Make(4);
        JudgmentTier received = JudgmentTier.Miss;
        int awarded = -1;
        s.OnJudgment += (t, a) => { received = t; awarded = a; };
        s.RegisterHit(JudgmentTier.Perfect);
        Assert.AreEqual(JudgmentTier.Perfect, received);
        Assert.AreEqual(22500, awarded);
    }
}
