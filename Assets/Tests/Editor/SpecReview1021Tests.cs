using NUnit.Framework;
using UnityEngine;

// 仕様面の点検(2026-10-21)の 1・2・4・7 の決まり。純粋な部品をここで確かめ、画面の流れは PlayMode で確かめる。
public class SpecReview1021Tests
{
    // ---- 2: Esc は2秒以内に2回 ----
    [Test]
    public void EscapeNeedsTwoPressesWithinTwoSeconds()
    {
        var gate = new EscapeTwiceGate();
        Assert.False(gate.Press(10), "1回目は知らせだけ");
        Assert.True(gate.Armed(11.999));
        Assert.True(gate.Press(12), "ちょうど2秒は2回目");
        Assert.False(gate.Armed(12), "成立したら数え直す");
        Assert.False(gate.Press(12.5));
        Assert.False(gate.Press(14.6), "2秒を過ぎたら新しい1回目");
        Assert.True(gate.Press(15.0));
        gate.Press(20); gate.Reset();
        Assert.False(gate.Press(20.5), "取り消したあとは1回目から");
    }

    // ---- 1: 1回も切っていない回は残さない ----
    [Test]
    public void OnlyRunsWithACutAreRecorded()
    {
        var saved = Snapshot();
        try
        {
            GameSession.ResetResult();
            GameSession.IsCalibrationMode = false;
            GameSession.FinalMiss = 120;
            Assert.False(GameSession.ShouldRecordRun, "全部 MISS で1回も切っていない回");
            Assert.True(GameSession.CompletedWithoutRecord);
            GameSession.FinalCutCount = 1;
            Assert.True(GameSession.ShouldRecordRun, "向き違いで MISS に下がった切りも、切った回");
            GameSession.FinalCutCount = 0; GameSession.FinalBad = 1;
            Assert.True(GameSession.ShouldRecordRun, "切った回数を持たない経路でも、MISS 以外の判定があれば残す");
            Assert.False(GameSession.CompletedWithoutRecord);
            GameSession.IsCalibrationMode = true;
            Assert.False(GameSession.ShouldRecordRun);
            Assert.False(GameSession.CompletedWithoutRecord);
            GameSession.IsCalibrationMode = false;
            GameSession.FinalBad = 0; GameSession.FinalMiss = 0;
            Assert.False(GameSession.CompletedWithoutRecord, "判定が1つも無い回は、終わったあとの理由を出さない");
        }
        finally { Restore(saved); }
    }

    [Test]
    public void HighScoreIsRecordedOncePerRunAndKeepsTheBestBefore()
    {
        var saved = Snapshot();
        try
        {
            GameSession.ResetResult();
            Assert.AreEqual(5000, GameSession.HighScoreBestBefore(5000));
            Assert.True(GameSession.ClaimHighScoreRecord());
            Assert.AreEqual(5000, GameSession.HighScoreBestBefore(9000), "作り直しても、残す前の最高点で比べる");
            Assert.False(GameSession.ClaimHighScoreRecord(), "同じ回は一度だけ残す");
            GameSession.ResetResult();
            Assert.True(GameSession.ClaimHighScoreRecord(), "次の回はまた残す");
            Assert.AreEqual(9000, GameSession.HighScoreBestBefore(9000));
            GameSession.AchievementRunId = null;
            Assert.True(GameSession.ClaimHighScoreRecord()); Assert.True(GameSession.ClaimHighScoreRecord());
        }
        finally { Restore(saved); }
    }

    [Test]
    public void CutCountCountsRealCutsButNotTimeouts()
    {
        var go = new GameObject("CutCountTest");
        try
        {
            var score = go.AddComponent<ScoreManager>();
            score.RegisterMiss();
            Assert.Zero(score.CutCount);
            score.RegisterHit(JudgmentTier.Bad);
            Assert.AreEqual(1, score.CutCount);
            score.RegisterHit(JudgmentTier.Miss);
            Assert.AreEqual(1, score.CutCount, "直接の MISS は切りに数えない");
            score.Reset();
            Assert.Zero(score.CutCount);
        }
        finally { Object.DestroyImmediate(go); }
    }

    [Test]
    public void CountdownTextsSayWhereTheTimeoutGoes()
    {
        StringAssert.Contains("自動スタート", SongSelectSkin.TimerNotice(15, true));
        StringAssert.Contains("タイトル", SongSelectSkin.TimerNotice(15, false));
        Assert.AreEqual("この曲が自動で始まるまで", SongSelectSkin.TimerNotice(60, true));
        StringAssert.Contains("タイトルへ", SongSelectSkin.TimerNotice(60, false));
        StringAssert.Contains("はじまるよ", SongSelectSkin.StartHint(5, true));
        StringAssert.Contains("タイトルへ もどるよ", SongSelectSkin.StartHint(5, false));
        Assert.AreEqual(SongSelectSkin.StartHint(40, true), SongSelectSkin.StartHint(40, false), "10秒より前は同じ案内");
    }

    // ---- 4: 曲の途中で剣を見失ったとき ----
    [Test]
    public void TrackingWatchPausesOnlyWhenEverySeenSaberIsLostAndNotesAreComing()
    {
        var watch = new PlayTrackingWatch();
        Assert.AreEqual(PlayTrackingWatch.Request.None, watch.Step(100, true), "一度も届いていない台(マウスで試す)では止めない");
        Feed(watch, 0, 10, 20);
        Feed(watch, 1, 10, 20);
        Assert.False(watch.AllLost(22.9));
        Assert.AreEqual(PlayTrackingWatch.Request.None, watch.Step(22.9, true));
        Assert.True(watch.AllLost(23.0), "最後に届いてから3秒");
        Assert.AreEqual(PlayTrackingWatch.Request.None, watch.Step(23.5, false), "休みの区間(ノーツが来ない)では止めない");
        Assert.AreEqual(PlayTrackingWatch.Request.Pause, watch.Step(23.6, true));
        Assert.AreEqual(PlayTrackingWatch.Phase.Waiting, watch.State);
        Assert.AreEqual(PlayTrackingWatch.Request.None, watch.Step(24, true), "止める要求は一度だけ");
    }

    [Test]
    public void TrackingWatchKeepsPlayingWhileOneSaberStillArrives()
    {
        var watch = new PlayTrackingWatch();
        Feed(watch, 0, 0, 30);
        Feed(watch, 1, 0, 5);
        Assert.False(watch.AllLost(31), "青が止まっても、赤が届いていれば止めない");
        Assert.AreEqual(PlayTrackingWatch.Request.None, watch.Step(31, true));
    }

    [Test]
    public void TrackingWatchResumesAfterOneSecondStableAndAThreeSecondCount()
    {
        var watch = new PlayTrackingWatch();
        Feed(watch, 0, 0, 10);
        Assert.AreEqual(PlayTrackingWatch.Request.Pause, watch.Step(13.5, true));
        watch.ObserveUsable(0, 20);
        Assert.AreEqual(PlayTrackingWatch.Request.None, watch.Step(20.05, true));
        Assert.AreEqual(PlayTrackingWatch.Phase.Waiting, watch.State, "1回届いただけでは戻らない");
        Feed(watch, 0, 20, 21.1);
        watch.Step(21.12, true);
        Assert.AreEqual(PlayTrackingWatch.Phase.Countdown, watch.State, "間を空けずに1秒届いたら数え始める");
        Assert.AreEqual(3, watch.CountdownRemaining(21.12), .001);
        StringAssert.Contains("再開まで 3", PlayTrackingStatus.ResumeText(watch.CountdownRemaining(21.12)));
        Feed(watch, 0, 21.1, 22.5);
        Assert.AreEqual(PlayTrackingWatch.Phase.Waiting, StepAt(watch, 23.0), "数える間に0.25秒より長く途切れたら待ちに戻る");
        Feed(watch, 0, 23.0, 24.2);
        Assert.AreEqual(PlayTrackingWatch.Phase.Countdown, StepAt(watch, 24.2));
        Feed(watch, 0, 24.2, 27.25);
        Assert.AreEqual(PlayTrackingWatch.Request.Resume, watch.Step(27.25, false), "3秒数えたら続きから");
        Assert.AreEqual(PlayTrackingWatch.Phase.Playing, watch.State);
    }

    [Test]
    public void TrackingWatchIgnoresRepeatedOrBrokenTimes()
    {
        var watch = new PlayTrackingWatch();
        watch.ObserveUsable(0, 5);
        for (int i = 0; i < 100; i++) watch.ObserveUsable(0, 5);
        watch.ObserveUsable(0, double.NaN); watch.ObserveUsable(3, 6);
        Assert.False(watch.Stable(5.1), "同じ受信時刻をくり返しても、続けて届いたことにならない");
        Assert.True(watch.AllLost(8.1));
    }

    [Test]
    public void TrackingViewShowsStatusPauseAndStaffLine()
    {
        var go = new GameObject("TrackingViewTest");
        try
        {
            var view = PlayTrackingStatus.Create(go.transform, 262);
            view.Show("位置受信  赤 ●  青 ○", null, false);
            Assert.False(view.PanelShown);
            view.Show("位置受信  赤 ○  青 ○", null, true);
            Assert.True(view.StaffShown, "休みの区間で見失ったら、スタッフ向けの1行だけ");
            Assert.AreEqual("", view.HeadlineShown);
            view.Show("位置受信  赤 ○  青 ○", PlayTrackingStatus.PausedText, true);
            StringAssert.Contains("一時停止中", view.HeadlineShown);
            view.Show("位置受信  赤 ●  青 ○", PlayTrackingStatus.ResumeText(2.2), false);
            Assert.AreEqual("再開まで 3", view.HeadlineShown);
            Assert.False(view.StaffShown);
            StringAssert.Contains("Esc 2回", PlayTrackingStatus.StaffText);
        }
        finally { Object.DestroyImmediate(go); }
    }

    // ---- 7: 曲ごとの鳴らす範囲 ----
    [Test]
    public void PlayRangeUsesTheSongValuesOnlyForTheMatchingAudio()
    {
        var timeline = new StagePerformanceTimeline { playStartSeconds = 5, musicEndSeconds = 107.85 };
        var range = timeline.ResolvePlayRange(109.94, true);
        Assert.AreEqual(5, range.Start); Assert.AreEqual(107.85, range.End);
        range = timeline.ResolvePlayRange(109.94, false);
        Assert.AreEqual(0, range.Start); Assert.AreEqual(109.94, range.End, "音源を差し替えたら古い値で切らない");
        range = new StagePerformanceTimeline().ResolvePlayRange(120, true);
        Assert.AreEqual(0, range.Start); Assert.AreEqual(120, range.End, "値の無い曲は今までどおり");
        range = new StagePerformanceTimeline { playStartSeconds = 119.5, musicEndSeconds = 200 }.ResolvePlayRange(120, true);
        Assert.AreEqual(0, range.Start, "最後の1秒より後ろからは鳴らさない"); Assert.AreEqual(120, range.End);
        range = new StagePerformanceTimeline { playStartSeconds = double.NaN, musicEndSeconds = -3 }.ResolvePlayRange(120, true);
        Assert.AreEqual(0, range.Start); Assert.AreEqual(120, range.End);
        range = new StagePerformanceTimeline { playStartSeconds = 10, musicEndSeconds = 8 }.ResolvePlayRange(120, true);
        Assert.AreEqual(120, range.End, "鳴らし始めより前の終わりは使わない");
    }

    [TestCase("2_23_AM", 0, 194.45)]
    [TestCase("Andalusia", 0, 105.15)]
    [TestCase("BenkyouRemaster", 0, 137.55)]
    [TestCase("Epilogue", 0, 164.45)]
    [TestCase("Phobia PARTⅠ", 0, 108.85)]
    [TestCase("RushE", 0, 142.55)]
    [TestCase("escapade", 5, 107.85)]
    [TestCase("logic", 0, 205.35)]
    public void SongsWithSilenceHaveAPlayRangeForTheirOwnAudio(string songId, double start, double end)
    {
        var timeline = StagePerformanceTimeline.Load(songId);
        Assert.AreEqual(start, timeline.playStartSeconds, 1e-9);
        Assert.AreEqual(end, timeline.musicEndSeconds, 1e-9);
        string folder = System.IO.Path.Combine(Application.streamingAssetsPath, "Songs", songId);
        Assert.True(SongLoudness.SourceMatches(folder, timeline.audioSha256), "stage.json の audioSha256 が今の音源と一致する");
        Assert.False(SongLoudness.SourceMatches(folder, new string('0', 64)));
        // 最後のノーツ(ロングの長さ込み)より前で鳴り終わる値にはしない(終わりの待ちはノーツ側でも 3.5 秒ある)。
        foreach (var difficulty in new[] { "Easy", "Normal", "Hard" })
        {
            var chart = ChartLoader.LoadFromStreamingAssets(songId, difficulty);
            double last = 0;
            foreach (var n in chart.notes) last = System.Math.Max(last, n.TimeSeconds + chart.offsetMs / 1000.0 + n.LingerSeconds(.7f));
            Assert.Greater(end + 2.0, last, songId + " " + difficulty);
            if (chart.notes.Count > 0) Assert.Greater(chart.notes[0].TimeSeconds + chart.offsetMs / 1000.0, start, "飛ばす頭にノーツが無い");
        }
    }

    [Test]
    public void ProgressBarRunsOverThePlayedPart()
    {
        Assert.AreEqual(0f, GameHUDSkin.Progress01(5, 5, 107.85));
        Assert.AreEqual(.5f, GameHUDSkin.Progress01(56.425, 5, 107.85), 1e-4f);
        Assert.AreEqual(1f, GameHUDSkin.Progress01(110, 5, 107.85));
        Assert.AreEqual(GameHUDSkin.Progress01(30, 120), GameHUDSkin.Progress01(30, 0, 120), "頭から鳴らす曲は今までと同じ");
    }

    // 1/30 秒ごとに届く(時刻は足し算で積み上げず、端の時刻をちょうどにする)。
    static void Feed(PlayTrackingWatch watch, int color, double from, double to)
    {
        int n = (int)System.Math.Floor((to - from) * 30 + 1e-6);
        for (int i = 0; i <= n; i++) watch.ObserveUsable(color, from + i / 30.0);
    }

    static PlayTrackingWatch.Phase StepAt(PlayTrackingWatch watch, double now) { watch.Step(now, true); return watch.State; }

    static object[] Snapshot() => new object[]
    {
        GameSession.AchievementRunId, GameSession.IsCalibrationMode, GameSession.FinalPerfect, GameSession.FinalGreat, GameSession.FinalGood,
        GameSession.FinalBad, GameSession.FinalMiss, GameSession.FinalCutCount, GameSession.FinalScore,
    };

    static void Restore(object[] s)
    {
        GameSession.AchievementRunId = (string)s[0]; GameSession.IsCalibrationMode = (bool)s[1];
        GameSession.FinalPerfect = (int)s[2]; GameSession.FinalGreat = (int)s[3]; GameSession.FinalGood = (int)s[4];
        GameSession.FinalBad = (int)s[5]; GameSession.FinalMiss = (int)s[6]; GameSession.FinalCutCount = (int)s[7]; GameSession.FinalScore = (int)s[8];
    }
}
