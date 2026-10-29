using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

// 第4弾の16個。既存の件数・並びのテストは別作業で更新する。
public partial class AchievementCatalogTests
{
    static readonly string[] FourthBatch =
    {
        "holdcut", "seisho", "mochitsuki", "passed", "lastrainbow", "kaibun", "hardfc", "sweep3",
        "setsujoku", "nothorn", "ittoryu", "rising", "slowstart", "scenemaster", "handperfect", "backpick",
    };

    static PlayStats Actions4(params NoteData[] notes)
    {
        var stats = Notes(notes);
        stats.ActionsComplete = true;
        stats.CandidatesObservedUntil = 1000;
        return stats;
    }

    static void Cut4(PlayStats stats, int k, int cut, SaberHand hand, double time, ulong stroke = 1,
        bool sweep = false, Vector2? direction = null, int frame = -1)
    {
        stats.BindGeneration(k, 1);
        stats.AcceptCut(k, cut, 1, hand, time, new SaberCutRecord
        {
            Frame = frame >= 0 ? frame : (int)Math.Round(time * 1000), Sample = cut,
            ObservedAt = time, StrokeId = stroke, Sweep = sweep,
            Direction = direction ?? Vector2.up, DirectionValid = true,
        });
    }

    static void Done4(PlayStats stats, int k, SaberHand hand = SaberHand.Right,
        JudgmentTier tier = JudgmentTier.Perfect, ulong stroke = 1, bool sweep = false, Vector2? direction = null)
    {
        var n = stats.Notes[k];
        if (tier != JudgmentTier.Miss)
            for (int i = n.Accepted.Count; i < n.Cuts; i++)
                Cut4(stats, k, i, hand, n.Time + (n.EndTime - n.Time) * i / Math.Max(1, n.Cuts - 1), stroke, sweep, direction);
        stats.Record(k, tier, null, null, hand, false);
        stats.ResolveAt(k, n.EndTime + (tier == JudgmentTier.Miss ? .2 : 0));
    }

    static AchievementContext Context4(PlayStats stats) => Ctx(c => c.Stats = stats);

    static AchievementContext Real4(SecretChart song, string difficulty)
    {
        var c = Real3(song, difficulty);
        c.Stats.PlayStartSeconds = Math.Max(0, c.Stats.Stage?.playStartSeconds ?? 0);
        c.Stats.PlayEndSeconds = c.Stats.Stage != null && c.Stats.Stage.musicEndSeconds > 0
            ? c.Stats.Stage.musicEndSeconds : c.Stats.Notes.Max(n => n.EndTime) + 1;
        return c;
    }

    [Test]
    public void FourthBatchIdsNamesAndTiers()
    {
        Assert.AreEqual(16, FourthBatch.Length);
        CollectionAssert.AllItemsAreUnique(FourthBatch);
        foreach (string id in FourthBatch)
        {
            var d = AchievementCatalog.Find(id);
            Assert.NotNull(d, id);
            Assert.IsTrue(AchievementStore.IsValidId(id), id);
            Assert.LessOrEqual(id.Length, 12);
            Assert.LessOrEqual(d.Text.Length, 20, id);
            Assert.LessOrEqual(d.Name.Length, 10, id);
            var tier = id == "passed" || id == "setsujoku" ? AchievementTier.None
                : id == "slowstart" ? AchievementTier.Silver
                : new[] { "lastrainbow", "kaibun", "hardfc", "handperfect" }.Contains(id) ? AchievementTier.Gold
                : AchievementTier.Bronze;
            Assert.AreEqual(tier, d.Tier, id);
        }
        Assert.IsNull(AchievementCatalog.Find("bingo"));
        Assert.IsNull(AchievementCatalog.Find("seeno"));
        Assert.AreEqual(AchievementTier.Gold, AchievementCatalog.Find("quiz").Tier, "2026-10-29 夜の段の見直しで満点答案は金");
    }

    [Test]
    public void HoldcutRequiresThreeDistinctOppositeSuccessesInsideBothIntervals()
    {
        var s = Actions4(PlayStatsTests.Note(1000, "gold", count: 8),
            PlayStatsTests.Note(2000, "blue"), PlayStatsTests.Note(3000, "blue"), PlayStatsTests.Note(4000, "blue", count: 2));
        Done4(s, 0);
        for (int k = 1; k < 4; k++) Done4(s, k, SaberHand.Left, JudgmentTier.Good);
        var c = Context4(s);
        NoteGate3(c, "holdcut");
        Assert.AreEqual(3, s.HeldOppositeNotes(), "ロングも1個");
        s.Notes[3].Result = JudgmentTier.Miss;
        Assert.IsFalse(Has(c, "holdcut"));
        s.Notes[3].Result = JudgmentTier.Perfect;
        s.Notes[0].HandMask = 3;
        Assert.IsFalse(Has(c, "holdcut"), "保持中に持ち替え");
        s.Notes[0].HandMask = 2;
        s.Notes[1].FirstCutTime = s.Notes[0].FirstCutTime + .001;
        Assert.IsFalse(Has(c, "holdcut"), "初打境界");
        s.Notes[1].FirstCutTime = 2;
        s.Notes[1].Time = s.Notes[0].EndTime - .001;
        Assert.IsFalse(Has(c, "holdcut"), "譜面の終端境界");
        s.Notes[1].Time = 2;
        s.ActionsComplete = false;
        Assert.IsFalse(Has(c, "holdcut"));
    }

    [Test]
    public void SeishoUsesOnlyArrivedOtherStationRecordsWithinSixtySeconds()
    {
        var c = Ctx(x => { x.SongId = RushESecretState.EpilogueFall.SongId; x.Namespace = "fes"; });
        var peer = Play3(c, "peer0001", c.EndedUnixMs - 60000, "B");
        peer.diff = RushESecretState.EpilogueFall.Difficulty;
        c.Plays = new[] { peer };
        Assert.IsTrue(Has(c, "seisho"), "表とF");
        peer.at--;
        Assert.IsFalse(Has(c, "seisho"));
        peer.at++;
        foreach (string station in new[] { "A", "", "C" })
        {
            peer.st = station;
            Assert.IsFalse(Has(c, "seisho"));
        }
        peer.st = "B";
        peer.ns = "";
        Assert.IsFalse(Has(c, "seisho"));
        peer.ns = "fes";
        peer.id = c.RunId;
        Assert.IsFalse(Has(c, "seisho"));
        peer.id = "peer0001";
        c.Plays = Array.Empty<RankingPlay>();
        Assert.IsFalse(Has(c, "seisho"), "未着記録を予測しない");
        c.Plays = new[] { peer };
        c.Station = "";
        Assert.IsFalse(Has(c, "seisho"));
        c.Station = "B";
        peer.st = "A";
        Assert.IsTrue(Has(c, "seisho"));
    }

    static PlayStats Mochi4(int count, params SaberHand[] hands)
    {
        var s = Actions4(PlayStatsTests.Note(1000, "gold", count: count));
        for (int i = 0; i < hands.Length; i++) Cut4(s, 0, i, hands[i], 1 + i * .2);
        s.Record(0, hands.Length == count ? JudgmentTier.Perfect : JudgmentTier.Miss);
        return s;
    }

    [Test]
    public void MochitsukiRequiresSixUnambiguousAlternatingCutsAndCompletion()
    {
        var l = SaberHand.Left; var r = SaberHand.Right;
        var s = Mochi4(6, l, r, l, r, l, r);
        var c = Context4(s);
        NoteGate3(c, "mochitsuki");
        s.CandidatesObservedUntil = 2.049;
        Assert.IsFalse(Has(c, "mochitsuki"), "最終打から50msを待つ");
        s.CandidatesObservedUntil = 3;
        s.Candidate(0, 1, l, 2.05, 9000);
        Assert.IsFalse(Has(c, "mochitsuki"), "最終打の50ms内の反対手");
        s = Mochi4(6, l, r, l, r, l, r);
        s.Candidate(0, 1, l, 2.2, 2000);
        Assert.IsFalse(Has(Context4(s), "mochitsuki"), "同一フレーム");
        Assert.IsFalse(Has(Context4(Mochi4(5, l, r, l, r, l)), "mochitsuki"));
        Assert.IsFalse(Has(Context4(Mochi4(6, l, r, r, l, r, l)), "mochitsuki"));
        Assert.IsFalse(Has(Context4(Mochi4(7, l, r, l, r, l, r)), "mochitsuki"), "残りMISS");
        Assert.IsTrue(Has(Context4(Mochi4(8, l, l, r, l, r, l, r, l)), "mochitsuki"), "途中から6打");
        s = Mochi4(6, l, r, l, r, l, r);
        s.Notes[0].Gold = false;
        Assert.IsFalse(Has(Context4(s), "mochitsuki"));
    }

    [Test]
    public void PassedAndQuizUseTheEightyRealPaperNotes()
    {
        var c = Real4(RushESecretState.BenkyouQuiz, "Q");
        var paper = c.Stats.Notes.Where(n => n.Quiz).ToArray();
        Assert.AreEqual(216, c.Stats.Notes.Count);
        Assert.AreEqual(80, paper.Length);
        Assert.AreEqual(192, c.Stats.Chart.quiz.decoys.Count);
        Assert.AreEqual(0, paper.Count(n => n.Long || c.Stats.Chart.notes[n.Index].hidden));
        Assert.That(paper.First().Time, Is.EqualTo(32.424).Within(.001));
        Assert.That(paper.Last().Time, Is.EqualTo(135.912094).Within(.001));
        foreach (var n in paper) n.Result = JudgmentTier.Miss;
        foreach (var n in paper.Take(48)) n.Result = JudgmentTier.Good;
        NoteGate3(c, "passed");
        Assert.IsFalse(Has(c, "quiz"));
        paper[47].Result = JudgmentTier.Bad;
        Assert.IsFalse(Has(c, "passed"));
        foreach (var n in paper) n.Result = JudgmentTier.Great;
        Assert.IsTrue(Has(c, "passed"));
        Assert.IsTrue(Has(c, "quiz"));
        paper[0].Result = JudgmentTier.Bad;
        Assert.IsFalse(Has(c, "quiz"), "BADは満点でない");
        var chart = PlayStatsTests.Chart(PlayStatsTests.Note(1000), PlayStatsTests.Note(1999), PlayStatsTests.Note(2000));
        chart.quiz = new QuizChartData { papers = new List<QuizPaperData> { new QuizPaperData { startMs = 1000, endMs = 2000 } } };
        c.Stats = PlayStatsTests.AllResults(PlayStatsTests.Stats(chart), JudgmentTier.Perfect);
        Assert.AreEqual(2, c.Stats.Notes.Count(n => n.Quiz));
        chart.quiz.papers.Clear();
        c.Stats = PlayStatsTests.Stats(chart);
        Assert.IsFalse(Has(c, "passed"));
    }

    [Test]
    public void LastRainbowIsCausedByTheFinalHitAndSurvivesTheWholeFinalGroup()
    {
        var s = Actions4(PlayStatsTests.Note(1000), PlayStatsTests.Note(2000, count: 2), PlayStatsTests.Note(2700, "blue"));
        Done4(s, 0);
        Cut4(s, 1, 0, SaberHand.Right, 2);
        s.VoltageJudgment(1, 1, 3, 4, 3);
        Done4(s, 1);
        Done4(s, 2, SaberHand.Left);
        var c = Context4(s);
        NoteGate3(c, "lastrainbow");
        s.Notes[2].Result = JudgmentTier.Miss;
        Assert.IsFalse(Has(c, "lastrainbow"));
        s.Notes[2].Result = JudgmentTier.Perfect;
        s.FinalVoltageLevel = 3;
        Assert.IsFalse(Has(c, "lastrainbow"));
        foreach (int cause in new[] { -1, 0 })
        {
            var other = Actions4(PlayStatsTests.Note(1000), PlayStatsTests.Note(2000, count: 2));
            other.VoltageJudgment(cause, 0, 3, 4, 3);
            Done4(other, 0); Done4(other, 1);
            Assert.IsFalse(Has(Context4(other), "lastrainbow"));
        }
        s = Actions4(PlayStatsTests.Note(1000, count: 2));
        s.VoltageJudgment(0, 0, 3, 4, 3);
        Done4(s, 0);
        Assert.IsFalse(Has(Context4(s), "lastrainbow"), "途中打");
        s = Actions4(PlayStatsTests.Note(1000));
        s.VoltageJudgment(0, 0, 3, 4, 4);
        Done4(s, 0);
        Assert.IsFalse(Has(Context4(s), "lastrainbow"), "以前に虹");
        s.VoltageMax = 4;
        Assert.IsFalse(Has(Context4(s), "lastrainbow"), "見た目だけでは原因を作らない");
    }

    [TestCase(12321, true)]
    [TestCase(99999, false)]
    [TestCase(11111, false)]
    [TestCase(10001, true)]
    [TestCase(9999, false)]
    [TestCase(100000, false)]
    [TestCase(12345, false)]
    public void KaibunUsesFiveDifferentDigitsWithoutPadding(int score, bool expected)
    {
        var c = Ctx(x => { x.Score = score; x.AimMode = true; });
        Assert.AreEqual(expected, Has(c, "kaibun"));
    }

    [Test]
    public void HardfcRequiresOrdinaryHardAndTheExistingFullCombo()
    {
        var c = Ctx(x => { x.Difficulty = "hArD"; x.Bad = x.Miss = 0; });
        Assert.IsTrue(Has(c, "hardfc"));
        c.Hit = 0;
        Assert.IsFalse(Has(c, "hardfc"));
        c.Hit = 10; c.Bad = 1;
        Assert.IsFalse(Has(c, "hardfc"));
        c.Bad = 0; c.Miss = 1;
        Assert.IsFalse(Has(c, "hardfc"));
        c.Miss = 0; c.AimMode = true;
        Assert.IsFalse(Has(c, "hardfc"));
        c.AimMode = false; c.AimDamage = 1;
        Assert.IsFalse(Has(c, "hardfc"));
        c.AimDamage = 0;
        foreach (string difficulty in new[] { "Easy", "Normal", "E", "D" })
        {
            c.Difficulty = difficulty;
            Assert.IsFalse(Has(c, "hardfc"));
        }
    }

    [Test]
    public void SweepUsesThreeJudgmentGroupsAndTheSameExistingStroke()
    {
        var s = Actions4(PlayStatsTests.Note(1000), PlayStatsTests.Note(1100), PlayStatsTests.Note(1200));
        for (int k = 0; k < 3; k++) Done4(s, k, stroke: 7, sweep: k > 0);
        var c = Context4(s);
        NoteGate3(c, "sweep3");
        s.Notes[2].Accepted[0].StrokeId = 8;
        Assert.IsFalse(Has(c, "sweep3"));
        s.Notes[2].Accepted[0].StrokeId = 7;
        s.Notes[2].Accepted[0].Sweep = false;
        Assert.IsFalse(Has(c, "sweep3"));
        s.Notes[2].Accepted[0].Sweep = true;
        s.Notes[1].Result = JudgmentTier.Bad;
        Assert.IsFalse(Has(c, "sweep3"));
        s.Notes[1].Result = JudgmentTier.Perfect;
        s.Notes[1].Long = true;
        Assert.IsFalse(Has(c, "sweep3"), "ロングは連続を切る");
        s.Notes[1].Long = false;
        s.Notes[1].Time = 1.02;
        Assert.IsFalse(Has(c, "sweep3"), "1msの組でなく25msの判定組");
    }

    [Test]
    public void SetsujokuConsumesTheNextGroupInTheSameCell()
    {
        var s = Actions4(Enumerable.Range(0, 6).Select(i => PlayStatsTests.Note(1000 + i * 1000)).ToArray());
        for (int k = 0; k < 6; k++) Done4(s, k, tier: k % 2 == 0 ? JudgmentTier.Miss : JudgmentTier.Perfect);
        var c = Context4(s);
        NoteGate3(c, "setsujoku");
        Assert.AreEqual(3, s.RevengeCount());
        s.Notes[1].FirstCutTime = s.Notes[0].ResolvedTime;
        Assert.IsFalse(Has(c, "setsujoku"), "MISS確定より先の切断で取り返さない");
        s.Notes[1].FirstCutTime = 2;
        s.Notes[3].Result = JudgmentTier.Great;
        Assert.AreEqual(2, s.RevengeCount(), "次の組で消費");
        s.Notes[3].Result = JudgmentTier.Perfect;
        s.Notes[0].Result = JudgmentTier.Bad;
        Assert.AreEqual(2, s.RevengeCount(), "BADを起点にしない");
        s.Notes[0].Result = JudgmentTier.Miss;
        s.Notes[1].X = 4;
        Assert.AreEqual(2, s.RevengeCount(), "別のマス");
        Assert.AreEqual(4, AchievementGrid.Cell(0, 0));
        Assert.AreEqual(8, AchievementGrid.Cell(5.5f, 3));
        Assert.AreEqual(0, AchievementGrid.Cell(-5.5f, -3));
        Assert.AreEqual(4, AchievementGrid.Cell(-5.5f / 3, -1));
        Assert.AreEqual(8, AchievementGrid.Cell(5.5f / 3, 1));
        Assert.AreEqual(-1, AchievementGrid.Cell(float.NaN, 0));
        Assert.AreEqual(-1, AchievementGrid.Cell(5.5001f, 0));
    }

    [Test]
    public void NothornRequiresHalfTheTargetsBothHandsAndEveryDangerWindow()
    {
        var c = Real4(RushESecretState.PhobiaAvoid, "D");
        c.AimMode = true;
        var session = new AimDodgeSession(c.Stats.Chart.aim, c.Stats.OffsetSeconds);
        Assert.AreEqual(50, session.Targets.Count);
        Assert.AreEqual(1189, session.Spikes.Count);
        var a = new PlayStats.AimObservation { Complete = true, Resolved = true, Targets = 50, Spikes = 1189,
            ActiveSeconds = 100, RedSeconds = 90, BlueSeconds = 90 };
        c.Stats.AimObserved = a;
        c.Hit = 25; c.Miss = 25; c.AimDamage = 0;
        Assert.IsTrue(Has(c, "nothorn"));
        c.Hit = 24; c.Miss = 26;
        Assert.IsFalse(Has(c, "nothorn"));
        c.Hit = 25; c.Miss = 25; c.AimDamage = 1;
        Assert.IsFalse(Has(c, "nothorn"));
        c.AimDamage = 0; a.BlueSeconds = 89.999;
        Assert.IsFalse(Has(c, "nothorn"));
        a.BlueSeconds = 90; a.MissingDanger = true;
        Assert.IsFalse(Has(c, "nothorn"));
        a.MissingDanger = false; a.Targets = 0;
        Assert.IsFalse(Has(c, "nothorn"));
        a.Targets = 50; a.Spikes = 0;
        Assert.IsFalse(Has(c, "nothorn"));
        a.Spikes = 1189; a.Resolved = false;
        Assert.IsFalse(Has(c, "nothorn"));
    }

    [Test]
    public void IttoryuCountsNotesAndRejectsOppositeCutsWithinTheWindow()
    {
        var data = Taps(8);
        data[3].color = "gold"; data[3].count = 2;
        var s = Actions4(data);
        for (int k = 0; k < 8; k++) Done4(s, k);
        var c = Context4(s);
        NoteGate3(c, "ittoryu");
        s.Notes[3].HandMask = 3;
        Assert.IsFalse(Has(c, "ittoryu"), "金ロングを両手で切った");
        s.Notes[3].HandMask = 2;
        s.Notes[3].Result = JudgmentTier.Miss;
        Assert.IsFalse(Has(c, "ittoryu"));
        var seven = Actions4(Taps(7));
        seven.Notes[0].Cuts = 10; seven.Notes[0].Long = true;
        for (int k = 0; k < 7; k++) Done4(seven, k);
        Assert.IsFalse(Has(Context4(seven), "ittoryu"), "打鍵数で8個にしない");
        data = Taps(8).Concat(new[] { PlayStatsTests.Note(500, "blue", count: 8) }).ToArray();
        s = Actions4(data);
        for (int k = 0; k < 8; k++) Done4(s, k);
        Done4(s, 8, SaberHand.Left);
        Assert.IsFalse(Has(Context4(s), "ittoryu"), "先に始まった反対手ロングの途中打");
        s = Actions4(Taps(8).Concat(new[] { PlayStatsTests.Note(1000, "blue") }).ToArray());
        for (int k = 0; k < 8; k++) Done4(s, k);
        Done4(s, 8, SaberHand.Left);
        Assert.IsFalse(Has(Context4(s), "ittoryu"), "同時組も全員同じ手");
    }

    [Test]
    public void RisingCountsInitialDirectionsOfFiftySuccessfulNonArrows()
    {
        var s = Actions4(Taps(50));
        var edge = new Vector2(Mathf.Sin(70 * Mathf.Deg2Rad), Mathf.Cos(70 * Mathf.Deg2Rad));
        for (int k = 0; k < 50; k++) Done4(s, k, direction: k < 40 ? edge : Vector2.down);
        var c = Context4(s);
        NoteGate3(c, "rising");
        s.Notes[39].Accepted[0].Direction = new Vector2(Mathf.Sin(70.01f * Mathf.Deg2Rad), Mathf.Cos(70.01f * Mathf.Deg2Rad));
        Assert.IsFalse(Has(c, "rising"));
        s.Notes[39].Accepted[0].Direction = edge;
        s.Notes[0].Arrow = true;
        Assert.IsFalse(Has(c, "rising"), "隠した矢印も対象外");
        s.Notes[0].Arrow = false; s.Notes[0].Result = JudgmentTier.Miss;
        Assert.IsFalse(Has(c, "rising"), "成功49個");
        s.Notes[0].Result = JudgmentTier.Perfect; s.Notes[0].Accepted[0].Direction = Vector2.zero;
        Assert.IsFalse(Has(c, "rising"), "静止入力");
    }

    [Test]
    public void SlowstartExtendsThroughTheTenthNotesWholeGroup()
    {
        var data = Taps(11);
        data[10].time = data[9].time;
        var s = Actions4(data);
        for (int k = 0; k < 11; k++) Done4(s, k, tier: k < 4 || k == 10 ? JudgmentTier.Miss : JudgmentTier.Perfect);
        var c = Context4(s); c.Score = 80000;
        NoteGate3(c, "slowstart");
        c.Score = 79999;
        Assert.IsFalse(Has(c, "slowstart"));
        c.Score = 80000; s.Notes[10].Result = JudgmentTier.Perfect;
        Assert.IsFalse(Has(c, "slowstart"));
        c.Stats = PlayStatsTests.AllResults(Notes(Taps(9)), JudgmentTier.Miss);
        Assert.IsFalse(Has(c, "slowstart"));
        c.Stats = PlayStatsTests.AllResults(Notes(Taps(10)), JudgmentTier.Perfect);
        c.Stats.Notes[0].Long = true; c.Stats.Notes[0].Cuts = 6; c.Stats.Notes[0].Result = JudgmentTier.Miss;
        Assert.IsFalse(Has(c, "slowstart"), "ロングの未達5打を5個にしない");
    }

    [Test]
    public void ScenemasterChecksActualStageEntriesAndRejectsMissingScenes()
    {
        var c = Real4(RushESecretState.EscapadeLong, "Normal");
        var entries = c.Stats.StrongSectionEntries();
        Assert.AreEqual(5, entries.Count);
        var expected = new[] { 10.125262, 25.283158, 50.546316, 65.704211, 85.914734 };
        for (int i = 0; i < expected.Length; i++) Assert.That(c.Stats.Notes[entries[i]].Time, Is.EqualTo(expected[i]).Within(.001));
        NoteGate3(c, "scenemaster");
        c.Stats.Notes[entries[0]].Result = JudgmentTier.Great;
        Assert.IsFalse(Has(c, "scenemaster"));
        c = Real4(RushESecretState.BenkyouQuiz, "Hard");
        Assert.AreEqual(6, c.Stats.StrongSectionEntries().Count);
        Assert.IsTrue(Has(c, "scenemaster"));
        Assert.IsFalse(Has(Real4(RushESecretState.BenkyouQuiz, "Q"), "scenemaster"), "第4入口が1秒を超える");
        Assert.IsFalse(Has(Real4(RushESecretState.LogicSpeed, "Easy"), "scenemaster"));
        Assert.IsFalse(Has(Real4(RushESecretState.LogicSpeed, "Normal"), "scenemaster"));
        Assert.AreEqual(8, Real4(RushESecretState.LogicSpeed, "Hard").Stats.StrongSectionEntries().Count);
        Assert.IsFalse(Has(Real4(RushESecretState.ElDoradoG, "G"), "scenemaster"), "G専用stageの強区間は3つ");
        var stage = new StagePerformanceTimeline { sections = Enumerable.Range(1, 4)
            .Select(i => new StagePerformanceTimeline.Section { startSeconds = i, endSeconds = i + .5, intensity = .65f }).ToArray() };
        var chart = PlayStatsTests.Chart(Enumerable.Range(1, 4).Select(i => PlayStatsTests.Note(i * 1000)).ToArray());
        c.Stats = PlayStatsTests.AllResults(PlayStatsTests.Stats(chart, stage), JudgmentTier.Perfect);
        c.Stats.PlayStartSeconds = 0; c.Stats.PlayEndSeconds = 5;
        Assert.IsTrue(Has(c, "scenemaster"));
        stage.sections[3].startSeconds = 3.01;
        Assert.IsFalse(Has(c, "scenemaster"), "同じ組を再利用しない");
        stage.sections[3].startSeconds = 3;
        Assert.IsFalse(Has(c, "scenemaster"), "同じ開始秒は1区間");
        c.Stats = PlayStatsTests.AllResults(PlayStatsTests.Stats(chart), JudgmentTier.Perfect);
        Assert.IsFalse(Has(c, "scenemaster"), "stageなし");
    }

    [Test]
    public void HandperfectUsesChartColorAndCountsEachLongOnce()
    {
        var s = PlayStatsTests.AllResults(Notes(Taps(20).Concat(new[] {
            PlayStatsTests.Note(12000, "blue"), PlayStatsTests.Note(13000, "gold") }).ToArray()), JudgmentTier.Perfect);
        s.Notes[20].Result = s.Notes[21].Result = JudgmentTier.Miss;
        var c = Context4(s);
        NoteGate3(c, "handperfect");
        s.Notes[0].Result = JudgmentTier.Great;
        Assert.IsFalse(Has(c, "handperfect"));
        s.Notes[0].Result = JudgmentTier.Miss;
        Assert.IsFalse(Has(c, "handperfect"));
        c.Stats = PlayStatsTests.AllResults(Notes(Taps(19)), JudgmentTier.Perfect);
        Assert.IsFalse(Has(c, "handperfect"));
        c.Stats = PlayStatsTests.AllResults(Notes(PlayStatsTests.Note(1000, count: 20)), JudgmentTier.Perfect);
        Assert.IsFalse(Has(c, "handperfect"));
    }

    [Test]
    public void BackpickNeedsTheRunPreparationFlag()
    {
        var c = Ctx(x => x.Backpick = true);
        Assert.IsTrue(Has(c, "backpick"));
        c.Backpick = false;
        Assert.IsFalse(Has(c, "backpick"));
    }

    [Test]
    public void RealChartsContainTheGoldLongAndHoldcutOpportunities()
    {
        var g = Real4(RushESecretState.ElDoradoG, "G");
        var first = g.Stats.Notes.First(n => n.Gold && n.Long);
        Assert.AreEqual(50, first.Cuts);
        Assert.That(first.Time, Is.EqualTo(2.422).Within(.001));
        Assert.That(first.EndTime, Is.EqualTo(12.022).Within(.001));
        var rush = Real4(RushESecretState.RushE, "Normal");
        var hold = rush.Stats.Notes.First(n => n.Gold && n.Long && Math.Abs(n.Time - 124.3519) < .001);
        Assert.That(hold.EndTime, Is.EqualTo(132.0519).Within(.001));
        Assert.GreaterOrEqual(rush.Stats.Notes.Count(n => n.Time > hold.Time + .001 && n.Time < hold.EndTime - .001), 3);
    }
}
