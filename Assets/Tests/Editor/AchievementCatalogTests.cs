using System;
using System.Collections.Generic;
using NUnit.Framework;

// 実績の一覧(AchievementCatalog、2026-10-29 の 44 個と第2弾の 32 個・第3弾の 26 個)。並び・段・ID の形と、各実績の取れる/取れないの境目。
public partial class AchievementCatalogTests
{
    // 表示の順(2026-10-29 v2.2 で並べ直し、同日夜に段を見直して並べ直した。簡単なもの → 難しいもの = 段の低い順、同じ段の中は見込みの高い順。clear が最初)。
    static readonly string[] Order =
    {
        "clear", "overtake", "wind", "hard", "xcut", "firstcut", "rainbow", "vanish_four", "practice", "fivekinds", "harvest",
        "silence", "finale", "eclipse", "passed", "goldcoin", "cradle", "two23", "shubu", "goldpair", "gohoubi", "aobakin",
        "lefthand", "regain", "modulate", "arrows", "dark", "m_echo", "dual", "combo50", "reswing", "octave", "onearm",
        "manicbreath", "learnpair", "goldall", "storm", "goodstart", "morningecho", "chorus", "kiriage", "quick", "triple",
        "redblue", "setsujoku", "twinstar", "jackpot", "upside", "matador", "comeback", "kangaii", "lunchtime", "school",
        "ranks", "secret_s", "rushe4", "souage", "xcut10", "restart", "owariyoshi", "mochitsuki", "yosumi", "kinou_koe",
        "holdcut", "streak20", "kouyou5", "senbon", "hitoributai", "pioneer", "nothorn", "backpick", "pairrainbow", "firstever",
        "combo100", "e_chords", "ittoryu", "sasayaki", "sweep3", "meteor", "balance", "top", "lastsec", "duo150k", "bullseye",
        "mirror", "top5", "twinprime", "buzzer", "nakami", "zanshin", "kannon", "bounceback", "sync", "kinou_ima", "rising",
        "scenemaster", "secret", "duet", "seisho", "hintkey", "honjitsu", "trend", "sottoji", "daynight", "fushime", "shushifu",
        "ichiban", "songswap", "omoteura", "tachiai", "lastpiece", "urafutari", "clock", "secretall", "soroiban", "medal3",
        "jihou", "kaikin", "late", "center10", "early", "splus", "almostfc", "hardsplus", "streak50", "goldperfect", "yatsugi",
        "evenscore", "memory", "rhythm_ok", "gobugobu", "slowstart", "kiriban", "zorome", "douten", "kaibun", "chords", "yushu",
        "fullhouse", "hyakuren", "lastrainbow", "fc", "ichien", "slidein", "handperfect", "fubuki", "sanshu", "kengou",
        "achsteps", "neckneck", "quiz", "shingan", "almostap", "fivekeys", "hardfc", "sebango", "steady", "allgood",
        "triplegold", "pairfc", "ap", "datescore",
    };

    // 第2弾の32個(catalog2.md の表の順)。
    static readonly string[] SecondBatch =
    {
        "datescore", "ichien", "restart", "upside", "sebango", "onearm", "vanish_four", "lastpiece", "sottoji", "triple",
        "slidein", "modulate", "eclipse", "jihou", "duo150k", "firstever", "two23", "redblue", "lastsec", "buzzer",
        "harvest", "early", "octave", "twinprime", "storm", "center10", "late", "reswing", "lunchtime", "lefthand",
        "silence", "fivekinds",
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
    public void TheListHas162AchievementsFromTheEasiestWithClearFirst()
    {
        var all = AchievementCatalog.All;
        Assert.AreEqual(162, all.Count);
        var ids = new List<string>();
        for (int i = 0; i < all.Count; i++)
        {
            ids.Add(all[i].Id);
            Assert.AreEqual(i, all[i].Order);
            Assert.IsTrue(AchievementStore.IsValidId(all[i].Id), all[i].Id + " は記録できる ID の形");
            Assert.IsFalse(string.IsNullOrEmpty(all[i].Text), all[i].Id);
            Assert.IsFalse(string.IsNullOrEmpty(all[i].Name), all[i].Id);
            Assert.LessOrEqual(all[i].Text.Length, 20, all[i].Id + " の通知の文は20字以内");
            Assert.LessOrEqual(all[i].Name.Length, 10, all[i].Id + " の名前は10字以内");
            Assert.AreSame(all[i], AchievementCatalog.Find(all[i].Id));
        }
        CollectionAssert.AreEqual(Order, ids);
        CollectionAssert.AllItemsAreUnique(ids);
        CollectionAssert.IsSubsetOf(SecondBatch, ids);
        Assert.AreEqual(AchievementCatalog.ClearId, ids[0], "完走がいちばん簡単(最初に出る)");
        for (int i = 1; i < all.Count; i++)
            Assert.LessOrEqual((int)all[i - 1].Tier, (int)all[i].Tier, all[i - 1].Id + " → " + all[i].Id + ": 段の低い順(簡単なものから)");
        Assert.AreEqual(AchievementTier.None, all[0].Tier);
        Assert.AreEqual(AchievementTier.Rainbow, all[all.Count - 1].Tier);
        Assert.IsNull(AchievementCatalog.Find("nothing"));
        Assert.IsNull(AchievementCatalog.Find(null));
        foreach (string id in AchievementCatalog.ClearNumberIds) Assert.NotNull(AchievementCatalog.Find(id), id);
        foreach (string id in AchievementCatalog.TallyIds) Assert.NotNull(AchievementCatalog.Find(id), id);
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
        CollectionAssert.AreEqual(new[] { "clear", "combo50", "ranks", "combo100", "splus", "fc", "ap" }, earned.ConvertAll(d => d.Id),
            "簡単なものから(段の低い順)");
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
        Assert.IsTrue(Has(Ctx(c => { Song(c, RushESecretState.BenkyouQuiz); c.Stats = Run(JudgmentTier.Good, JudgmentTier.Perfect); }), "quiz"),
            "GOOD 以上が正解");
        Assert.IsFalse(Has(Ctx(c => { Song(c, RushESecretState.BenkyouQuiz); c.Stats = Run(JudgmentTier.Bad, JudgmentTier.Perfect); }), "quiz"),
            "合格点と同じく BAD は正解でない");
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

    // ---- 第2弾(catalog2.md の32個) ----

    static AchievementUnlock U(string id, int number) => new AchievementUnlock(AchievementCatalog.Find(id), number);

    static NoteData Vanish(float timeMs, string vanish)
    {
        var note = PlayStatsTests.Note(timeMs);
        note.vanish = vanish;
        return note;
    }

    [Test]
    public void DatescoreIsTheMonthAndDayOfTheStartInTheLastFourDigits()
    {
        // 曲を始めたのは 10月29日(下4桁 1029)
        Assert.IsTrue(Has(Ctx(c => c.Score = 51029), "datescore"));
        Assert.IsTrue(Has(Ctx(c => c.Score = 11029), "datescore"));
        Assert.IsFalse(Has(Ctx(c => c.Score = 51028), "datescore"));
        Assert.IsFalse(Has(Ctx(c => c.Score = 1029), "datescore"), "1万点未満");
        var nov3 = new DateTime(2026, 11, 3, 15, 0, 0, DateTimeKind.Local);
        Assert.IsTrue(Has(Ctx(c => { c.StartedLocal = nov3; c.EndedLocal = nov3.AddMinutes(3); c.Score = 81103; }), "datescore"), "11月3日は 1103");
        Assert.IsFalse(Has(Ctx(c => { c.StartedLocal = nov3; c.EndedLocal = nov3.AddMinutes(3); c.Score = 80113; }), "datescore"));
        // 日付をまたいだ回は、曲を始めた日
        var lateNight = new DateTime(2026, 10, 28, 23, 59, 0, DateTimeKind.Local);
        Assert.IsTrue(Has(Ctx(c => { c.StartedLocal = lateNight; c.EndedLocal = lateNight.AddMinutes(3); c.Score = 61028; }), "datescore"));
        Assert.IsFalse(Has(Ctx(c => { c.StartedLocal = lateNight; c.EndedLocal = lateNight.AddMinutes(3); c.Score = 61029; }), "datescore"));
    }

    // 1ミスでフルコンボ・1つで AP を逃した人への称号(2026-10-29 ユーザー「1ミスでフルコンとかAP逃した人への称号も出しといて」)。
    [Test]
    public void OneAwayFromFullComboOrAllPerfectGetsItsOwnTitle()
    {
        Assert.IsTrue(Has(Ctx(c => { c.Miss = 1; c.Bad = 0; }), "almostfc"), "MISS が1つ");
        Assert.IsTrue(Has(Ctx(c => { c.Miss = 0; c.Bad = 1; }), "almostfc"), "BAD が1つ");
        Assert.IsFalse(Has(Ctx(c => { c.Miss = 2; c.Bad = 0; }), "almostfc"));
        Assert.IsFalse(Has(Ctx(c => { c.Miss = 1; c.Bad = 1; }), "almostfc"));
        Assert.IsFalse(Has(Ctx(c => { c.Miss = 0; c.Bad = 0; }), "almostfc"), "フルコンボは別の実績");
        Assert.IsFalse(Has(Ctx(c => { c.Miss = 1; c.Hit = 0; c.Perfect = c.Great = c.Good = 0; }), "almostfc"), "1つも当てていない");
        Assert.IsFalse(Has(Ctx(c => { c.Miss = 1; c.AimMode = true; }), "almostfc"), "D は対象外");
        void Fc(AchievementContext c, int perfect, int great, int good) { c.Miss = c.Bad = 0; c.Hit = 10; c.Perfect = perfect; c.Great = great; c.Good = good; }
        Assert.IsTrue(Has(Ctx(c => Fc(c, 9, 1, 0)), "almostap"), "GREAT が1つ");
        Assert.IsTrue(Has(Ctx(c => Fc(c, 9, 0, 1)), "almostap"), "GOOD が1つ");
        Assert.IsFalse(Has(Ctx(c => Fc(c, 8, 2, 0)), "almostap"));
        Assert.IsFalse(Has(Ctx(c => Fc(c, 10, 0, 0)), "almostap"), "AP は別の実績");
        Assert.IsFalse(Has(Ctx(c => { Fc(c, 9, 1, 0); c.Miss = 1; }), "almostap"), "MISS があるとフルコンボでない");
        Assert.IsFalse(Has(Ctx(c => { Fc(c, 9, 1, 0); c.AimMode = true; }), "almostap"), "D は対象外");
        // 2026-10-29 夜の段の見直し: 惜しいフルコンは銀(FC より取りやすい)、惜しい AP は金。
        Assert.AreEqual(AchievementTier.Silver, AchievementCatalog.Find("almostfc").Tier);
        Assert.AreEqual(AchievementTier.Gold, AchievementCatalog.Find("almostap").Tier);
    }

    // D(照準の裏譜面)は点が 1000 点刻みで、命中はどれも PERFECT。点の数字や FC・AP の実績が簡単に取れてしまうので外す(2026-10-29 段の見直し)。
    [Test]
    public void AimModeRunsDoNotGetScoreDigitOrFullComboAchievements()
    {
        void Aim(AchievementContext c, int score) { c.AimMode = true; c.Hit = 50; c.Perfect = 50; c.Great = c.Good = c.Bad = c.Miss = 0; c.AimDamage = 0; c.Score = score; }
        void Normal(AchievementContext c, int score) { c.Hit = 50; c.Perfect = 50; c.Great = c.Good = c.Bad = c.Miss = 0; c.Score = score; }
        Assert.IsTrue(Has(Ctx(c => Normal(c, 85000)), "zorome"), "ふつうの譜面の 85,000 点は下3桁 000 のゾロ目");
        Assert.IsFalse(Has(Ctx(c => Aim(c, 85000)), "zorome"), "D の 1000 点刻みは必ずゾロ目になるので外す");
        Assert.IsTrue(Has(Ctx(c => Normal(c, 80050)), "slidein"));
        Assert.IsFalse(Has(Ctx(c => Aim(c, 80000)), "slidein"));
        Assert.IsTrue(Has(Ctx(c => Normal(c, 100000)), "fc"));
        Assert.IsTrue(Has(Ctx(c => Normal(c, 100000)), "ap"));
        Assert.IsFalse(Has(Ctx(c => Aim(c, 100000)), "fc"), "D の全弾命中は FC ではない");
        Assert.IsFalse(Has(Ctx(c => Aim(c, 100000)), "ap"), "D の全弾命中は AP ではない");
    }

    [Test]
    public void IchienIsExactlyTwoHundredCuts()
    {
        Assert.AreEqual(200, AchievementCatalog.PricePerPlay, "1プレイ200円(2026-10-29 に 300円から変わった)");
        StringAssert.StartsWith("200円ぶん", AchievementCatalog.Find("ichien").Text);
        Assert.IsTrue(Has(Ctx(c => c.CutCount = AchievementCatalog.PricePerPlay), "ichien"));
        Assert.IsFalse(Has(Ctx(c => c.CutCount = AchievementCatalog.PricePerPlay - 1), "ichien"));
        Assert.IsFalse(Has(Ctx(c => c.CutCount = AchievementCatalog.PricePerPlay + 1), "ichien"));
    }

    // S: 流れる速さが 0.75 秒以上 0 のままの止まりそれぞれの、止まりが終わったあと最初の組(短い止まりは数えない)。
    [Test]
    public void RestartNeedsEveryFirstGroupAfterALongStopPerfectOnS()
    {
        var chart = PlayStatsTests.Chart(PlayStatsTests.Note(500), PlayStatsTests.Note(2000), PlayStatsTests.Note(2500),
            PlayStatsTests.Note(4000), PlayStatsTests.Note(6000));
        chart.offsetMs = 100f;
        chart.scroll.Add(new ScrollSpeedData { time = 1000, speed = 0 });
        chart.scroll.Add(new ScrollSpeedData { time = 1750, speed = 1 });   // 0.75 秒ちょうど
        chart.scroll.Add(new ScrollSpeedData { time = 3000, speed = 0 });
        chart.scroll.Add(new ScrollSpeedData { time = 3500, speed = 1 });   // 0.5 秒は短い
        chart.scroll.Add(new ScrollSpeedData { time = 5000, speed = 0 });
        chart.scroll.Add(new ScrollSpeedData { time = 5900, speed = 2 });
        PlayStats Run(JudgmentTier first, JudgmentTier second)
        {
            var stats = PlayStatsTests.AllResults(PlayStatsTests.Stats(chart), JudgmentTier.Miss);
            stats.Record(1, first);
            stats.Record(4, second);
            return stats;
        }
        void S(AchievementContext c) => Song(c, RushESecretState.LogicSpeed);
        Assert.IsTrue(Has(Ctx(c => { S(c); c.Stats = Run(JudgmentTier.Perfect, JudgmentTier.Perfect); }), "restart"), "止まりのあとの組のほかは問わない");
        Assert.IsFalse(Has(Ctx(c => { S(c); c.Stats = Run(JudgmentTier.Perfect, JudgmentTier.Great); }), "restart"), "止まり全部");
        Assert.IsFalse(Has(Ctx(c => { S(c); c.Stats = Run(JudgmentTier.Great, JudgmentTier.Perfect); }), "restart"));
        Assert.IsFalse(Has(Ctx(c => c.Stats = Run(JudgmentTier.Perfect, JudgmentTier.Perfect)), "restart"), "S のときだけ");
        var noStop = PlayStatsTests.AllResults(Notes(Taps(4)), JudgmentTier.Perfect);
        Assert.IsFalse(Has(Ctx(c => { S(c); c.Stats = noStop; }), "restart"), "止まりの無い譜面");
    }

    // R: 最初の回転の始まりの組(無ければ 0.1 秒以内のいちばん近い組)と、回転が終わったあとの最初の2組。
    [Test]
    public void UpsideNeedsTheTurnStartAndTheTwoGroupsAfterItPerfectOnR()
    {
        ChartData Chart(float hingeMs)
        {
            var chart = PlayStatsTests.Chart(PlayStatsTests.Note(1000), PlayStatsTests.Note(hingeMs, "gold"), PlayStatsTests.Note(3000),
                PlayStatsTests.Note(4500, "blue", x: -1), PlayStatsTests.Note(5000, "blue", x: -1), PlayStatsTests.Note(5000, "red", x: 1),
                PlayStatsTests.Note(6000));
            chart.reverse.enabled = true;
            chart.reverse.turns.Add(new ReverseTurnData { time = 9000, endTime = 9500, degrees = 360 });   // 2回目(並びの順は問わない)
            chart.reverse.turns.Add(new ReverseTurnData { time = 2000, endTime = 4000, degrees = 180 });
            return chart;
        }
        PlayStats Run(ChartData chart, params int[] great)
        {
            var stats = PlayStatsTests.AllResults(PlayStatsTests.Stats(chart), JudgmentTier.Perfect);
            stats.Record(0, JudgmentTier.Miss);
            stats.Record(2, JudgmentTier.Miss);
            stats.Record(6, JudgmentTier.Miss);
            foreach (int k in great) stats.Record(k, JudgmentTier.Great);
            return stats;
        }
        void R(AchievementContext c) => Song(c, RushESecretState.ManicSonicReverse);
        Assert.IsTrue(Has(Ctx(c => { R(c); c.Stats = Run(Chart(2000)); }), "upside"), "回転中・3組目より後は問わない");
        foreach (int k in new[] { 1, 3, 4, 5 })
            Assert.IsFalse(Has(Ctx(c => { R(c); c.Stats = Run(Chart(2000), k); }), "upside"), "ノーツ " + k);
        Assert.IsTrue(Has(Ctx(c => { R(c); c.Stats = Run(Chart(2080)); }), "upside"), "始まりから 0.1 秒以内のいちばん近い組");
        Assert.IsFalse(Has(Ctx(c => { R(c); c.Stats = Run(Chart(2080), 1); }), "upside"));
        Assert.IsFalse(Has(Ctx(c => { R(c); c.Stats = Run(Chart(2200)); }), "upside"), "始まりから 0.1 秒以内に組が無ければ、場面がそろっていない");
        Assert.IsFalse(Has(Ctx(c => c.Stats = Run(Chart(2000))), "upside"), "R のときだけ");
        var off = Chart(2000);
        off.reverse.enabled = false;
        Assert.IsFalse(Has(Ctx(c => { R(c); c.Stats = Run(off); }), "upside"), "回転の無い譜面");
    }

    [Test]
    public void SebangoMatchesTheLastThreeDigitsOfTheClearNumber()
    {
        Assert.IsTrue(Has(Ctx(c => { c.ClearNumber = 37; c.Score = 71037; }), "sebango"), "37人目で 71,037点");
        Assert.IsTrue(Has(Ctx(c => { c.ClearNumber = 1037; c.Score = 71037; }), "sebango"), "下3桁どうし");
        Assert.IsTrue(Has(Ctx(c => { c.ClearNumber = 1000; c.Score = 52000; }), "sebango"));
        Assert.IsFalse(Has(Ctx(c => { c.ClearNumber = 37; c.Score = 71038; }), "sebango"));
        Assert.IsFalse(Has(Ctx(c => { c.ClearNumber = 37; c.Score = 37; }), "sebango"), "1000点未満");
        Assert.IsFalse(Has(Ctx(c => { c.ClearNumber = 0; c.Score = 71000; }), "sebango"), "番号が無い");
    }

    // T: 反対の剣がしびれている間に、切った剣の側にある金を切った。
    [Test]
    public void OnearmNeedsAGoldOnTheCuttingSideWhileTheOtherSaberIsStunned()
    {
        var chart = PlayStatsTests.Chart(PlayStatsTests.Note(1000, "gold", x: 1), PlayStatsTests.Note(2000, "gold", x: -1),
            PlayStatsTests.Note(3000, "gold"), PlayStatsTests.Note(4000, "red", x: 1));
        PlayStats Cut(int k, JudgmentTier tier, SaberHand hand, bool stunned)
        {
            var stats = PlayStatsTests.AllResults(PlayStatsTests.Stats(chart), JudgmentTier.Perfect);
            stats.Record(k, tier, null, null, hand, stunned);
            return stats;
        }
        bool T(PlayStats stats) => Has(Ctx(c => { Song(c, RushESecretState.AndalusiaBull); c.Stats = stats; }), "onearm");
        Assert.IsTrue(T(Cut(0, JudgmentTier.Good, SaberHand.Right, true)), "右の剣で右の金");
        Assert.IsTrue(T(Cut(1, JudgmentTier.Bad, SaberHand.Left, true)), "左の剣で左の金(MISS でなければ)");
        Assert.IsTrue(T(Cut(2, JudgmentTier.Perfect, SaberHand.Left, true)), "まん中(x = 0)");
        Assert.IsFalse(T(Cut(0, JudgmentTier.Perfect, SaberHand.Left, true)), "腕を交差させた");
        Assert.IsFalse(T(Cut(0, JudgmentTier.Perfect, SaberHand.Right, false)), "しびれていない");
        Assert.IsFalse(T(Cut(0, JudgmentTier.Miss, SaberHand.Right, true)));
        Assert.IsFalse(T(Cut(3, JudgmentTier.Perfect, SaberHand.Right, true)), "金だけ");
        Assert.IsFalse(T(Cut(0, JudgmentTier.Perfect, SaberHand.Any, true)), "剣でない");
        Assert.IsFalse(Has(Ctx(c => c.Stats = Cut(0, JudgmentTier.Good, SaberHand.Right, true)), "onearm"), "T のときだけ");
    }

    [Test]
    public void VanishFourNeedsThreeSuccessesOfEveryKind()
    {
        var notes = new List<NoteData>();
        float t = 1000;
        foreach (string kind in new[] { "fade", "sudden", "blink", "hide" })
            for (int i = 0; i <= AchievementCatalog.VanishKindSuccesses; i++) notes.Add(Vanish(t += 300, kind));
        notes.Add(PlayStatsTests.Note(t + 300));
        var chart = PlayStatsTests.Chart(notes.ToArray());
        PlayStats Run(params int[] bad)
        {
            var stats = PlayStatsTests.AllResults(PlayStatsTests.Stats(chart), JudgmentTier.Good);
            stats.Record(notes.Count - 1, JudgmentTier.Miss);   // 消えないノーツは問わない
            foreach (int k in bad) stats.Record(k, JudgmentTier.Bad);
            return stats;
        }
        bool V(PlayStats stats) => Has(Ctx(c => { Song(c, RushESecretState.ZeroVanish); c.Stats = stats; }), "vanish_four");
        Assert.IsTrue(V(Run()), "GOOD も成功");
        Assert.IsTrue(V(Run(12)), "hide の4個のうち3個");
        Assert.IsFalse(V(Run(12, 13)), "hide が2個");
        Assert.IsFalse(V(Run(0, 1)), "fade が2個");
        Assert.IsFalse(Has(Ctx(c => c.Stats = Run()), "vanish_four"), "V のときだけ");
    }

    // 今日の2台の回(この回は入れない)で、選曲に出る全曲のうちこの回の曲だけが欠けている。
    [Test]
    public void LastpieceIsTheOneSongMissingFromTodaysPlays()
    {
        string today = Morning.ToString("yyyy/MM/dd", System.Globalization.CultureInfo.InvariantCulture);
        RankingPlay Play(string song, string date = null, string ns = "", string station = "B", string id = null) => new RankingPlay
        {
            id = id ?? Guid.NewGuid().ToString("N"), song = song, diff = "Normal", st = station, ns = ns, date = date ?? today, at = 1, score = 1,
            rank = "C", acc = .5f,
        };
        bool With(params RankingPlay[] plays) =>
            Has(Ctx(c => { c.SongId = "C"; c.ListedSongs = new[] { "A", "B", "C" }; c.Plays = plays; }), "lastpiece");
        Assert.IsTrue(With(Play("A"), Play("B", station: "A")), "2台の今日の回(難易度・台は問わない)");
        Assert.IsTrue(With(Play("A"), Play("B"), Play("A")));
        Assert.IsFalse(With(Play("A"), Play("B"), Play("C")), "この回の曲はもう遊ばれていた");
        Assert.IsFalse(With(Play("A")), "B が欠けている");
        Assert.IsFalse(With(Play("A"), Play("B", date: "2026/10/28")), "昨日の回");
        Assert.IsFalse(With(Play("A"), Play("B", ns: OperatorMode.FestivalNamespace)), "別のモードの回");
        Assert.IsFalse(With(Play("A"), Play("B", id: "run00001")), "この回そのもの");
        Assert.IsFalse(Has(Ctx(c => { c.SongId = "D"; c.ListedSongs = new[] { "A", "B", "C" }; c.Plays = new[] { Play("A"), Play("B"), Play("C") }; }),
            "lastpiece"), "選曲に出ない曲");
        Assert.IsFalse(Has(Ctx(c => { c.SongId = "C"; c.Plays = new[] { Play("A"), Play("B") }; }), "lastpiece"), "全曲が分からない");
    }

    [Test]
    public void SottojiIsANormalChartAfterOpeningItsSecretInThatSelect()
    {
        Assert.IsTrue(Has(Ctx(c => { c.OpenedSecretHere = true; c.Difficulty = "Normal"; }), "sottoji"));
        Assert.IsTrue(Has(Ctx(c => { c.OpenedSecretHere = true; c.Difficulty = "Easy"; }), "sottoji"));
        Assert.IsFalse(Has(Ctx(c => { c.OpenedSecretHere = true; Song(c, RushESecretState.RushE); }), "sottoji"), "裏譜面を遊んだ");
        Assert.IsFalse(Has(Ctx(c => c.Difficulty = "Hard"), "sottoji"), "裏譜面を出していない");
    }

    [Test]
    public void TripleNeedsAGroupOfThreeAllPerfect()
    {
        var notes = new[]
        {
            PlayStatsTests.Note(1000, "blue", x: -1), PlayStatsTests.Note(1000.5f, "blue", "up", x: -.5f), PlayStatsTests.Note(1000, "gold", x: 1),
            PlayStatsTests.Note(2000, "red", x: 1), PlayStatsTests.Note(2000, "blue", x: -1),
        };
        PlayStats Run(int great = -1)
        {
            var stats = PlayStatsTests.AllResults(Notes(notes), JudgmentTier.Perfect);
            if (great >= 0) stats.Record(great, JudgmentTier.Great);
            return stats;
        }
        Assert.IsTrue(Has(Ctx(c => c.Stats = Run()), "triple"), "1ms 以内の3個");
        Assert.IsTrue(Has(Ctx(c => c.Stats = Run(3)), "triple"), "2個の組は問わない");
        Assert.IsFalse(Has(Ctx(c => c.Stats = Run(1)), "triple"));
        var apart = PlayStatsTests.AllResults(Notes(PlayStatsTests.Note(1000, "blue"), PlayStatsTests.Note(1000, "red"), PlayStatsTests.Note(1002, "gold")),
            JudgmentTier.Perfect);
        Assert.IsFalse(Has(Ctx(c => c.Stats = apart), "triple"), "2ms 離れたら別の組");
        var withLong = PlayStatsTests.AllResults(Notes(PlayStatsTests.Note(1000, "blue"), PlayStatsTests.Note(1000, "red"),
            PlayStatsTests.Note(1000, "gold", count: 4)), JudgmentTier.Perfect);
        Assert.IsTrue(Has(Ctx(c => c.Stats = withLong), "triple"), "ロングは最後まで切れたら PERFECT");
        withLong.Record(2, JudgmentTier.Miss);
        Assert.IsFalse(Has(Ctx(c => c.Stats = withLong), "triple"));
    }

    [Test]
    public void SlideinIsUnderAHundredPointsAboveARankLine()
    {
        foreach (int score in new[] { 40000, 40099, 60000, 60099, 80050, 90099 })
            Assert.IsTrue(Has(Ctx(c => c.Score = score), "slidein"), score.ToString());
        foreach (int score in new[] { 0, 39999, 40100, 59999, 60100, 89999, 90100, 100000 })
            Assert.IsFalse(Has(Ctx(c => c.Score = score), "slidein"), score.ToString());
    }

    // 校歌: 名前が「転調後の主題」で始まる区間の、始まりの 0.05 秒前より後の最初の組(どの難易度でも)。
    [Test]
    public void ModulateNeedsTheFirstGroupAfterTheKeyChangePerfect()
    {
        var stage = new StagePerformanceTimeline
        {
            sections = new[]
            {
                new StagePerformanceTimeline.Section { startSeconds = 5, endSeconds = 9, intensity = .65f, label = "主旋律の展開（PDF第28〜41小節）" },
                new StagePerformanceTimeline.Section { startSeconds = 10, endSeconds = 20, intensity = .72f, label = "転調後の主題（PDF第53〜70小節）" },
            },
        };
        var chart = PlayStatsTests.Chart(PlayStatsTests.Note(8940), PlayStatsTests.Note(8960, "blue", x: -1), PlayStatsTests.Note(8960, "red", x: 1),
            PlayStatsTests.Note(9500));
        chart.offsetMs = 1000f;   // 区間は音源の時刻、ノーツは time + offsetMs(9.94秒・9.96秒・10.5秒)
        PlayStats Run(JudgmentTier target)
        {
            var stats = PlayStatsTests.AllResults(PlayStatsTests.Stats(chart, stage), JudgmentTier.Miss);
            stats.Record(1, target);
            stats.Record(2, JudgmentTier.Perfect);
            return stats;
        }
        void School(AchievementContext c, string difficulty) { c.SongId = RushESecretState.EpilogueFall.SongId; c.Difficulty = difficulty; }
        Assert.IsTrue(Has(Ctx(c => { School(c, "Normal"); c.Stats = Run(JudgmentTier.Perfect); }), "modulate"), "9.96秒の組(9.94秒は 0.05 秒前より前)");
        Assert.IsTrue(Has(Ctx(c => { School(c, RushESecretState.EpilogueFall.Difficulty); c.Stats = Run(JudgmentTier.Perfect); }), "modulate"), "F も");
        Assert.IsFalse(Has(Ctx(c => { School(c, "Easy"); c.Stats = Run(JudgmentTier.Great); }), "modulate"));
        Assert.IsFalse(Has(Ctx(c => c.Stats = Run(JudgmentTier.Perfect)), "modulate"), "校歌のときだけ");
        var unnamed = new StagePerformanceTimeline { sections = new[] { new StagePerformanceTimeline.Section { startSeconds = 10, endSeconds = 20 } } };
        Assert.IsFalse(Has(Ctx(c => { School(c, "Normal"); c.Stats = PlayStatsTests.AllResults(PlayStatsTests.Stats(chart, unnamed), JudgmentTier.Perfect); }),
            "modulate"), "転調の区間が無い");
    }

    // 背景が AstralOrbit の曲で、食がいちばん濃い間(EvaluateEclipse ≥ 0.99)のノーツが1個以上で全部 PERFECT。
    [Test]
    public void EclipseNeedsEveryNoteAtTotalityPerfectOnAstralOrbit()
    {
        StagePerformanceTimeline Stage(string theme) => new StagePerformanceTimeline
        {
            stageTheme = theme,
            sections = new[] { new StagePerformanceTimeline.Section { startSeconds = 100, endSeconds = 116, intensity = 1f } },
        };
        // 食は区間のまん中の16秒で入り戻り、108秒がいちばん濃い。108.4秒は 0.99 以上、109秒は届かない。
        var chart = PlayStatsTests.Chart(PlayStatsTests.Note(50000), PlayStatsTests.Note(108000, "gold"), PlayStatsTests.Note(108400),
            PlayStatsTests.Note(109000));
        PlayStats Run(string theme, params int[] great)
        {
            var stats = PlayStatsTests.AllResults(PlayStatsTests.Stats(chart, Stage(theme)), JudgmentTier.Perfect);
            stats.Record(0, JudgmentTier.Miss);
            stats.Record(3, JudgmentTier.Miss);
            foreach (int k in great) stats.Record(k, JudgmentTier.Great);
            return stats;
        }
        Assert.GreaterOrEqual(Stage("AstralOrbit").EvaluateEclipse(108.4), AchievementCatalog.EclipseMin);
        Assert.Less(Stage("AstralOrbit").EvaluateEclipse(109), AchievementCatalog.EclipseMin);
        Assert.IsTrue(Has(Ctx(c => c.Stats = Run("AstralOrbit")), "eclipse"));
        Assert.IsFalse(Has(Ctx(c => c.Stats = Run("AstralOrbit", 2)), "eclipse"));
        Assert.IsFalse(Has(Ctx(c => c.Stats = Run("VioletVault")), "eclipse"), "ほかの背景には食が無い");
        var outside = PlayStatsTests.AllResults(PlayStatsTests.Stats(PlayStatsTests.Chart(PlayStatsTests.Note(50000), PlayStatsTests.Note(109000)),
            Stage("AstralOrbit")), JudgmentTier.Perfect);
        Assert.IsFalse(Has(Ctx(c => c.Stats = outside), "eclipse"), "いちばん濃い間にノーツが無い");
    }

    [Test]
    public void JihouIsAResultInTheFirstSecondOfAnHour()
    {
        var day = new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Local);
        PlayStats At(DateTime at, JudgmentTier tier = JudgmentTier.Good)
        {
            var stats = PlayStatsTests.AllResults(Notes(Taps(3)), JudgmentTier.Perfect);
            stats.Record(1, tier, 5, tier == JudgmentTier.Miss ? (DateTime?)null : at, SaberHand.Right, false);
            return stats;
        }
        Assert.IsTrue(Has(Ctx(c => c.Stats = At(day.AddHours(13))), "jihou"), "13:00:00.000");
        Assert.IsTrue(Has(Ctx(c => c.Stats = At(day.AddHours(13).AddMilliseconds(999))), "jihou"), "13:00:00.999");
        Assert.IsTrue(Has(Ctx(c => c.Stats = At(day.AddHours(10), JudgmentTier.Bad)), "jihou"), "BAD でも");
        Assert.IsFalse(Has(Ctx(c => c.Stats = At(day.AddHours(13).AddSeconds(1))), "jihou"), "13:00:01");
        Assert.IsFalse(Has(Ctx(c => c.Stats = At(day.AddHours(13).AddMilliseconds(-1))), "jihou"), "12:59:59.999");
        Assert.IsFalse(Has(Ctx(c => c.Stats = At(day.AddHours(13).AddMinutes(30))), "jihou"), "13:30:00");
        Assert.IsFalse(Has(Ctx(c => c.Stats = At(day.AddHours(13), JudgmentTier.Miss)), "jihou"), "MISS");
    }

    [Test]
    public void Duo150kAddsTheClosestNeighborsScore()
    {
        long endMs = new DateTimeOffset(Morning).ToUnixTimeMilliseconds();
        RankingPlay Play(long agoMs, int score, string station = "B", string ns = "") => new RankingPlay
        {
            id = Guid.NewGuid().ToString("N"), song = "ElDorado", diff = "Hard", st = station, ns = ns, at = endMs - agoMs, score = score, acc = .5f,
            rank = "C", date = "2026/10/29",
        };
        bool With(int score, params RankingPlay[] plays) => Has(Ctx(c => { c.Score = score; c.Plays = plays; }), "duo150k");
        Assert.IsTrue(With(70000, Play(30000, 80000)), "曲・難易度は問わない");
        Assert.IsTrue(With(70000, Play(-60000, 80000)), "前後60秒");
        Assert.IsFalse(With(70000, Play(30000, 79999)));
        Assert.IsFalse(With(70000, Play(60001, 80000)), "60秒より前");
        Assert.IsFalse(With(70000, Play(30000, 80000, "A")), "自分の台");
        Assert.IsFalse(With(70000, Play(30000, 80000, ns: OperatorMode.FestivalNamespace)), "別のモード");
        Assert.IsFalse(With(70000, Play(5000, 10000), Play(50000, 90000)), "となりの回は差がいちばん小さい1回");
        Assert.IsTrue(With(70000, Play(50000, 10000), Play(-5000, 90000)));
        Assert.IsFalse(With(100000), "となりの回が無い");
    }

    // 初物: この回でほかに解除した実績(clear・キリ番・初物・大収穫を除く)のどれかが番号 1。
    [Test]
    public void FirsteverNeedsANumberOneAmongTheOtherUnlocks()
    {
        bool With(params AchievementUnlock[] unlocks) => Has(Ctx(c => c.Unlocked = unlocks), "firstever");
        Assert.IsTrue(With(U("fc", 1), U("clear", 37)));
        Assert.IsTrue(With(U("sebango", 1), U("clear", 37)), "clear の番号を見る実績(背番号・双子の素数)も");
        Assert.IsFalse(With(U("fc", 2), U("clear", 1)), "clear の1人目");
        Assert.IsFalse(With(U("kiriban", 1), U("clear", 100)), "キリ番の1人目(100人目)");
        Assert.IsFalse(With(U("harvest", 1), U("firstever", 1)));
        Assert.IsFalse(With(U("fc", 0)), "番号の無い解除(記録が読めない)");
        Assert.IsFalse(With());
    }

    [Test]
    public void HarvestNeedsEightOtherUnlocks()
    {
        var eight = new[] { U("ap", 3), U("fc", 9), U("splus", 4), U("combo100", 20), U("ranks", 40), U("combo50", 51), U("kiriban", 2), U("sebango", 1) };
        Assert.AreEqual(AchievementCatalog.HarvestCount, eight.Length);
        Assert.IsTrue(Has(Ctx(c => c.Unlocked = eight), "harvest"));
        var seven = new List<AchievementUnlock>(eight);
        seven.RemoveAt(0);
        seven.Add(U("clear", 300));
        seven.Add(U("firstever", 1));
        Assert.IsFalse(Has(Ctx(c => c.Unlocked = seven), "harvest"), "clear と初物は数えない");
    }

    // 2:23 AM: 譜面の時刻が 143 秒にいちばん近い組(差 1 秒以内)。どの難易度でも。
    [Test]
    public void Two23NeedsTheGroupNearest143SecondsPerfect()
    {
        var chart = PlayStatsTests.Chart(PlayStatsTests.Note(141000), PlayStatsTests.Note(142693, "gold", "down", x: -1),
            PlayStatsTests.Note(142693, "gold", "down", x: 1), PlayStatsTests.Note(143800));
        PlayStats Run(params int[] great)
        {
            var stats = PlayStatsTests.AllResults(PlayStatsTests.Stats(chart), JudgmentTier.Perfect);
            stats.Record(0, JudgmentTier.Miss);
            stats.Record(3, JudgmentTier.Miss);
            foreach (int k in great) stats.Record(k, JudgmentTier.Great);
            return stats;
        }
        void AM(AchievementContext c, string difficulty) { c.SongId = RushESecretState.TwoTwentyThreeLight.SongId; c.Difficulty = difficulty; }
        Assert.IsTrue(Has(Ctx(c => { AM(c, "Hard"); c.Stats = Run(); }), "two23"), "142.693秒の組(143.8秒より近い)");
        Assert.IsTrue(Has(Ctx(c => { AM(c, RushESecretState.TwoTwentyThreeLight.Difficulty); c.Stats = Run(); }), "two23"), "Y も");
        Assert.IsFalse(Has(Ctx(c => { AM(c, "Normal"); c.Stats = Run(2); }), "two23"));
        Assert.IsFalse(Has(Ctx(c => c.Stats = Run()), "two23"), "2:23 AM のときだけ");
        var far = PlayStatsTests.AllResults(Notes(PlayStatsTests.Note(141500), PlayStatsTests.Note(144500)), JudgmentTier.Perfect);
        Assert.IsFalse(Has(Ctx(c => { AM(c, "Easy"); c.Stats = far; }), "two23"), "1秒以内に組が無い");
    }

    // 1個だけの組の赤・青のタップが赤青交互に12個、全部成功(金・矢印・ロング・2個以上の組・成功でない結果・同じ色で数え直し)。
    [Test]
    public void RedblueNeedsTwelveAlternatingSingleTaps()
    {
        PlayStats Relay(int count, string first = "red", Action<List<NoteData>> change = null, Action<PlayStats> results = null)
        {
            var notes = new List<NoteData>();
            for (int i = 0; i < count; i++) notes.Add(PlayStatsTests.Note(1000 + i * 300, (i % 2 == 0) == (first == "red") ? "red" : "blue"));
            change?.Invoke(notes);
            var stats = PlayStatsTests.AllResults(Notes(notes.ToArray()), JudgmentTier.Good);
            results?.Invoke(stats);
            return stats;
        }
        int n = AchievementCatalog.RelayNotes;
        Assert.IsTrue(Has(Ctx(c => c.Stats = Relay(n)), "redblue"), "GOOD も成功");
        Assert.IsTrue(Has(Ctx(c => c.Stats = Relay(n, "blue")), "redblue"), "青から始めても");
        Assert.IsFalse(Has(Ctx(c => c.Stats = Relay(n - 1)), "redblue"));
        Assert.IsFalse(Has(Ctx(c => c.Stats = Relay(n, results: s => s.Record(5, JudgmentTier.Bad))), "redblue"), "BAD で数え直し");
        Assert.IsTrue(Has(Ctx(c => c.Stats = Relay(n + 6, results: s => s.Record(5, JudgmentTier.Bad))), "redblue"), "数え直してから12個");
        Assert.IsFalse(Has(Ctx(c => c.Stats = Relay(n, change: l => l[6].color = "gold")), "redblue"), "金");
        Assert.IsFalse(Has(Ctx(c => c.Stats = Relay(n, change: l => l[6].direction = "up")), "redblue"), "矢印");
        Assert.IsFalse(Has(Ctx(c => c.Stats = Relay(n, change: l => l[6].count = 3)), "redblue"), "ロング");
        Assert.IsFalse(Has(Ctx(c => c.Stats = Relay(n, change: l => l[6].color = l[5].color)), "redblue"), "同じ色が続いた");
        Assert.IsFalse(Has(Ctx(c => c.Stats = Relay(n, change: l => l.Add(PlayStatsTests.Note(l[6].time, l[6].color == "red" ? "blue" : "red", x: 1)))),
            "redblue"), "2個の組");
    }

    [Test]
    public void LastsecIsAManualStartInTheLastFiveSeconds()
    {
        // 選曲の制限(100→60→45 秒、2026-10-30)に合わせて数える。
        double start = AchievementRunInfo.SelectionStartSeconds;
        Assert.AreEqual(SongSelectCountdown.LimitSeconds, start);
        foreach (double seconds in new[] { start - AchievementCatalog.LastSecondsLeft, start - .01 })
            Assert.IsTrue(Has(Ctx(c => c.SelectionSeconds = seconds), "lastsec"), seconds.ToString());
        Assert.IsFalse(Has(Ctx(c => c.SelectionSeconds = start - AchievementCatalog.LastSecondsLeft - .01), "lastsec"));
        Assert.IsFalse(Has(Ctx(c => c.SelectionSeconds = AchievementRunInfo.SelectionElapsed(0)), "lastsec"), "時間切れの自動開始は制限ちょうど");
        Assert.IsFalse(Has(Ctx(c => c.SelectionSeconds = null), "lastsec"), "判定調整から戻った選曲");
    }

    // 最後の組の最初の判定の直前と、最後の判定の直後の HUD の点でランクが上がった(曲の終わりの点のランクがそれ以上)。
    [Test]
    public void BuzzerRaisesTheRankInTheFinalGroup()
    {
        PlayStats Final(params (int before, int after)[] judgments)
        {
            var stats = PlayStatsTests.AllResults(Notes(Taps(3)), JudgmentTier.Perfect);
            foreach (var (before, after) in judgments) stats.FinalJudgment(before, after);
            return stats;
        }
        Assert.IsTrue(Has(Ctx(c => { c.Score = 80050; c.Stats = Final((79950, 80050)); }), "buzzer"), "A から S");
        Assert.IsTrue(Has(Ctx(c => { c.Score = 80300; c.Stats = Final((79700, 79990), (79990, 80300)); }), "buzzer"),
            "組の最初の判定の直前から、最後の判定の直後まで");
        Assert.IsFalse(Has(Ctx(c => { c.Score = 80300; c.Stats = Final((80010, 80300)); }), "buzzer"), "S のまま");
        Assert.IsFalse(Has(Ctx(c => { c.Score = 79000; c.Stats = Final((79950, 80050)); }), "buzzer"), "曲の終わりの点のランクが下");
        Assert.IsFalse(Has(Ctx(c => { c.Score = 80050; c.Stats = Final(); }), "buzzer"), "最後の組の判定が無い");
        var late = Final((79950, 80050));
        late.Complete = false;
        Assert.IsFalse(Has(Ctx(c => { c.Score = 80050; c.Stats = late; }), "buzzer"), "途中から記録した回");
    }

    // ずれ方の癖: 誤差のある判定が30個以上、PERFECT 以外が5個以上で、その誤差がすべて早め(遅め)。
    [Test]
    public void EarlyAndLateNeedEveryOffBeatToLeanOneWay()
    {
        PlayStats Errors(int judged, params double[] offBeats)
        {
            var stats = Notes(Taps(judged + 2));
            for (int k = 0; k < judged; k++)
            {
                bool off = k < offBeats.Length;
                stats.Record(k, off ? JudgmentTier.Great : JudgmentTier.Perfect, off ? offBeats[k] : k % 2 == 0 ? 8 : -8, DateTime.Now, SaberHand.Right, false);
            }
            stats.Record(judged, JudgmentTier.Miss);   // MISS と、誤差の無い判定(ロング)は数えない
            stats.Record(judged + 1, JudgmentTier.Bad);
            return stats;
        }
        int min = AchievementCatalog.LeanMinJudged;
        var early = new[] { -60.0, -75, -90, -55, -99 };
        Assert.IsTrue(Has(Ctx(c => c.Stats = Errors(min, early)), "early"));
        Assert.IsFalse(Has(Ctx(c => c.Stats = Errors(min, early)), "late"));
        Assert.IsFalse(Has(Ctx(c => c.Stats = Errors(min - 1, early)), "early"), "誤差のある判定が29個");
        Assert.IsFalse(Has(Ctx(c => c.Stats = Errors(min, -60, -75, -90, -55)), "early"), "PERFECT 以外が4個");
        Assert.IsFalse(Has(Ctx(c => c.Stats = Errors(min, -60, -75, -90, -55, 95)), "early"), "1個が遅め");
        Assert.IsFalse(Has(Ctx(c => c.Stats = Errors(min, -60, -75, -90, -55, 0)), "early"), "0ms はどちらでもない");
        var late = new[] { 95.0, 100, 130, 150, 170 };
        Assert.IsTrue(Has(Ctx(c => c.Stats = Errors(min, late)), "late"));
        Assert.IsFalse(Has(Ctx(c => c.Stats = Errors(min, late)), "early"));
    }

    [Test]
    public void OctaveNeedsALongOfNineCutsCutToTheEnd()
    {
        Assert.AreEqual(9, AchievementCatalog.OctaveCuts, "途中の鐘の8段(G5〜G6)と最後の1回");
        PlayStats Long(int cuts, JudgmentTier tier)
        {
            var stats = PlayStatsTests.AllResults(Notes(PlayStatsTests.Note(1000), PlayStatsTests.Note(3000, "gold", count: cuts)), JudgmentTier.Perfect);
            stats.Record(1, tier);
            return stats;
        }
        Assert.IsTrue(Has(Ctx(c => c.Stats = Long(AchievementCatalog.OctaveCuts, JudgmentTier.Perfect)), "octave"));
        Assert.IsTrue(Has(Ctx(c => c.Stats = Long(20, JudgmentTier.Perfect)), "octave"));
        Assert.IsFalse(Has(Ctx(c => c.Stats = Long(AchievementCatalog.OctaveCuts - 1, JudgmentTier.Perfect)), "octave"));
        Assert.IsFalse(Has(Ctx(c => c.Stats = Long(AchievementCatalog.OctaveCuts, JudgmentTier.Miss)), "octave"), "最後まで切れなかった");
    }

    [Test]
    public void TwinprimeNeedsThisClearAndTheOneTwoBeforeToBePrimes()
    {
        foreach (int n in new[] { 5, 7, 13, 19, 31, 43, 61, 73, 103, 109, 1021 })
            Assert.IsTrue(Has(Ctx(c => c.ClearNumber = n), "twinprime"), n.ToString());
        foreach (int n in new[] { 0, 1, 2, 3, 4, 9, 11, 23, 25, 100, 1019 })
            Assert.IsFalse(Has(Ctx(c => c.ClearNumber = n), "twinprime"), n.ToString());
        Assert.IsTrue(AchievementCatalog.IsPrime(2) && AchievementCatalog.IsPrime(97) && AchievementCatalog.IsPrime(7919));
        Assert.IsFalse(AchievementCatalog.IsPrime(1) || AchievementCatalog.IsPrime(91) || AchievementCatalog.IsPrime(-7) || AchievementCatalog.IsPrime(7917));
    }

    // いちばん密な2秒(各ノーツの時刻 t からの [t, t+2) の数がいちばん多い窓、同じ数なら早い方)の8個以上がクリーン。
    [Test]
    public void StormNeedsTheDensestTwoSecondsClean()
    {
        PlayStats Storm(int dense, params (int k, JudgmentTier tier)[] results)
        {
            var notes = new List<NoteData> { PlayStatsTests.Note(1000), PlayStatsTests.Note(5000) };
            for (int i = 0; i < dense; i++) notes.Add(PlayStatsTests.Note(10000 + i * 240));
            notes.Add(PlayStatsTests.Note(12000));
            var stats = PlayStatsTests.AllResults(Notes(notes.ToArray()), JudgmentTier.Good);
            stats.Record(0, JudgmentTier.Miss);   // 窓の外は問わない
            foreach (var (k, tier) in results) stats.Record(k, tier);
            return stats;
        }
        int n = AchievementCatalog.StormMinNotes;
        Assert.IsTrue(Has(Ctx(c => c.Stats = Storm(n)), "storm"));
        Assert.IsFalse(Has(Ctx(c => c.Stats = Storm(n, (4, JudgmentTier.Bad))), "storm"), "窓の中の BAD");
        Assert.IsTrue(Has(Ctx(c => c.Stats = Storm(n, (2 + n, JudgmentTier.Miss))), "storm"),
            "12秒ちょうどは10秒からの窓の外(10.24秒からの窓も8個だが、同じ数なら早い方)");
        Assert.IsFalse(Has(Ctx(c => c.Stats = Storm(n - 1)), "storm"), "7個");
    }

    [Test]
    public void Center10NeedsFiftyCutsWithinTenMilliseconds()
    {
        PlayStats Errors(int within, double extra)
        {
            var stats = Notes(Taps(within + 2));
            for (int k = 0; k < within; k++)
                stats.Record(k, JudgmentTier.Perfect, k % 2 == 0 ? AchievementCatalog.CenterMs : -AchievementCatalog.CenterMs, DateTime.Now, SaberHand.Left, false);
            stats.Record(within, JudgmentTier.Perfect, extra, DateTime.Now, SaberHand.Left, false);
            stats.Record(within + 1, JudgmentTier.Perfect);   // 誤差の無い判定(ロング)は数えない
            return stats;
        }
        int n = AchievementCatalog.CenterCount;
        Assert.IsTrue(Has(Ctx(c => c.Stats = Errors(n, 40)), "center10"), "±10ms ちょうどが50個");
        Assert.IsFalse(Has(Ctx(c => c.Stats = Errors(n - 1, 10.01)), "center10"), "49個");
    }

    [Test]
    public void ReswingNeedsAWrongSwingBeforeAPerfectArrow()
    {
        PlayStats Swing(string direction, bool before, JudgmentTier tier, bool after = false)
        {
            var stats = Notes(PlayStatsTests.Note(1000, direction: direction), PlayStatsTests.Note(2000));
            if (before) stats.MarkWrongSwing(0);
            stats.Record(0, tier);
            if (after) stats.MarkWrongSwing(0);
            stats.Record(1, JudgmentTier.Perfect);
            return stats;
        }
        Assert.IsTrue(Has(Ctx(c => c.Stats = Swing("up", true, JudgmentTier.Perfect)), "reswing"));
        Assert.IsFalse(Has(Ctx(c => c.Stats = Swing("up", true, JudgmentTier.Great)), "reswing"));
        Assert.IsFalse(Has(Ctx(c => c.Stats = Swing("up", false, JudgmentTier.Perfect)), "reswing"), "振り直していない");
        Assert.IsFalse(Has(Ctx(c => c.Stats = Swing("up", false, JudgmentTier.Perfect, true)), "reswing"), "結果のあとの知らせは数えない");
        Assert.IsFalse(Has(Ctx(c => c.Stats = Swing("none", true, JudgmentTier.Perfect)), "reswing"), "矢印のノーツだけ");
    }

    [Test]
    public void LunchtimeIsAStartFromNoonToBeforeOne()
    {
        var day = new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Local);
        bool At(DateTime start) => Has(Ctx(c => { c.StartedLocal = start; c.EndedLocal = start.AddMinutes(3); }), "lunchtime");
        Assert.IsTrue(At(day.AddHours(12)));
        Assert.IsTrue(At(day.AddHours(13).AddSeconds(-1)), "12:59:59");
        Assert.IsFalse(At(day.AddHours(12).AddSeconds(-1)), "11:59:59");
        Assert.IsFalse(At(day.AddHours(13)), "13:00:00");
        Assert.IsFalse(Has(Ctx(c => { c.StartedLocal = day.AddHours(11).AddMinutes(58); c.EndedLocal = day.AddHours(12).AddMinutes(1); }), "lunchtime"),
            "終わりがお昼でも、始めたのが前");
    }

    [Test]
    public void LefthandIsTheBlueSaberAtTheTitle()
    {
        Assert.IsTrue(Has(Ctx(c => c.TitleHand = SaberHand.Left), "lefthand"));
        Assert.IsFalse(Has(Ctx(c => c.TitleHand = SaberHand.Right), "lefthand"), "赤の剣(マウスも)");
        Assert.IsFalse(Has(Ctx(c => c.TitleHand = SaberHand.Any), "lefthand"), "キーボード");
    }

    // 2番目からの組で「それより前の全ノーツの終わり(ロングは終わり)の最大」からの間がいちばん長い(同じなら早い方)組が、2秒以上あいて全部 PERFECT。
    [Test]
    public void SilenceNeedsTheGroupAfterTheLongestRestPerfect()
    {
        PlayStats Rest(float restartMs, float lastGapMs, params int[] perfect)
        {
            var notes = new[] { PlayStatsTests.Note(1000), PlayStatsTests.Note(1500, count: 3), PlayStatsTests.Note(restartMs), PlayStatsTests.Note(restartMs + lastGapMs) };
            notes[1].lengthMs = 1500f;   // ロングは 3.0 秒まで
            var stats = PlayStatsTests.AllResults(Notes(notes), JudgmentTier.Great);
            foreach (int k in perfect) stats.Record(k, JudgmentTier.Perfect);
            return stats;
        }
        Assert.IsTrue(Has(Ctx(c => c.Stats = Rest(5500, 2100, 2)), "silence"), "ロングの終わり(3.0秒)から2.5秒あいた組");
        Assert.IsFalse(Has(Ctx(c => c.Stats = Rest(5500, 2100, 3)), "silence"), "いちばん長い休みのあとの組だけ");
        Assert.IsTrue(Has(Ctx(c => c.Stats = Rest(4900, 2100, 3)), "silence"), "ロングの終わりから測ると1.9秒なので、いちばん長いのは最後の2.1秒");
        Assert.IsFalse(Has(Ctx(c => c.Stats = Rest(4900, 2100, 2)), "silence"));
        Assert.IsTrue(Has(Ctx(c => c.Stats = Rest(5000, 2000, 2)), "silence"), "2.0秒ちょうどが2か所なら早い方");
        Assert.IsFalse(Has(Ctx(c => c.Stats = Rest(5000, 2000, 3)), "silence"));
        Assert.IsFalse(Has(Ctx(c => c.Stats = Rest(4500, 1500, 0, 1, 2, 3)), "silence"), "2秒あく所が無い");
    }

    // 赤・青・金・矢印・ロングの種類ごとに PERFECT が1個以上(金の矢印は金と矢印の両方に数える)。
    [Test]
    public void FivekindsNeedsAPerfectOfEveryKind()
    {
        NoteData[] Kinds() => new[]
        {
            PlayStatsTests.Note(1000, "red"), PlayStatsTests.Note(2000, "blue"), PlayStatsTests.Note(3000, "gold"),
            PlayStatsTests.Note(4000, "red", "up"), PlayStatsTests.Note(5000, "blue", count: 3),
        };
        var all = PlayStatsTests.AllResults(Notes(Kinds()), JudgmentTier.Perfect);
        Assert.IsTrue(Has(Ctx(c => c.Stats = all), "fivekinds"));
        all.Record(4, JudgmentTier.Miss);
        Assert.IsFalse(Has(Ctx(c => c.Stats = all), "fivekinds"), "ロングを最後まで切れなかった");
        var noRed = PlayStatsTests.AllResults(Notes(Kinds()), JudgmentTier.Perfect);
        noRed.Record(0, JudgmentTier.Great);
        noRed.Record(3, JudgmentTier.Great);
        Assert.IsFalse(Has(Ctx(c => c.Stats = noRed), "fivekinds"), "赤の PERFECT が無い");
        var goldArrow = PlayStatsTests.AllResults(Notes(PlayStatsTests.Note(1000, "red"), PlayStatsTests.Note(2000, "blue"),
            PlayStatsTests.Note(3000, "gold", "down"), PlayStatsTests.Note(5000, "red", count: 3)), JudgmentTier.Perfect);
        Assert.IsTrue(Has(Ctx(c => c.Stats = goldArrow), "fivekinds"), "金の矢印は金と矢印の両方");
        var noLong = PlayStatsTests.AllResults(Notes(PlayStatsTests.Note(1000, "red"), PlayStatsTests.Note(2000, "blue"), PlayStatsTests.Note(3000, "gold", "up")),
            JudgmentTier.Perfect);
        Assert.IsFalse(Has(Ctx(c => c.Stats = noLong), "fivekinds"), "ロングの無い譜面");
    }

    // 演奏の記録を使う第2弾の実績は、D(照準・ノーツ無し)と途中から記録した回では取れない。
    [Test]
    public void NoteBasedSecondBatchNeedsACompleteNoteRecord()
    {
        var stats = PlayStatsTests.AllResults(Notes(PlayStatsTests.Note(1000, "red"), PlayStatsTests.Note(2000, "blue"), PlayStatsTests.Note(3000, "gold", "up"),
            PlayStatsTests.Note(4000, "blue", count: AchievementCatalog.OctaveCuts)), JudgmentTier.Perfect);
        Assert.IsTrue(Has(Ctx(c => c.Stats = stats), "fivekinds"));
        Assert.IsTrue(Has(Ctx(c => c.Stats = stats), "octave"));
        Assert.IsFalse(Has(Ctx(c => { c.Stats = stats; c.AimMode = true; }), "fivekinds"), "D は対象外");
        stats.Complete = false;
        Assert.IsFalse(Has(Ctx(c => c.Stats = stats), "octave"), "途中から記録した回");
    }
}
