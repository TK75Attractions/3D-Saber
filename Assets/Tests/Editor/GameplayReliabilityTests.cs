using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

// 利用者の譜面・保存データと再接続を入口に、実際の症状が再発しないことを確認する。
public class GameplayReliabilityTests
{
    readonly List<GameObject> owned = new List<GameObject>();
    readonly string song = "__GameplayReliability_" + Guid.NewGuid().ToString("N");

    [TearDown]
    public void Cleanup()
    {
        foreach (var go in owned) if (go != null) Object.DestroyImmediate(go);
        owned.Clear();
        PlayerPrefs.DeleteKey(SongAchievementStore.Key(song, "Hard"));
        PlayerPrefs.DeleteKey(HighScoreStore.Key(song, "Hard"));
    }

    GameObject Own(string name)
    {
        var go = new GameObject(name); owned.Add(go); return go;
    }

    NoteSpawner Spawn(out CuttableNote note, int count = 2)
    {
        var spawner = Own("ReliabilitySpawner").AddComponent<NoteSpawner>();
        spawner.noteRoot = spawner.transform;
        spawner.notePrefab = Own("ReliabilityPrefab");
        spawner.notePrefab.AddComponent<CuttableNote>().shatterDebrisCount = 0;
        spawner.buildTimingCues = false;
        spawner.simultaneousGuideEnabled = false;
        spawner.SetChart(new ChartData { notes = new List<NoteData> { new NoteData { time = 1000, count = count } } });
        spawner.Tick(1);
        note = spawner.LiveNotes[0];
        return spawner;
    }

    [Test]
    public void BrokenJsonDoesNotCrashTheSongBrowser()
    {
        Assert.DoesNotThrow(() => ChartLoader.Parse("{ broken"));
        Assert.Zero(ChartLoader.Parse("[]").notes.Count);
        Assert.Zero(ChartLoader.Parse(" \t\r\n ").notes.Count);
    }

    [Test]
    public void UnicodeBomAndSurroundingWhitespaceDoNotHideAChart()
    {
        var chart = ChartLoader.Parse(" \uFEFF{\"bpm\":196,\"notes\":[{\"time\":2890,\"x\":-0.4,\"y\":0.7}]} ");
        Assert.AreEqual(1, chart.notes.Count);
        Assert.AreEqual(2890, chart.notes[0].time);
        Assert.AreEqual(.7f, chart.notes[0].y);
    }

    [Test]
    public void NullRecordsDoNotDiscardTheOtherNotes()
    {
        var chart = ChartLoader.Parse("{\"notes\":[{\"time\":1000},null,{\"time\":2000}]}");
        Assert.AreEqual(2, chart.notes.Count);
        Assert.AreEqual(2000, chart.notes[1].time);
    }

    [Test]
    public void RemovingExplicitNullKeepsARealZeroTimeCenterNoteAndIgnoresNestedNulls()
    {
        var chart = ChartLoader.Parse("{\"metadata\":{\"notes\":[null]},\"notes\":[null,{\"time\":0,\"x\":0,\"y\":0,\"color\":null},{\"time\":2000,\"ignored\":[null,1]},null]}");
        Assert.AreEqual(2, chart.notes.Count);
        Assert.Zero(chart.notes[0].time);
        Assert.Zero(chart.notes[0].x);
        Assert.Zero(chart.notes[0].y);
        Assert.AreEqual(2000, chart.notes[1].time);
    }

    [Test]
    public void EqualTimeNotesKeepAuthorOrderAfterSorting()
    {
        var chart = new ChartData();
        for (int i = 0; i < 32; i++) chart.notes.Add(new NoteData { time = 2000, x = i });
        chart.notes.Add(new NoteData { time = 1000, x = -1 });
        var loaded = ChartLoader.Parse(JsonUtility.ToJson(chart));
        for (int i = 0; i < 32; i++) Assert.AreEqual(i, loaded.notes[i + 1].x);
    }

    [Test]
    public void WhitespaceInExportedKindsDoesNotLoseTheColorOrFlick()
    {
        var chart = ChartLoader.Parse("{\"notes\":[{\"time\":1000,\"type\":\" LONG \",\"color\":\" Blue \",\"direction\":\" Up \",\"count\":4,\"lengthMs\":700}]}");
        var note = chart.notes[0];
        Assert.AreEqual(SaberHand.Left, SaberHandHelper.FromColor(note.color));
        Assert.AreEqual(CutDirection.Up, CutDirectionHelper.Parse(note.direction));
        Assert.True(note.IsLong);
        Assert.AreEqual(700, note.lengthMs);
    }

    [Test]
    public void InvalidMetadataCannotCollapseAllNotesToTheCenter()
    {
        var chart = ChartLoader.Parse("{\"bpm\":0,\"coordScale\":0,\"notes\":[{\"time\":-110,\"x\":-0.8,\"y\":0.4}]}");
        Assert.Greater(chart.bpm, 0);
        Assert.AreEqual(1, chart.coordScale);
        Assert.AreEqual(-110, chart.notes[0].time, "負の開始時刻は作者の意図として残す");
        Assert.AreEqual(-.8f, chart.notes[0].x);
    }

    [Test]
    public void MeterDuplicatesUseLastDefinitionWithoutMovingNotes()
    {
        var chart = ChartLoader.Parse("{\"bpm\":196,\"timeSignatures\":[{\"beat\":0,\"numerator\":3,\"denominator\":4},{\"beat\":0,\"numerator\":5,\"denominator\":4},{\"beat\":4,\"numerator\":0,\"denominator\":3}],\"notes\":[{\"time\":2890}]}");
        Assert.AreEqual(1, chart.timeSignatures.Count);
        Assert.AreEqual(5, chart.timeSignatures[0].numerator);
        Assert.AreEqual(2890, chart.notes[0].time);
    }

    [Test]
    public void ExplicitLongDurationWinsOverTheCutCountEstimate()
    {
        var note = new NoteData { time = 10000, count = 44, lengthMs = 1500 };
        Assert.AreEqual(11.5, note.TimeSeconds + note.LingerSeconds(.7f), .00001);
        note.lengthMs = 0;
        Assert.AreEqual(30.1, note.LingerSeconds(.7f), .00001);
        note.count = 1; note.lengthMs = 5000;
        Assert.Zero(note.LingerSeconds(.7f));
    }

    [Test]
    public void LongDifficultyUsesRequiredCutsAndTheAvailableTime()
    {
        var shortLong = new ChartData { notes = new List<NoteData> { new NoteData { time = 1000, count = 2, lengthMs = 4000 } } };
        var denseLong = new ChartData { notes = new List<NoteData> { new NoteData { time = 1000, count = 44, lengthMs = 4000 } } };
        Assert.Greater(ChartDifficultyRater.Rate(denseLong), ChartDifficultyRater.Rate(shortLong));
        denseLong.notes[0].lengthMs = 30000;
        Assert.Less(ChartDifficultyRater.Rate(denseLong), 10, "同じ44回でも長い滞留時間なら密度は低い");
    }

    [Test]
    public void InvalidSongPathsStayOutsideTheLoader()
    {
        Assert.False(ChartLoader.ValidPathSegment("../製作中"));
        Assert.False(ChartLoader.ValidPathSegment("..\\製作中"));
        Assert.True(ChartLoader.ValidPathSegment("製作中"));
        Assert.Zero(ChartLoader.LoadFromStreamingAssets("../製作中", "Hard").notes.Count);
    }

    [Test]
    public void AChartLockedByItsEditorDoesNotCrashTheGame()
    {
        string file = Path.Combine(Path.GetTempPath(), "SaberLockedChart_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using (var handle = new FileStream(file, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.DoesNotThrow(() => ChartLoader.LoadFromFile(file));
                Assert.Zero(ChartLoader.LoadFromFile(file).notes.Count);
            }
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }

    [Test]
    public void BindingAfterSpawnStillScoresTheVisibleNoteOnce()
    {
        var spawner = Spawn(out var note);
        var score = Own("LateScore").AddComponent<ScoreManager>();
        score.Bind(spawner);
        score.Bind(spawner);
        note.Cut(Vector3.zero, Vector3.right * 5);
        note.Cut(Vector3.zero, Vector3.right * 5);
        Assert.AreEqual(1, score.PerfectCount);
        Assert.AreEqual(300, score.Score);
    }

    [Test]
    public void DetachingScoreStopsTheOldVisibleNotesFromScoring()
    {
        var spawner = Spawn(out var note);
        var score = Own("DetachedScore").AddComponent<ScoreManager>();
        score.Bind(spawner);
        score.Bind(null);
        note.Cut(Vector3.zero, Vector3.right * 5);
        note.Cut(Vector3.zero, Vector3.right * 5);
        Assert.Zero(score.Score);
    }

    void MakeWrongFlick(ScoreManager score)
    {
        var spawner = Spawn(out var note);
        note.RequiredDirection = CutDirection.Right;
        note.IsGold = true;
        score.Bind(spawner);
        note.Cut(Vector3.zero, Vector3.up * 5, CutDirection.None, SaberHand.Left);
        note.Cut(Vector3.zero, Vector3.up * 5, CutDirection.None, SaberHand.Left);
        Assert.True(score.LastWasWrongFlick);
        Assert.True(score.LastCutWasGold);
    }

    [Test]
    public void ReplayResetClearsWrongFlickAndThePreviousHand()
    {
        var score = Own("ResetScore").AddComponent<ScoreManager>();
        MakeWrongFlick(score);
        score.Reset();
        Assert.False(score.LastWasWrongFlick);
        Assert.False(score.LastErrorValid);
        Assert.False(score.LastCutWasGold);
        Assert.AreEqual(SaberHand.Any, score.LastCutHand);
    }

    [Test]
    public void DirectJudgmentsDoNotReuseThePreviousCutSfxOrHapticContext()
    {
        var score = Own("DirectScore").AddComponent<ScoreManager>();
        MakeWrongFlick(score);
        score.RegisterHit(JudgmentTier.Perfect);
        Assert.False(score.LastWasWrongFlick);
        Assert.False(score.LastCutWasGold);
        Assert.AreEqual(CutDirection.None, score.LastCutDirection);
        Assert.AreEqual(SaberHand.Any, score.LastCutHand);
        score.RegisterMiss();
        Assert.False(score.LastWasWrongFlick);
    }

    [Test]
    public void ResetAlsoClearsThePreviousTapTimingError()
    {
        var spawner = Spawn(out var note, 1);
        var score = Own("TimingReset").AddComponent<ScoreManager>();
        score.Bind(spawner);
        note.CutAtSongTime(Vector3.zero, Vector3.right * 5, CutDirection.None, SaberHand.Any, 1.02);
        Assert.True(score.LastErrorValid);
        Assert.AreEqual(20, score.LastErrorMs, .001);
        score.Reset();
        Assert.False(score.LastErrorValid);
        Assert.Zero(score.LastErrorMs);
    }

    [Test]
    public void TimedOutLongHidesTheRemainingCount()
    {
        var spawner = Spawn(out var note, 4);
        Assert.True(note.countLabel.gameObject.activeSelf);
        note.MarkMiss();
        Assert.False(note.countLabel.gameObject.activeSelf);
    }

    [Test]
    public void DisabledNotesCannotBeCutByALateCallback()
    {
        var note = Own("HiddenNote").AddComponent<CuttableNote>();
        note.gameObject.SetActive(false);
        Assert.False(note.CutAtSongTime(Vector3.zero, Vector3.right * 5, CutDirection.None, SaberHand.Any, 0));
        Assert.False(note.IsCut);
    }

    [Test]
    public void RemovingOnlyTheSpawnerAlsoRemovesUnpooledNotes()
    {
        var spawner = Spawn(out var note);
        // EditModeでは未実行のMonoBehaviourへOnDestroyが自動発火しないため終了処理だけを直接呼ぶ。
        // SendMessageも未実行BehaviourではUnityのアサートになるため使わない。
        // 実際のComponent破棄からの呼び出しはSongPlayerRecoveryPlayTestsで確認する。
        typeof(NoteSpawner).GetMethod("OnDestroy", System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic).Invoke(spawner, null);
        Object.DestroyImmediate(spawner);
        Assert.True(note == null);
    }

    [Test]
    public void InsertingAnEditableResultTakesAnIndependentSnapshot()
    {
        var input = new HighScoreEntry { score = 400, accuracy = .75f, rank = "A", date = "today" };
        var table = new HighScoreTable();
        HighScoreStore.Insert(table, input, 5);
        input.score = 99999; input.accuracy = 0;
        Assert.AreEqual(400, table.entries[0].score);
        Assert.AreEqual(.75f, table.entries[0].accuracy);
    }

    [Test]
    public void BadRankingRowsDoNotBreakInsertionOrShowImpossibleAccuracy()
    {
        var table = new HighScoreTable { entries = new List<HighScoreEntry> { null, new HighScoreEntry { score = 100 } } };
        Assert.AreEqual(0, HighScoreStore.Insert(table, new HighScoreEntry { score = 200, accuracy = float.NaN }, 5));
        Assert.Zero(table.entries[0].accuracy);
        Assert.AreEqual(-1, HighScoreStore.Insert(table, new HighScoreEntry { score = -1 }, 5));
        PlayerPrefs.SetString(HighScoreStore.Key(song, "Hard"), "{\"entries\":[{\"score\":50,\"accuracy\":5}]}");
        Assert.AreEqual(1, HighScoreStore.Load(song, "Hard").entries[0].accuracy);
    }

    [Test]
    public void AchievementDifficultyAliasesUseTheExistingStandardKey()
    {
        Assert.AreEqual("songAchievements_v1_" + song + "::Hard", SongAchievementStore.Key(song, " HARD "));
        Assert.True(SongAchievementStore.Record(song, "Hard", "same", 5, 0, 0, 0, 0));
        Assert.False(SongAchievementStore.Record(song, "hard", "same", 5, 0, 0, 0, 0));
        Assert.AreEqual(1, SongAchievementStore.Load(song, " HARD ").ap);
    }

    [Test]
    public void RedisplayingAnEarlierRunDoesNotCountItAsAnotherPlayer()
    {
        Assert.True(SongAchievementStore.Record(song, "Hard", "first", 5, 0, 0, 0, 0));
        Assert.True(SongAchievementStore.Record(song, "Hard", "second", 5, 0, 0, 0, 0));
        Assert.False(SongAchievementStore.Record(song, "Hard", "first", 5, 0, 0, 0, 0));
        Assert.AreEqual(2, SongAchievementStore.Load(song, "Hard").ap);
    }

    [Test]
    public void ExistingAchievementCountsAndLastRunSurviveTheNewHistoryFormat()
    {
        PlayerPrefs.SetString(SongAchievementStore.Key(song, "Hard"), "{\"s\":12,\"ap\":7,\"lastRunId\":\"legacy\"}");
        Assert.False(SongAchievementStore.Record(song, "hard", "legacy", 5, 0, 0, 0, 0));
        Assert.True(SongAchievementStore.Record(song, "Hard", "new", 5, 0, 0, 0, 0));
        Assert.AreEqual(13, SongAchievementStore.Load(song, "Hard").s);
        Assert.AreEqual(8, SongAchievementStore.Load(song, "Hard").ap);
    }

    [Test]
    public void LargeValidCountersDoNotOverflowIntoPerfectAccuracy()
    {
        Assert.AreEqual(.5f, PlayRankHelper.Accuracy(int.MaxValue, 0, 0, 0, int.MaxValue), .00001);
        Assert.AreEqual(0, PlayRankHelper.Accuracy(-10, 0, 0, 0, 1));
    }

    [Test]
    public void CorruptPreferencesDoNotFreezeNotesOrMoveTheChartOutOfRange()
    {
        bool hasApproach = PlayerPrefs.HasKey("noteApproachTime"), hasOffset = PlayerPrefs.HasKey("judgmentOffsetMs");
        float approach = PlayerPrefs.GetFloat("noteApproachTime");
        int offset = PlayerPrefs.GetInt("judgmentOffsetMs");
        try
        {
            PlayerPrefs.SetFloat("noteApproachTime", float.NaN);
            PlayerPrefs.SetInt("judgmentOffsetMs", int.MaxValue);
            Assert.AreEqual(GameSession.NoteApproachTimeDefault, GameSession.NoteApproachTime);
            Assert.AreEqual(GameSession.JudgmentOffsetMaxMs, GameSession.JudgmentOffsetMs);
            GameSession.NoteApproachTime = float.NegativeInfinity;
            Assert.AreEqual(GameSession.NoteApproachTimeDefault, GameSession.NoteApproachTime);
        }
        finally
        {
            if (hasApproach) PlayerPrefs.SetFloat("noteApproachTime", approach); else PlayerPrefs.DeleteKey("noteApproachTime");
            if (hasOffset) PlayerPrefs.SetInt("judgmentOffsetMs", offset); else PlayerPrefs.DeleteKey("judgmentOffsetMs");
            PlayerPrefs.Save();
        }
    }
}
