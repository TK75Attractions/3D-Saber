using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

// Phobia PARTⅠ の裏譜面「避」(2026-10-24): 剣が2つの照準になり、トゲをよけて的(ダーツ)を狙う。
// 点は 50000 から始まり、トゲで 1000 減り、的は全部で最大 50000(満点 100000)。
public class PhobiaAvoidTests
{
    const string SongId = "Phobia PARTⅠ";

    [Test]
    public void TenJacketCutsOnPhobiaRevealTheAvoidChart()
    {
        var secret = RushESecretState.For(SongId);
        Assert.AreSame(RushESecretState.PhobiaAvoid, secret);
        Assert.AreEqual("避", secret.Difficulty);
        Assert.AreSame(secret, RushESecretState.ForDifficulty("避"));
        Assert.IsTrue(RushESecretState.IsSecretDifficulty(" 避 "));
        Assert.AreEqual(RushESecretState.Magenta, GameHUDSkin.DifficultyColor("避"));
        Assert.AreEqual(RushESecretState.Magenta, GameStartCountdown.AccentForDifficulty("避"));
        var state = new RushESecretState();
        state.Select(SongId);
        for (int i = 0; i < 9; i++) Assert.IsTrue(state.TryHit(i * .2));
        Assert.IsTrue(state.TryHit(2)); Assert.IsTrue(state.Transitioning);
        Assert.IsTrue(state.Reveal()); Assert.IsTrue(state.Finish()); Assert.IsTrue(state.Unlocked);
        Assert.AreSame(secret, state.Current);
        // 記録は Hard などと別。
        Assert.AreNotEqual(HighScoreStore.Key(SongId, "避"), HighScoreStore.Key(SongId, "Hard"));
        Assert.AreNotEqual(SongAchievementStore.Key(SongId, "避"), SongAchievementStore.Key(SongId, "Hard"));
        Assert.AreNotEqual(DailyRankingStore.Key(SongId, "避"), DailyRankingStore.Key(SongId, "Hard"));
    }

    [Test]
    public void TheChartHasFiftyTargetsWorthFiftyThousandAndNoNotes()
    {
        var chart = ChartLoader.LoadFromStreamingAssets(SongId, "避");
        Assert.AreEqual(0, chart.notes.Count, "ノーツは流さない");
        Assert.AreEqual(50, chart.aim.targets.Count);
        Assert.AreEqual(25, chart.aim.targets.Count(t => t.color == "red"));
        Assert.AreEqual(25, chart.aim.targets.Count(t => t.color == "blue"));
        Assert.Greater(chart.aim.spikes.Count, 60);
        Assert.Greater(chart.aim.spikes.Count(s => s.follow == "red"), 0, "追尾トゲがある");
        Assert.AreEqual(10, ChartDifficultyRater.Rate(chart), "ノーツが無くても遊べる譜面として扱う");
        // 赤の的は右寄り、青の的は左寄り。どれも腕の届く範囲。
        Assert.IsTrue(chart.aim.targets.Where(t => t.color == "red").All(t => t.x >= -.2f));
        Assert.IsTrue(chart.aim.targets.Where(t => t.color == "blue").All(t => t.x <= .2f));
        Assert.IsTrue(chart.aim.targets.All(t => Mathf.Abs(t.x) <= 2f && Mathf.Abs(t.y) <= 1.1f));
        Assert.IsTrue(chart.aim.spikes.All(s => Mathf.Abs(s.x) <= 2.1f && Mathf.Abs(s.y) <= 1.2f));
        var session = new AimDodgeSession(chart.aim, chart.offsetMs / 1000.0, 110.9);
        Assert.AreEqual(50, session.Targets.Count);
        CollectionAssert.AreEqual(Enumerable.Repeat(1000, 50), session.Targets.Select(t => t.MaxPoints));
        Assert.Greater(session.FirstTime, AimDodgeRules.LeadSeconds, "最初の的も奥から見える時間がある");
        Assert.Less(session.LastTime, 108.85, "音が鳴り終わる前に終わる");
        // 同じ手の的は 1.2 秒以上あける。
        foreach (var hand in new[] { SaberHand.Right, SaberHand.Left })
        {
            var times = session.Targets.Where(t => t.Hand == hand).Select(t => t.Time).ToList();
            for (int i = 1; i < times.Count; i++) Assert.GreaterOrEqual(times[i] - times[i - 1], 1.2 - 1e-6, hand.ToString());
        }
        // 的の直前1秒・直後0.6秒には、トゲの絵を的の絵に重ねない(的の真ん中で待てる・見づらくならない)。
        foreach (var spike in session.Spikes.Where(s => s.Follow == SaberHand.Any))
            foreach (var target in session.Targets)
            {
                double lead = target.Time - spike.Time;
                if (lead < -.6 || lead > 1.0) continue;
                Assert.GreaterOrEqual(Vector2.Distance(spike.Position, target.Position),
                    AimDodgeRules.SpikeVisualRadius * spike.Size + AimDodgeRules.TargetRadius - 1e-4f, $"{spike.Time:F3} / {target.Time:F3}");
            }
    }

    [Test]
    public void ScoreStartsAtFiftyThousandSpikesCostAThousandAndAllBullsMakeAHundredThousand()
    {
        Assert.AreEqual(50000, AimDodgeRules.Score(0, 0));
        Assert.AreEqual(100000, AimDodgeRules.Score(0, 50000), "理論値");
        Assert.AreEqual(49000, AimDodgeRules.Score(1, 0));
        Assert.AreEqual(0, AimDodgeRules.Score(60, 0), "0 より下げない");
        Assert.AreEqual(50000, AimDodgeRules.Score(60, 50000));
        Assert.AreEqual(50000, AimDodgeRules.DartMaxPoints(7).Sum(), "割り切れなくても的の満点は 50000");
        Assert.AreEqual(0, AimDodgeRules.DartMaxPoints(0).Length);
        Assert.AreEqual(JudgmentTier.Perfect, AimDodgeRules.Classify(.2f));
        Assert.AreEqual(JudgmentTier.Great, AimDodgeRules.Classify(.3f));
        Assert.AreEqual(JudgmentTier.Good, AimDodgeRules.Classify(.62f));
        Assert.AreEqual(JudgmentTier.Miss, AimDodgeRules.Classify(.63f));
        Assert.AreEqual(JudgmentTier.Miss, AimDodgeRules.Classify(float.PositiveInfinity), "一度も照準が来なかった");
        Assert.AreEqual(1000, AimDodgeRules.DartPoints(JudgmentTier.Perfect, 1000));
        Assert.AreEqual(600, AimDodgeRules.DartPoints(JudgmentTier.Great, 1000));
        Assert.AreEqual(300, AimDodgeRules.DartPoints(JudgmentTier.Good, 1000));
        Assert.AreEqual(0, AimDodgeRules.DartPoints(JudgmentTier.Miss, 1000));
        Assert.AreEqual("BULL", AimDodgeRules.DartLabel(JudgmentTier.Perfect));
        Assert.AreEqual("INNER", AimDodgeRules.DartLabel(JudgmentTier.Great));
        Assert.AreEqual("OUTER", AimDodgeRules.DartLabel(JudgmentTier.Good));
        Assert.AreEqual("MISS", AimDodgeRules.DartLabel(JudgmentTier.Miss));
        Assert.AreEqual(.878f, AimDodgeRules.DartAccuracy(43900, 50000), 1e-5f);
    }

    static AimHandSample At(float x, float y) => new AimHandSample(true, new Vector2(x, y));

    [Test]
    public void DartsTakeTheClosestPointAndSpikesHitOncePerTouch()
    {
        var chart = new AimChartData();
        chart.targets.Add(new AimTargetData { time = 2000, x = 1, y = 0, color = "red" });
        chart.targets.Add(new AimTargetData { time = 4000, x = -1, y = 0, color = "blue" });
        chart.targets.Add(new AimTargetData { time = 6000, x = 1, y = 0, color = "red" });
        chart.spikes.Add(new AimSpikeData { time = 3000, x = 0, y = 0 });
        chart.spikes.Add(new AimSpikeData { time = 3300, x = 0, y = .1f });
        chart.spikes.Add(new AimSpikeData { time = 5000, x = 0, y = 0 });
        chart.spikes.Add(new AimSpikeData { time = 5500, x = 2, y = 0 });
        var session = new AimDodgeSession(chart, 0);
        var events = new List<AimEvent>();
        var none = default(AimHandSample);

        // 赤の的: 窓の前は数えない。窓の中で BULL に入った瞬間に決まる。
        session.Tick(1.85, At(1f, 0), none, events);
        Assert.AreEqual(0, events.Count, "窓(±0.1秒)の前");
        session.Tick(1.95, At(1.3f, 0), none, events);
        Assert.AreEqual(0, events.Count, "まだ OUTER より良くなりうる");
        session.Tick(2.0, At(1.15f, 0), none, events);
        Assert.AreEqual(1, events.Count);
        Assert.AreEqual(AimEventKind.Dart, events[0].Kind);
        Assert.AreEqual(JudgmentTier.Perfect, events[0].Target.Tier);
        Assert.AreEqual(events[0].Target.MaxPoints, events[0].Target.Points);

        // トゲ: 届いた前後0.12秒に照準の芯が当たりの内なら1回だけ当たり。0.6 秒は守られる。
        events.Clear();
        session.Tick(2.95, At(.3f, 0), none, events);
        Assert.AreEqual(AimEventKind.Damage, events.Single().Kind);
        Assert.AreEqual(SaberHand.Right, events[0].Hand);
        events.Clear();
        session.Tick(3.0, At(.3f, 0), none, events);
        session.Tick(3.3, At(0f, .1f), none, events);
        Assert.IsFalse(events.Any(e => e.Kind == AimEventKind.Damage), "当たった直後は守られる");
        Assert.AreEqual(1, session.DamageCount);

        // 青の的: 照準が届いていない手は MISS。
        events.Clear();
        session.Tick(4.2, At(1f, 1f), none, events);
        Assert.AreEqual(JudgmentTier.Miss, events.Single(e => e.Kind == AimEventKind.Dart).Target.Tier);
        Assert.IsTrue(events.Any(e => e.Kind == AimEventKind.Avoided), "3.3 秒のトゲは当たっていないので解除");

        // 見えていない手はトゲにも当たらない。かすりは当たりの外の幅だけ。
        events.Clear();
        session.Tick(5.0, new AimHandSample(false, Vector2.zero), none, events);
        session.Tick(5.2, At(5, 5), none, events);
        Assert.IsTrue(events.Any(e => e.Kind == AimEventKind.Avoided && e.Spike.Index == 2));
        events.Clear();
        session.Tick(5.5, At(2f - AimDodgeRules.ContactDistance(1) - .1f, 0), none, events);
        Assert.AreEqual(AimEventKind.Graze, events.Single().Kind);
        Assert.AreEqual(1, session.DamageCount);

        // 最後の赤の的は INNER(0.3 ずれ)。コンボは MISS とトゲで切れる。
        events.Clear();
        session.Tick(6.0, At(1.3f, 0), none, events);
        session.Tick(6.11, At(1.3f, 0), none, events);
        Assert.AreEqual(JudgmentTier.Great, events.Single(e => e.Kind == AimEventKind.Dart).Target.Tier);
        session.Tick(7.0, none, none, events);
        Assert.IsTrue(session.Completed);
        CollectionAssert.AreEqual(new[] { 1, 1, 0, 1 }, session.TierCounts);
        int each = AimDodgeRules.DartMaxPoints(3)[0];
        Assert.AreEqual(AimDodgeRules.Score(1, session.DartPoints), session.Score);
        Assert.AreEqual(each + AimDodgeRules.DartPoints(JudgmentTier.Great, AimDodgeRules.DartMaxPoints(3)[2]), session.DartPoints);
        Assert.AreEqual(1, session.Combo);
        Assert.AreEqual(1, session.MaxCombo);
    }

    [Test]
    public void ALongFrameThatSkipsTheWholeWindowUsesTheCrosshairRightAfterIt()
    {
        var chart = new AimChartData();
        chart.targets.Add(new AimTargetData { time = 2000, x = 1, y = 0, color = "red" });
        chart.targets.Add(new AimTargetData { time = 4000, x = 1, y = 0, color = "red" });
        var session = new AimDodgeSession(chart, 0);
        var events = new List<AimEvent>();
        session.Tick(1.5, At(1f, 0), default, events);
        session.Tick(2.4, At(1f, 0), default, events);
        Assert.AreEqual(JudgmentTier.Perfect, events.Single().Target.Tier, "窓の前と後ろしか見ていなくても、照準が真ん中なら BULL");
        events.Clear();
        session.Tick(3.5, At(1f, 0), default, events);
        session.Tick(4.4, new AimHandSample(false, Vector2.zero), default, events);
        Assert.AreEqual(JudgmentTier.Miss, events.Single().Target.Tier, "照準が見えていなければ MISS のまま");
    }

    [Test]
    public void FollowSpikesAimWhereTheCrosshairIsWhenTheyAppear()
    {
        var chart = new AimChartData();
        chart.spikes.Add(new AimSpikeData { time = 3000, x = .5f, y = .5f, follow = "blue" });
        chart.spikes.Add(new AimSpikeData { time = 3500, x = .5f, y = .5f, follow = "red" });
        var session = new AimDodgeSession(chart, 0);
        session.PlaceFollowers(3.0 - AimDodgeRules.LeadSeconds - .01, At(1, 1), At(-1, -1));
        Assert.AreEqual(new Vector2(.5f, .5f), session.Spikes[0].Position, "出る前は決めない");
        session.PlaceFollowers(3.0 - AimDodgeRules.LeadSeconds, At(1, 1), At(-3, -0.4f));
        Assert.AreEqual(new Vector2(-2f, -.4f), session.Spikes[0].Position, "青の照準の位置(腕の届く範囲に収める)");
        session.PlaceFollowers(3.5 - AimDodgeRules.LeadSeconds, new AimHandSample(false, Vector2.zero), At(0, 0));
        Assert.AreEqual(new Vector2(.5f, .5f), session.Spikes[1].Position, "赤が見えていなければ譜面の位置");
        Assert.IsTrue(session.Needs(1.0, SaberHand.Any), "トゲが来ていれば止める対象");
        Assert.IsFalse(session.Needs(1.0, SaberHand.Left), "色ごとの知らせはその色の的だけ(トゲはどちらの手にも当たるので数えない)");
    }

    [Test]
    public void ChartLoaderKeepsAimObjectsInOrderAndNormalizesThem()
    {
        var chart = ChartLoader.Parse("{\"bpm\":120,\"notes\":[],\"aim\":{\"targets\":[{\"time\":2000,\"x\":1,\"y\":0,\"color\":\" Blue \"},{\"time\":1000,\"x\":0,\"y\":0}],"
            + "\"spikes\":[{\"time\":500,\"x\":0,\"y\":0,\"size\":0},{\"time\":400,\"x\":1,\"y\":0,\"follow\":\" RED \"}]}}");
        Assert.AreEqual(2, chart.aim.targets.Count);
        Assert.AreEqual(1000f, chart.aim.targets[0].time);
        Assert.AreEqual("red", chart.aim.targets[0].color, "色の無い的は赤");
        Assert.AreEqual("blue", chart.aim.targets[1].color);
        Assert.AreEqual(2, chart.aim.spikes.Count);
        Assert.AreEqual(400f, chart.aim.spikes[0].time, "時刻順");
        Assert.AreEqual("red", chart.aim.spikes[0].follow);
        Assert.AreEqual(SaberHand.Right, AimDodgeRules.FollowOf(chart.aim.spikes[0].follow));
        Assert.AreEqual(SaberHand.Any, AimDodgeRules.FollowOf(chart.aim.spikes[1].follow));
        Assert.AreEqual(1f, chart.aim.spikes[1].size, "大きさ 0 は 1 に戻す");
        Assert.IsFalse(ChartLoader.Parse("{\"bpm\":120,\"notes\":[]}").aim.HasContent);
        Assert.AreEqual(0, ChartDifficultyRater.Rate(ChartLoader.Parse("{\"bpm\":120,\"notes\":[]}")), "空の譜面は今までどおり遊べない");
    }

    [Test]
    public void ScoreManagerCountsDartsAndDamageFromFiftyThousand()
    {
        var go = new GameObject("AimScore");
        try
        {
            var score = go.AddComponent<ScoreManager>();
            score.BeginAimScoring(50);
            Assert.IsTrue(score.AimScoring);
            Assert.AreEqual(50000, score.Score);
            Assert.AreEqual(50000, score.ScoreWithCombo);
            Assert.AreEqual(0, score.ComboBonus);
            Assert.AreEqual(50, score.ComboUnits);
            var tiers = new List<JudgmentTier>(); int lost = 0;
            score.OnJudgmentEx += (tier, award, flick) => tiers.Add(tier);
            score.OnAimDamage += value => lost = value;
            score.RegisterAimDart(JudgmentTier.Perfect, 1000, SaberHand.Right);
            Assert.AreEqual(51000, score.Score);
            Assert.AreEqual(1, score.Combo);
            Assert.AreEqual(SaberHand.Right, score.LastCutHand);
            score.RegisterAimDamage();
            Assert.AreEqual(50000, score.Score);
            Assert.AreEqual(1000, lost);
            Assert.AreEqual(0, score.Combo, "トゲでコンボが切れる");
            Assert.IsFalse(score.IsFullCombo);
            score.RegisterAimDart(JudgmentTier.Great, 600, SaberHand.Left);
            score.RegisterAimDart(JudgmentTier.Miss, 0, SaberHand.Left);
            Assert.AreEqual(50600, score.Score);
            CollectionAssert.AreEqual(new[] { JudgmentTier.Perfect, JudgmentTier.Great, JudgmentTier.Miss }, tiers);
            Assert.AreEqual(1, score.PerfectCount); Assert.AreEqual(1, score.GreatCount); Assert.AreEqual(1, score.MissCount);
            Assert.AreEqual(2, score.CutCount, "外した的は記録の『切った回数』に入れない");
            Assert.AreEqual(1600, score.AimDartPoints);
            score.FinalizeScore();
            Assert.AreEqual(50600, score.Score, "コンボ点は足さない");
            score.RegisterAimDart(JudgmentTier.Perfect, 1000, SaberHand.Right);
            Assert.AreEqual(50600, score.Score, "精算のあとは数えない");
            score.Reset();
            Assert.IsFalse(score.AimScoring);
            Assert.AreEqual(0, score.Score);

            // トゲに当たらず全部 BULL なら FULL COMBO と ALL PERFECT。
            score.BeginAimScoring(2);
            score.RegisterAimDart(JudgmentTier.Perfect, 25000, SaberHand.Right);
            score.RegisterAimDart(JudgmentTier.Perfect, 25000, SaberHand.Left);
            Assert.IsTrue(score.IsFullCombo); Assert.IsTrue(score.IsAllPerfect);
            Assert.AreEqual(100000, score.Score);
        }
        finally { Object.DestroyImmediate(go); }
    }

    [Test]
    public void ResultShowsDartRowsAndDartAccuracy()
    {
        try
        {
            GameSession.ResetResult();
            GameSession.FinalAimMode = true;
            GameSession.FinalPerfect = 40; GameSession.FinalGreat = 5; GameSession.FinalGood = 3; GameSession.FinalMiss = 2;
            GameSession.FinalAimDamage = 4;
            GameSession.FinalAimDartPoints = 40 * 1000 + 5 * 600 + 3 * 300;
            GameSession.FinalAimDartTotal = 50000;
            Assert.AreEqual(43900 / 50000f, GameSession.FinalAccuracy, 1e-6f);
            CollectionAssert.AreEqual(new[] { "BULL", "INNER", "OUTER", "MISS", "DAMAGE" }, ResultSkin.JudgeLabels(true));
            CollectionAssert.AreEqual(new[] { 40, 5, 3, 2, 4 }, ResultSkin.JudgeCounts(true));
            Assert.AreEqual("50 TARGETS", ResultSkin.DistributionCountLabel(true, 50));
            Assert.AreEqual("BULL RATE 80.0%", ResultSkin.DistributionRateLabel(true, 80f));
            CollectionAssert.AreEqual(new[] { "PERFECT", "GREAT", "GOOD", "BAD", "MISS" }, ResultSkin.JudgeLabels(false));
            Assert.AreEqual("12 NOTES", ResultSkin.DistributionCountLabel(false, 12));
            GameSession.ResetResult();
            Assert.IsFalse(GameSession.FinalAimMode);
            Assert.AreEqual(0, GameSession.FinalAimDamage);
        }
        finally { GameSession.ResetResult(); }
    }
}
