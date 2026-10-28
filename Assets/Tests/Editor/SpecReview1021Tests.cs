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
            Assert.True(GameSession.ShouldRecordRun, "1回でも切った回は残す");
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
    public void TrackingWatchResumesAfterReadyPoseAndAThreeSecondCount()
    {
        var watch = new PlayTrackingWatch();
        Feed(watch, 0, 0, 10);
        Assert.AreEqual(PlayTrackingWatch.Request.Pause, watch.Step(13.5, true));
        for (int i = 0; i <= 3; i++)
        {
            double now = 20 + i * .1;
            watch.ObserveUsable(0, now);
            watch.Step(now, false, 1);
            Assert.AreEqual(i < 3 ? PlayTrackingWatch.Phase.Waiting : PlayTrackingWatch.Phase.Countdown, watch.State);
        }
        Assert.AreEqual(3, watch.CountdownRemaining(20.3), .001);
        StringAssert.Contains("再開まで 3", PlayTrackingStatus.ResumeText(watch.CountdownRemaining(20.3)));
        Assert.AreEqual(PlayTrackingWatch.Phase.Waiting, StepAt(watch, 20.6), "数える間に途切れたら待ち直す");
        for (int i = 0; i <= 3; i++)
        {
            double now = 21 + i * .1;
            watch.ObserveUsable(0, now); watch.Step(now, false, 1);
        }
        Assert.AreEqual(PlayTrackingWatch.Phase.Countdown, watch.State);
        Feed(watch, 0, 21.3, 24.35);
        Assert.AreEqual(PlayTrackingWatch.Request.Resume, watch.Step(24.35, false));
        Assert.AreEqual(PlayTrackingWatch.Phase.Playing, watch.State);
    }

    // 2026-10-29: 曲の読み直しで音を展開する間(約0.25秒)など、main スレッドが止まったフレームは途切れに数えない(3・2・1 を輪合わせへ戻さない)。
    // ふつうの間隔のフレームで届かない・止まる前から届いていなかった色は今までどおり。
    [Test]
    public void TrackingWatchDoesNotCountAFrozenFrameAsAGap()
    {
        var sender = new PlayTrackingWatch();
        double start = CountdownByFrames(sender), before = start + 1;
        Frame(sender, before, before);
        Frame(sender, before + .266, before);
        Assert.AreEqual(PlayTrackingWatch.Phase.Countdown, sender.State, "送り手も一緒に止まった 0.266 秒(PlayMode の試験と同じ並び)は途切れに数えない");
        Frame(sender, before + .3, before + .27);
        Assert.AreEqual(PlayTrackingWatch.Phase.Countdown, sender.State, "止まったあとに届いた位置で数え続ける");
        var request = PlayTrackingWatch.Request.None;
        double resumedAt = double.NaN;
        for (int i = 1; request == PlayTrackingWatch.Request.None && i <= 90; i++)
        {
            double now = before + .3 + i / 30.0;
            request = Frame(sender, now, now);
            resumedAt = now;
        }
        Assert.AreEqual(PlayTrackingWatch.Request.Resume, request);
        Assert.AreEqual(start + PlayTrackingWatch.ResumeCountdownSeconds, resumedAt, 1 / 30.0 + 1e-6, "数え始めから3秒で再開する(延びない)");

        var phone = new PlayTrackingWatch();
        start = CountdownByFrames(phone); before = start + 1;
        Frame(phone, before, before);
        Frame(phone, before + .3, before + .28);
        Assert.AreEqual(PlayTrackingWatch.Phase.Countdown, phone.State, "止まっている間もスマホからは届いていた(受信の間が 0.28 秒に見える)");

        var lost = new PlayTrackingWatch();
        start = CountdownByFrames(lost); before = start + 1;
        Frame(lost, before, before);
        for (int i = 1; i <= 9; i++) Frame(lost, before + i / 30.0, double.NaN);
        Assert.AreEqual(PlayTrackingWatch.Phase.Waiting, lost.State, "ふつうに見ていて 0.25 秒届かなければ今までどおり待ち直す");

        var stale = new PlayTrackingWatch();
        for (int i = 0; i <= 300; i++) Frame(stale, i / 30.0, i / 30.0);
        for (int i = 301; i <= 387; i++) Frame(stale, i / 30.0, double.NaN);
        Assert.AreEqual(PlayTrackingWatch.Phase.Playing, stale.State);
        Assert.AreEqual(PlayTrackingWatch.Request.Pause, Frame(stale, 387 / 30.0 + .3, double.NaN),
            "止まる前から届いていなかった色は、止まっていた間も届いていないまま(2.9 秒 + 0.3 秒で止める)");
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
    [TestCase("Andalusia", 0, 122.8)]
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

    // 本編と同じ順(フレームの初め → 届いた赤の位置 → 判定)で1フレーム進める。arrived が NaN なら届いていない。
    static PlayTrackingWatch.Request Frame(PlayTrackingWatch watch, double now, double arrived, int inside = 0)
    {
        watch.BeginFrame(now);
        if (!double.IsNaN(arrived)) watch.ObserveUsable(0, arrived);
        return watch.Step(now, true, inside);
    }

    // 赤だけ届く台で、10 秒届いたあと見失って止め、輪にそろえて 3・2・1 を数え始めるまでを 1/30 秒のフレームで進める。数え始めの時刻を返す。
    static double CountdownByFrames(PlayTrackingWatch watch)
    {
        int i = 0;
        for (; i <= 300; i++) Frame(watch, i / 30.0, i / 30.0);
        for (; i <= 420; i++) Frame(watch, i / 30.0, double.NaN);
        Assert.AreEqual(PlayTrackingWatch.Phase.Waiting, watch.State, "3 秒届かなければ止める");
        for (; watch.State == PlayTrackingWatch.Phase.Waiting && i <= 600; i++) Frame(watch, i / 30.0, i / 30.0, 1);
        Assert.AreEqual(PlayTrackingWatch.Phase.Countdown, watch.State, "輪にそろえたら数え始める");
        return (i - 1) / 30.0;
    }

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

    // 2026-10-24 ユーザー「右上の時計の絵文字を動かして」: 残りの数字が1つ減るたびに長い針が30°進み(12秒で1周)、
    // 0.15秒で少し行き過ぎて戻る。短い針は長い針の12分の1。選曲と判定調整の同じ時計。
    [Test]
    public void CountdownClockHandsTickWithTheNumber()
    {
        Assert.AreEqual(new Vector2(0, 90), SongSelectSkin.ClockHandDegrees(100), "残り100秒は今までの絵(12時と3時)");
        Assert.AreEqual(new Vector2(0, 90), SongSelectSkin.ClockHandDegrees(99.95), "最初の数字では動かさない");
        Assert.AreEqual(0, SongSelectSkin.ClockHandDegrees(98.999).x, 1, "数字が変わった瞬間は前の位置から");
        Assert.Greater(SongSelectSkin.ClockHandDegrees(98.9).x, 30, "少し行き過ぎてから");
        var settled = SongSelectSkin.ClockHandDegrees(98.8);
        Assert.AreEqual(30, settled.x, 1e-3, "30°の位置に止まる");
        Assert.AreEqual(92.5, settled.y, 1e-3, "短い針は12分の1");
        Assert.AreEqual(330, SongSelectSkin.ClockHandDegrees(88.5).x, 1e-3);
        Assert.AreEqual(0, SongSelectSkin.ClockHandDegrees(87.5).x, 1e-3, "12秒で1周");
        Assert.AreEqual(120, SongSelectSkin.ClockHandDegrees(87.5).y, 1e-3);
        Assert.AreEqual(new Vector2(0, 90), SongSelectSkin.ClockHandDegrees(double.NaN));
        var go = new GameObject("Clock", typeof(RectTransform));
        try
        {
            var clock = go.AddComponent<SongSelectDiscGraphic>();
            clock.Form = SongSelectDiscGraphic.Shape.Clock;
            Assert.AreEqual(0, clock.HandDegrees); Assert.AreEqual(90, clock.ShortHandDegrees);
            clock.SetHands(30, 92.5f);
            Assert.AreEqual(30, clock.HandDegrees); Assert.AreEqual(92.5f, clock.ShortHandDegrees);
        }
        finally { Object.DestroyImmediate(go); }
    }
}
