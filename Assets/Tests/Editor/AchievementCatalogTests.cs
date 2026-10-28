using System;
using System.Collections.Generic;
using NUnit.Framework;

// 実績の一覧(AchievementCatalog、2026-10-29 の 44 個)。並び・ID の形と、各実績の取れる/取れないの境目。
public class AchievementCatalogTests
{
    // catalog.md の表の順(表示の順=珍しい順、clear が最後)。
    static readonly string[] Order =
    {
        "ap", "senbon", "quiz", "bullseye", "fubuki", "mirror", "shingan", "memory", "matador", "clock",
        "hardsplus", "secret_s", "wind", "overtake", "dark", "ichiban", "streak50", "fc", "kiriban", "zorome",
        "splus", "combo100", "streak20", "top", "secret", "pioneer", "jackpot", "comeback", "duet", "school",
        "ranks", "hard", "chorus", "quick", "combo50", "goldall", "dual", "arrows", "rainbow", "firstcut",
        "finale", "xcut", "practice", "clear",
    };

    static readonly DateTime Morning = new DateTime(2026, 10, 29, 10, 0, 0, DateTimeKind.Local);

    // 何も取れない寄りの、ふつうの記録された回(C ランク・ミスあり・演奏の記録なし)。
    static AchievementContext Ctx(Action<AchievementContext> change = null)
    {
        var c = new AchievementContext
        {
            SongId = "__AchievementTest", Difficulty = "Normal",
            Score = 30123, MaxCombo = 10, Hit = 10, Perfect = 5, Great = 3, Good = 2, Bad = 0, Miss = 2, CutCount = 10, Accuracy = .5f,
            Station = "A", Namespace = "", RunId = "run00001",
            StartedLocal = Morning.AddMinutes(-3), EndedLocal = Morning, EndedUnixMs = new DateTimeOffset(Morning).ToUnixTimeMilliseconds(),
            ClearsToday = 3,
        };
        change?.Invoke(c);
        return c;
    }

    static bool Has(AchievementContext context, string id)
    {
        var definition = AchievementCatalog.Find(id);
        Assert.NotNull(definition, id);
        return definition.Met(context);
    }

    static void Song(AchievementContext c, SecretChart chart) { c.SongId = chart.SongId; c.Difficulty = chart.Difficulty; }

    // 演奏の記録(最初から記録した回)。
    static PlayStats Notes(params NoteData[] notes) => PlayStatsTests.Stats(PlayStatsTests.Chart(notes));

    static NoteData[] Taps(int count, string color = "red", float fromMs = 1000, float stepMs = 500)
    {
        var notes = new NoteData[count];
        for (int i = 0; i < count; i++) notes[i] = PlayStatsTests.Note(fromMs + i * stepMs, color);
        return notes;
    }

    [Test]
    public void TheListHas44AchievementsInTheCatalogOrderWithClearLast()
    {
        var all = AchievementCatalog.All;
        Assert.AreEqual(44, all.Count);
        var ids = new List<string>();
        for (int i = 0; i < all.Count; i++)
        {
            ids.Add(all[i].Id);
            Assert.AreEqual(i, all[i].Order);
            Assert.IsTrue(AchievementStore.IsValidId(all[i].Id), all[i].Id + " は記録できる ID の形");
            Assert.IsFalse(string.IsNullOrEmpty(all[i].Text), all[i].Id);
            Assert.IsFalse(string.IsNullOrEmpty(all[i].Name), all[i].Id);
            Assert.AreSame(all[i], AchievementCatalog.Find(all[i].Id));
        }
        CollectionAssert.AreEqual(Order, ids);
        CollectionAssert.AllItemsAreUnique(ids);
        Assert.AreEqual(AchievementCatalog.ClearId, ids[ids.Count - 1]);
        Assert.IsNull(AchievementCatalog.Find("nothing"));
        Assert.IsNull(AchievementCatalog.Find(null));
    }

    [Test]
    public void AnOrdinaryRunGetsOnlyClear()
    {
        var earned = AchievementCatalog.Earned(Ctx());
        Assert.AreEqual(1, earned.Count, string.Join(",", earned.ConvertAll(d => d.Id)));
        Assert.AreEqual(AchievementCatalog.ClearId, earned[0].Id);
        Assert.AreEqual(0, AchievementCatalog.Earned(null).Count);
    }

    [Test]
    public void EarnedAchievementsAreInDisplayOrder()
    {
        var earned = AchievementCatalog.Earned(Ctx(c => { c.Score = 95123; c.MaxCombo = 120; c.Miss = 0; c.Perfect = 10; c.Great = c.Good = 0; }));
        for (int i = 1; i < earned.Count; i++) Assert.Less(earned[i - 1].Order, earned[i].Order);
        CollectionAssert.AreEqual(new[] { "ap", "fc", "splus", "combo100", "ranks", "combo50", "clear" }, earned.ConvertAll(d => d.Id));
    }

    [Test]
    public void AllPerfectAndFullComboFollowTheJudgmentCounts()
    {
        Assert.IsTrue(Has(Ctx(c => { c.Miss = 0; c.Perfect = 10; c.Great = c.Good = 0; }), "ap"));
        Assert.IsTrue(Has(Ctx(c => { c.Miss = 0; c.Perfect = 10; c.Great = c.Good = 0; }), "fc"));
        var great = Ctx(c => { c.Miss = 0; c.Perfect = 9; c.Great = 1; c.Good = 0; });
        Assert.IsFalse(Has(great, "ap"));
        Assert.IsTrue(Has(great, "fc"), "GOOD・GREAT はフルコンボ");
        Assert.IsFalse(Has(Ctx(c => { c.Miss = 0; c.Bad = 1; }), "fc"), "BAD でコンボが切れる");
        Assert.IsFalse(Has(Ctx(), "fc"), "MISS");
        Assert.IsFalse(Has(Ctx(c => { c.Miss = 0; c.Perfect = 10; c.Great = c.Good = 0; c.AimDamage = 1; }), "ap"), "避はトゲに当たると対象外");
        Assert.IsFalse(Has(Ctx(c => { c.Hit = c.Perfect = c.Great = c.Good = c.Miss = 0; }), "fc"), "1つも判定が無い");
    }

    [Test]
    public void SenbonNeedsAThousandCuts()
    {
        Assert.IsFalse(Has(Ctx(c => c.CutCount = AchievementCatalog.SenbonCuts - 1), "senbon"));
        Assert.IsTrue(Has(Ctx(c => c.CutCount = AchievementCatalog.SenbonCuts), "senbon"));
    }

    [Test]
    public void QuizNeedsEveryRealNoteOnThePapersCut()
    {
        var chart = PlayStatsTests.Chart(PlayStatsTests.Note(500), PlayStatsTests.Note(1000, "gold"), PlayStatsTests.Note(1500, "gold"), PlayStatsTests.Note(3000));
        chart.quiz.papers.Add(new QuizPaperData { startMs = 1000, endMs = 2000 });
        PlayStats Run(JudgmentTier inside, JudgmentTier outside)
        {
            var stats = PlayStatsTests.Stats(chart);
            stats.Record(0, outside); stats.Record(1, inside); stats.Record(2, JudgmentTier.Perfect); stats.Record(3, outside);
            return stats;
        }
        Assert.IsTrue(Has(Ctx(c => { Song(c, RushESecretState.BenkyouQuiz); c.Stats = Run(JudgmentTier.Perfect, JudgmentTier.Miss); }), "quiz"),
            "試験用紙の外の MISS は問わない");
        Assert.IsTrue(Has(Ctx(c => { Song(c, RushESecretState.BenkyouQuiz); c.Stats = Run(JudgmentTier.Bad, JudgmentTier.Perfect); }), "quiz"),
            "BAD はよい");
        Assert.IsFalse(Has(Ctx(c => { Song(c, RushESecretState.BenkyouQuiz); c.Stats = Run(JudgmentTier.Miss, JudgmentTier.Perfect); }), "quiz"));
        Assert.IsFalse(Has(Ctx(c => { c.SongId = RushESecretState.BenkyouQuiz.SongId; c.Difficulty = "Hard"; c.Stats = Run(JudgmentTier.Perfect, JudgmentTier.Perfect); }), "quiz"),
            "Q のときだけ");
        var noPaper = PlayStatsTests.AllResults(Notes(Taps(4)), JudgmentTier.Perfect);
        Assert.IsFalse(Has(Ctx(c => { Song(c, RushESecretState.BenkyouQuiz); c.Stats = noPaper; }), "quiz"), "試験用紙のノーツが無い");
        var late = Run(JudgmentTier.Perfect, JudgmentTier.Perfect);
        late.Complete = false;
        Assert.IsFalse(Has(Ctx(c => { Song(c, RushESecretState.BenkyouQuiz); c.Stats = late; }), "quiz"), "途中から記録した回");
    }

    [Test]
    public void BullseyeNeedsEveryTargetInTheAimChart()
    {
        void D(AchievementContext c) { Song(c, RushESecretState.PhobiaAvoid); c.AimMode = true; c.Hit = c.Perfect = 20; c.Great = c.Good = 0; c.Miss = 0; }
        Assert.IsTrue(Has(Ctx(D), "bullseye"));
        Assert.IsTrue(Has(Ctx(c => { D(c); c.AimDamage = 3; }), "bullseye"), "トゲは問わない");
        Assert.IsFalse(Has(Ctx(c => { D(c); c.Miss = 1; }), "bullseye"));
        Assert.IsFalse(Has(Ctx(c => { D(c); c.AimMode = false; }), "bullseye"));
        Assert.IsFalse(Has(Ctx(c => { D(c); c.Hit = c.Perfect = 0; }), "bullseye"), "的が1つも無い");
    }

    // 裏の印のノーツ(F・R・V・M)を全部 MISS 以外で切ると取れる。印の無いノーツの MISS は問わない。
    [Test]
    public void SecretMarkedNotesMustAllBeCut()
    {
        Check("fubuki", RushESecretState.EpilogueFall, chart => { chart.fall.enabled = true; chart.notes[1].fall = chart.notes[2].fall = true; });
        Check("mirror", RushESecretState.ManicSonicReverse, chart => { chart.reverse.enabled = true; chart.notes[1].mirror = chart.notes[2].mirror = true; });
        Check("shingan", RushESecretState.ZeroVanish, chart => { chart.notes[1].vanish = chart.notes[2].vanish = "hide"; });
        Check("memory", RushESecretState.MorningMemory, chart => { chart.notes[1].veil = .5f; chart.notes[2].veil = 1f; });

        void Check(string id, SecretChart secret, Action<ChartData> mark)
        {
            var chart = PlayStatsTests.Chart(Taps(4));
            mark(chart);
            PlayStats Run(JudgmentTier marked, JudgmentTier other)
            {
                var stats = PlayStatsTests.Stats(chart);
                stats.Record(0, other); stats.Record(1, marked); stats.Record(2, JudgmentTier.Perfect); stats.Record(3, other);
                return stats;
            }
            Assert.IsTrue(Has(Ctx(c => { Song(c, secret); c.Stats = Run(JudgmentTier.Good, JudgmentTier.Miss); }), id), id);
            Assert.IsFalse(Has(Ctx(c => { Song(c, secret); c.Stats = Run(JudgmentTier.Miss, JudgmentTier.Perfect); }), id), id + " の印の MISS");
            Assert.IsFalse(Has(Ctx(c => { c.Stats = Run(JudgmentTier.Perfect, JudgmentTier.Perfect); }), id), id + " はその裏譜面だけ");
            var plain = PlayStatsTests.AllResults(Notes(Taps(4)), JudgmentTier.Perfect);
            Assert.IsFalse(Has(Ctx(c => { Song(c, secret); c.Stats = plain; }), id), id + " の印のノーツが無い");
        }
    }

    [Test]
    public void MatadorAllowsUpToFiveStuns()
    {
        PlayStats Bull(int stuns, bool hasBull = true) { var s = Notes(Taps(3)); s.HasBull = hasBull; s.Stuns = stuns; return s; }
        Assert.IsTrue(Has(Ctx(c => { Song(c, RushESecretState.AndalusiaBull); c.Stats = Bull(AchievementCatalog.MatadorMaxStuns); }), "matador"));
        Assert.IsFalse(Has(Ctx(c => { Song(c, RushESecretState.AndalusiaBull); c.Stats = Bull(AchievementCatalog.MatadorMaxStuns + 1); }), "matador"));
        Assert.IsTrue(Has(Ctx(c => { Song(c, RushESecretState.AndalusiaBull); c.Stats = Bull(0); }), "matador"));
        Assert.IsFalse(Has(Ctx(c => { Song(c, RushESecretState.AndalusiaBull); c.Stats = Bull(0, false); }), "matador"), "闘牛の無い回");
        Assert.IsFalse(Has(Ctx(c => { Song(c, RushESecretState.AndalusiaBull); }), "matador"), "演奏の記録が無い");
        Assert.IsFalse(Has(Ctx(c => { c.Stats = Bull(0); }), "matador"), "T のときだけ");
    }

    [Test]
    public void ClockNeedsTheSongToSpan1423()
    {
        var day = new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Local);
        bool At(int h1, int m1, int s1, int h2, int m2, int s2, string song = null, string difficulty = "Easy") => Has(Ctx(c =>
        {
            c.SongId = song ?? RushESecretState.TwoTwentyThreeLight.SongId; c.Difficulty = difficulty;
            c.StartedLocal = day.AddHours(h1).AddMinutes(m1).AddSeconds(s1); c.EndedLocal = day.AddHours(h2).AddMinutes(m2).AddSeconds(s2);
        }), "clock");
        Assert.IsTrue(At(14, 20, 10, 14, 23, 0), "終わりがちょうど 14:23:00");
        Assert.IsFalse(At(14, 20, 0, 14, 22, 59));
        Assert.IsTrue(At(14, 23, 59, 14, 27, 0), "始めが 14:23:59");
        Assert.IsFalse(At(14, 24, 0, 14, 27, 0), "14:24:00 からは外");
        Assert.IsTrue(At(14, 22, 0, 14, 25, 0, difficulty: RushESecretState.TwoTwentyThreeLight.Difficulty), "どの難易度でも");
        Assert.IsFalse(At(14, 22, 0, 14, 25, 0, "ElDorado"), "2:23 AM のときだけ");
        Assert.IsFalse(At(2, 22, 0, 2, 25, 0), "午前の 2:23 ではない");
    }

    [Test]
    public void RanksFollowTheScoreThresholds()
    {
        int splus = PlayRankHelper.ScoreLowerBound(PlayRank.SPlus), s = PlayRankHelper.ScoreLowerBound(PlayRank.S), b = PlayRankHelper.ScoreLowerBound(PlayRank.B);
        Assert.AreEqual(90000, splus);
        Assert.IsTrue(Has(Ctx(c => c.Score = splus), "splus"));
        Assert.IsFalse(Has(Ctx(c => c.Score = splus - 1), "splus"));
        Assert.IsTrue(Has(Ctx(c => c.Score = s), "ranks"));
        Assert.IsFalse(Has(Ctx(c => c.Score = s - 1), "ranks"));
        // Hard(表)
        Assert.IsTrue(Has(Ctx(c => { c.Difficulty = "Hard"; c.Score = splus; }), "hardsplus"));
        Assert.IsFalse(Has(Ctx(c => { c.Difficulty = "Hard"; c.Score = splus - 1; }), "hardsplus"));
        Assert.IsFalse(Has(Ctx(c => { Song(c, RushESecretState.RushE); c.Score = splus; }), "hardsplus"), "裏譜面は Hard ではない");
        Assert.IsTrue(Has(Ctx(c => { c.Difficulty = "hard"; c.Score = b; }), "hard"));
        Assert.IsFalse(Has(Ctx(c => { c.Difficulty = "Hard"; c.Score = b - 1; }), "hard"));
        Assert.IsFalse(Has(Ctx(c => { c.Difficulty = "Normal"; c.Score = 99000; }), "hard"));
        // 裏譜面
        Assert.IsTrue(Has(Ctx(c => { Song(c, RushESecretState.RushE); c.Score = s; }), "secret_s"));
        Assert.IsFalse(Has(Ctx(c => { Song(c, RushESecretState.RushE); c.Score = s - 1; }), "secret_s"));
        Assert.IsFalse(Has(Ctx(c => { c.Difficulty = "Hard"; c.Score = 99000; }), "secret_s"));
        Assert.IsTrue(Has(Ctx(c => Song(c, RushESecretState.ElDoradoG)), "secret"), "裏譜面の回");
        Assert.IsFalse(Has(Ctx(c => c.Difficulty = "Hard"), "secret"));
    }

    [Test]
    public void WindAndOvertakeCountCutsOfMarkedNotes()
    {
        var wind = Taps(AchievementCatalog.WindCuts + 1);
        foreach (var note in wind) note.windX = .3f;
        var windStats = PlayStatsTests.AllResults(Notes(wind), JudgmentTier.Bad);
        windStats.Record(0, JudgmentTier.Miss);
        Assert.IsTrue(Has(Ctx(c => { Song(c, RushESecretState.AobasouWind); c.Stats = windStats; }), "wind"), "MISS 以外が 50 個");
        windStats.Record(1, JudgmentTier.Miss);
        Assert.IsFalse(Has(Ctx(c => { Song(c, RushESecretState.AobasouWind); c.Stats = windStats; }), "wind"), "49 個");

        var overtake = Taps(AchievementCatalog.OvertakeCuts);
        foreach (var note in overtake) note.approachScale = 1.5f;
        var overtakeStats = PlayStatsTests.AllResults(Notes(overtake), JudgmentTier.Good);
        Assert.IsTrue(Has(Ctx(c => { Song(c, RushESecretState.YurikagoOvertake); c.Stats = overtakeStats; }), "overtake"));
        Assert.IsFalse(Has(Ctx(c => { c.Stats = overtakeStats; }), "overtake"), "P のときだけ");
        overtakeStats.Record(5, JudgmentTier.Miss);
        Assert.IsFalse(Has(Ctx(c => { Song(c, RushESecretState.YurikagoOvertake); c.Stats = overtakeStats; }), "overtake"));
    }

    [Test]
    public void DarkNeedsSixtyPercentAccuracyOnY()
    {
        Assert.IsTrue(Has(Ctx(c => { Song(c, RushESecretState.TwoTwentyThreeLight); c.Accuracy = AchievementCatalog.DarkAccuracy; }), "dark"));
        Assert.IsFalse(Has(Ctx(c => { Song(c, RushESecretState.TwoTwentyThreeLight); c.Accuracy = .59f; }), "dark"));
        Assert.IsFalse(Has(Ctx(c => { c.SongId = RushESecretState.TwoTwentyThreeLight.SongId; c.Difficulty = "Hard"; c.Accuracy = 1f; }), "dark"));
    }

    [Test]
    public void IchibanIsTheFirstClearOfTheDayInThisMode()
    {
        Assert.IsTrue(Has(Ctx(c => c.ClearsToday = 0), "ichiban"));
        Assert.IsFalse(Has(Ctx(c => c.ClearsToday = 1), "ichiban"));
        Assert.IsFalse(Has(Ctx(c => c.ClearsToday = -1), "ichiban"), "記録が読めない日は出さない");
    }

    [Test]
    public void PerfectStreaksNeedTwentyAndFifty()
    {
        PlayStats Streak(int perfect)
        {
            var stats = Notes(Taps(3));
            stats.Judgment(JudgmentTier.Great);
            for (int i = 0; i < perfect; i++) stats.Judgment(JudgmentTier.Perfect);
            stats.Judgment(JudgmentTier.Miss);
            return stats;
        }
        Assert.IsTrue(Has(Ctx(c => c.Stats = Streak(AchievementCatalog.ShortStreak)), "streak20"));
        Assert.IsFalse(Has(Ctx(c => c.Stats = Streak(AchievementCatalog.ShortStreak - 1)), "streak20"));
        Assert.IsTrue(Has(Ctx(c => c.Stats = Streak(AchievementCatalog.LongStreak)), "streak50"));
        Assert.IsFalse(Has(Ctx(c => c.Stats = Streak(AchievementCatalog.LongStreak - 1)), "streak50"));
        Assert.IsFalse(Has(Ctx(c => { c.Stats = Streak(60); c.AimMode = true; }), "streak20"), "照準の裏譜面は対象外");
        Assert.IsFalse(Has(Ctx(), "streak20"), "演奏の記録が無い");
    }

    [Test]
    public void KiribanIsEveryHundredthClear()
    {
        Assert.IsTrue(Has(Ctx(c => c.ClearNumber = 100), "kiriban"));
        Assert.IsTrue(Has(Ctx(c => c.ClearNumber = 1200), "kiriban"));
        Assert.IsFalse(Has(Ctx(c => c.ClearNumber = 99), "kiriban"));
        Assert.IsFalse(Has(Ctx(c => c.ClearNumber = 101), "kiriban"));
        Assert.IsFalse(Has(Ctx(c => c.ClearNumber = 0), "kiriban"), "番号が無い");
    }

    [Test]
    public void ZoromeIsALastThreeDigitRepdigit()
    {
        foreach (int score in new[] { 1000, 12111, 45555, 99999, 30000 }) Assert.IsTrue(Has(Ctx(c => c.Score = score), "zorome"), score.ToString());
        foreach (int score in new[] { 999, 0, 555, 12110, 45556, 99998, 30123 }) Assert.IsFalse(Has(Ctx(c => c.Score = score), "zorome"), score.ToString());
    }

    [Test]
    public void CombosNeedFiftyAndAHundred()
    {
        Assert.IsTrue(Has(Ctx(c => c.MaxCombo = 50), "combo50"));
        Assert.IsFalse(Has(Ctx(c => c.MaxCombo = 49), "combo50"));
        Assert.IsTrue(Has(Ctx(c => c.MaxCombo = 100), "combo100"));
        Assert.IsFalse(Has(Ctx(c => c.MaxCombo = 99), "combo100"));
    }

    [Test]
    public void TopAndPioneerUseTheTotalRanking()
    {
        void Ranked(AchievementContext c, int rank, int tied, int total) { c.RankingAvailable = true; c.DailyRank = rank; c.TiedPlays = tied; c.TotalPlays = total; }
        Assert.IsTrue(Has(Ctx(c => Ranked(c, 1, 1, AchievementCatalog.TopMinPlays)), "top"));
        Assert.IsFalse(Has(Ctx(c => Ranked(c, 1, 1, AchievementCatalog.TopMinPlays - 1)), "top"));
        Assert.IsFalse(Has(Ctx(c => Ranked(c, 1, 2, 10)), "top"), "同点の1位");
        Assert.IsFalse(Has(Ctx(c => Ranked(c, 2, 1, 10)), "top"));
        Assert.IsFalse(Has(Ctx(c => { Ranked(c, 1, 1, 10); c.RankingAvailable = false; }), "top"), "順位が出ていない");
        Assert.IsTrue(Has(Ctx(c => Ranked(c, 1, 1, 1)), "pioneer"));
        Assert.IsFalse(Has(Ctx(c => Ranked(c, 1, 1, 2)), "pioneer"));
        Assert.IsFalse(Has(Ctx(c => { Ranked(c, 1, 1, 1); c.RankingAvailable = false; }), "pioneer"));
    }

    [Test]
    public void JackpotRainbowAndXcutComeFromThePlayRecord()
    {
        var stats = Notes(Taps(3));
        Assert.IsFalse(Has(Ctx(c => c.Stats = stats), "jackpot"));
        Assert.IsFalse(Has(Ctx(c => c.Stats = stats), "rainbow"));
        Assert.IsFalse(Has(Ctx(c => c.Stats = stats), "xcut"));
        stats.Jackpots = 1; stats.VoltageMax = VoltageMeter.MaximumLevel; stats.CrossCuts = 1;
        Assert.IsTrue(Has(Ctx(c => c.Stats = stats), "jackpot"));
        Assert.IsTrue(Has(Ctx(c => c.Stats = stats), "rainbow"));
        Assert.IsTrue(Has(Ctx(c => c.Stats = stats), "xcut"));
        stats.VoltageMax = VoltageMeter.MaximumLevel - 1;
        Assert.IsFalse(Has(Ctx(c => c.Stats = stats), "rainbow"));
        Assert.IsFalse(Has(Ctx(), "jackpot"), "演奏の記録が無い");
    }

    [Test]
    public void ComebackNeedsAMistakeAndThirtyComboAtTheEnd()
    {
        var stats = Notes(Taps(3));
        stats.FinalCombo = AchievementCatalog.ComebackCombo;
        Assert.IsTrue(Has(Ctx(c => { c.Miss = 1; c.Stats = stats; }), "comeback"));
        Assert.IsTrue(Has(Ctx(c => { c.Miss = 0; c.Bad = 1; c.Stats = stats; }), "comeback"));
        Assert.IsFalse(Has(Ctx(c => { c.Miss = 0; c.Bad = 0; c.Stats = stats; }), "comeback"), "立て直していない");
        stats.FinalCombo = AchievementCatalog.ComebackCombo - 1;
        Assert.IsFalse(Has(Ctx(c => { c.Miss = 1; c.Stats = stats; }), "comeback"));
    }

    [Test]
    public void DuetIsTheOtherStationFinishingTheSameSongWithinAMinute()
    {
        var end = Morning;
        long endMs = new DateTimeOffset(end).ToUnixTimeMilliseconds();
        RankingPlay Play(long agoMs, string station = "B", string song = "__AchievementTest", string ns = "", string diff = "Hard", string id = null) =>
            new RankingPlay { id = id ?? Guid.NewGuid().ToString("N"), song = song, diff = diff, st = station, ns = ns, at = endMs - agoMs, score = 1, acc = .5f, rank = "C", date = "2026/10/29" };
        bool With(params RankingPlay[] plays) => Has(Ctx(c => c.Plays = plays), "duet");
        Assert.IsTrue(With(Play(60000)), "60 秒前まで(難易度は問わない)");
        Assert.IsTrue(With(Play(0)));
        Assert.IsFalse(With(Play(60001)));
        // 相手が曲を1秒あとに終えても、終わりの演出が短くて結果が先に届いていれば、あとで結果が出たこの回が取る(取るのは届いた側だけ)。
        Assert.IsTrue(With(Play(-1000)), "前後60秒");
        Assert.IsTrue(With(Play(-60000)));
        Assert.IsFalse(With(Play(-60001)));
        Assert.IsFalse(With(Play(1000, "A")), "自分の台");
        Assert.IsFalse(With(Play(1000, ns: "fes")), "別のモード");
        Assert.IsFalse(With(Play(1000, song: "ElDorado")), "別の曲");
        Assert.IsFalse(With(Play(1000, id: "run00001")), "この回");
        Assert.IsFalse(With(), "記録なし");
        Assert.IsFalse(Has(Ctx(c => c.Plays = null), "duet"));
    }

    [Test]
    public void SchoolIsTheSchoolSongOnAnyDifficulty()
    {
        Assert.IsTrue(Has(Ctx(c => { c.SongId = RushESecretState.EpilogueFall.SongId; c.Difficulty = "Easy"; }), "school"));
        Assert.IsTrue(Has(Ctx(c => Song(c, RushESecretState.EpilogueFall)), "school"));
        Assert.IsFalse(Has(Ctx(), "school"));
    }

    [Test]
    public void ChorusNeedsFiveCleanNotesInTheLastStrongSection()
    {
        var stage = new StagePerformanceTimeline
        {
            sections = new[]
            {
                new StagePerformanceTimeline.Section { startSeconds = 1, endSeconds = 2, intensity = 1f },
                new StagePerformanceTimeline.Section { startSeconds = 5, endSeconds = 8, intensity = 1f },
            },
        };
        PlayStats Run(int inside, JudgmentTier insideTier, JudgmentTier earlier)
        {
            var notes = new List<NoteData> { PlayStatsTests.Note(1500) };
            for (int i = 0; i < inside; i++) notes.Add(PlayStatsTests.Note(5000 + i * 400));
            var stats = PlayStatsTests.Stats(PlayStatsTests.Chart(notes.ToArray()), stage);
            PlayStatsTests.AllResults(stats, JudgmentTier.Great);
            stats.Record(0, earlier);
            stats.Record(inside, insideTier);
            return stats;
        }
        Assert.IsTrue(Has(Ctx(c => c.Stats = Run(AchievementCatalog.ChorusMinNotes, JudgmentTier.Good, JudgmentTier.Miss)), "chorus"),
            "前のサビの MISS は問わない");
        Assert.IsFalse(Has(Ctx(c => c.Stats = Run(AchievementCatalog.ChorusMinNotes - 1, JudgmentTier.Good, JudgmentTier.Perfect)), "chorus"), "4 個");
        Assert.IsFalse(Has(Ctx(c => c.Stats = Run(AchievementCatalog.ChorusMinNotes, JudgmentTier.Bad, JudgmentTier.Perfect)), "chorus"), "BAD");
        Assert.IsFalse(Has(Ctx(c => c.Stats = Run(AchievementCatalog.ChorusMinNotes, JudgmentTier.Miss, JudgmentTier.Perfect)), "chorus"), "MISS");
        var noStage = PlayStatsTests.AllResults(Notes(Taps(8)), JudgmentTier.Perfect);
        Assert.IsFalse(Has(Ctx(c => c.Stats = noStage), "chorus"), "サビの無い曲");
    }

    [Test]
    public void QuickNeedsFifteenSecondsOrLess()
    {
        Assert.IsTrue(Has(Ctx(c => c.SelectionSeconds = AchievementCatalog.QuickSeconds), "quick"));
        Assert.IsTrue(Has(Ctx(c => c.SelectionSeconds = 0), "quick"));
        Assert.IsFalse(Has(Ctx(c => c.SelectionSeconds = AchievementCatalog.QuickSeconds + .01), "quick"));
        Assert.IsFalse(Has(Ctx(c => c.SelectionSeconds = AchievementRunInfo.SelectionElapsed(0)), "quick"), "時間切れの自動開始");
        Assert.IsFalse(Has(Ctx(c => c.SelectionSeconds = null), "quick"), "判定調整から戻った選曲");
    }

    [Test]
    public void GoldallNeedsEveryGoldNoteIncludingGoldLongs()
    {
        var notes = new List<NoteData>(Taps(3, "gold")) { PlayStatsTests.Note(9000, "red") };
        var stats = PlayStatsTests.AllResults(Notes(notes.ToArray()), JudgmentTier.Bad);
        stats.Record(3, JudgmentTier.Miss);
        Assert.IsTrue(Has(Ctx(c => c.Stats = stats), "goldall"), "金以外の MISS は問わない");
        stats.Record(1, JudgmentTier.Miss);
        Assert.IsFalse(Has(Ctx(c => c.Stats = stats), "goldall"));
        var two = PlayStatsTests.AllResults(Notes(Taps(AchievementCatalog.GoldMinNotes - 1, "gold")), JudgmentTier.Perfect);
        Assert.IsFalse(Has(Ctx(c => c.Stats = two), "goldall"), "金が2個");
        // 金のロングは最後まで切れれば PERFECT、残りが MISS なら MISS
        var withLong = PlayStatsTests.AllResults(Notes(PlayStatsTests.Note(1000, "gold"), PlayStatsTests.Note(2000, "gold"),
            PlayStatsTests.Note(3000, "gold", count: 5)), JudgmentTier.Perfect);
        Assert.IsTrue(Has(Ctx(c => c.Stats = withLong), "goldall"));
        withLong.Record(2, JudgmentTier.Miss);
        Assert.IsFalse(Has(Ctx(c => c.Stats = withLong), "goldall"));
    }

    [Test]
    public void DualNeedsSixtyPercentPerfectForRedAndBlue()
    {
        PlayStats Hands(int red, int redPerfect, int blue, int bluePerfect)
        {
            var notes = new List<NoteData>(Taps(red, "red"));
            notes.AddRange(Taps(blue, "blue", 50000));
            notes.AddRange(Taps(20, "gold", 90000)); // 金は数えない
            var stats = PlayStatsTests.AllResults(Notes(notes.ToArray()), JudgmentTier.Great);
            for (int i = 0; i < redPerfect; i++) stats.Record(i, JudgmentTier.Perfect);
            for (int i = 0; i < bluePerfect; i++) stats.Record(red + i, JudgmentTier.Perfect);
            return stats;
        }
        Assert.IsTrue(Has(Ctx(c => c.Stats = Hands(10, 6, 10, 6)), "dual"), "ちょうど 6 割");
        Assert.IsFalse(Has(Ctx(c => c.Stats = Hands(10, 5, 10, 10)), "dual"));
        Assert.IsFalse(Has(Ctx(c => c.Stats = Hands(10, 10, 10, 5)), "dual"));
        Assert.IsFalse(Has(Ctx(c => c.Stats = Hands(9, 9, 20, 20)), "dual"), "赤が 10 個ない");
        Assert.IsTrue(Has(Ctx(c => c.Stats = Hands(30, 18, 15, 9)), "dual"));
    }

    [Test]
    public void ArrowsNeedEveryArrowNoteCut()
    {
        var notes = new[] { PlayStatsTests.Note(1000, direction: "up"), PlayStatsTests.Note(2000, "blue", "left"), PlayStatsTests.Note(3000, "gold", "down"), PlayStatsTests.Note(4000) };
        var stats = PlayStatsTests.AllResults(Notes(notes), JudgmentTier.Bad);
        stats.Record(3, JudgmentTier.Miss);
        Assert.IsTrue(Has(Ctx(c => c.Stats = stats), "arrows"));
        stats.Record(0, JudgmentTier.Miss);
        Assert.IsFalse(Has(Ctx(c => c.Stats = stats), "arrows"));
        var two = PlayStatsTests.AllResults(Notes(PlayStatsTests.Note(1000, direction: "up"), PlayStatsTests.Note(2000, direction: "up")), JudgmentTier.Perfect);
        Assert.IsFalse(Has(Ctx(c => c.Stats = two), "arrows"), "矢印が2個");
    }

    [Test]
    public void FirstcutAndFinaleNeedPerfectGroups()
    {
        var stats = PlayStatsTests.AllResults(Notes(PlayStatsTests.Note(1000), PlayStatsTests.Note(1000, "blue", x: 1), PlayStatsTests.Note(2000), PlayStatsTests.Note(3000)), JudgmentTier.Perfect);
        stats.Record(2, JudgmentTier.Miss);
        Assert.IsTrue(Has(Ctx(c => c.Stats = stats), "firstcut"));
        Assert.IsTrue(Has(Ctx(c => c.Stats = stats), "finale"));
        stats.Record(1, JudgmentTier.Great);
        stats.Record(3, JudgmentTier.Great);
        Assert.IsFalse(Has(Ctx(c => c.Stats = stats), "firstcut"), "同時の組の片方が GREAT");
        Assert.IsFalse(Has(Ctx(c => c.Stats = stats), "finale"));
        Assert.IsFalse(Has(Ctx(c => { c.Stats = Notes(); }), "firstcut"), "ノーツの無い譜面(照準の裏譜面)");
    }

    [Test]
    public void PracticeAndClear()
    {
        Assert.IsTrue(Has(Ctx(c => c.PracticedBefore = true), "practice"));
        Assert.IsFalse(Has(Ctx(), "practice"));
        Assert.IsTrue(Has(Ctx(), "clear"), "記録された回すべて");
    }
}
