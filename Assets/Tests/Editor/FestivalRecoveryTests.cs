using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEngine;

public class FestivalRecoveryTests
{
    [TestCase("Easy", 1f, true, 1.25f)]
    [TestCase("easy", 1f, true, 1.25f)]
    [TestCase("Easy", 2f, true, 2f)]
    [TestCase("Easy", .8f, true, .8f)]
    [TestCase("Easy", 1.1f, true, 1.1f)]
    [TestCase("Normal", 1f, true, 1f)]
    [TestCase("Hard", 1f, true, 1f)]
    [TestCase("Easy", 1f, false, 1f)]
    public void ApproachOverrideIsOnlyForNormalSpeedInAnEasySong(string difficulty, float saved, bool song, float expected)
    {
        float before = GameSession.NoteApproachTime;
        bool hadKey = PlayerPrefs.HasKey("noteApproachTime");
        float stored = PlayerPrefs.GetFloat("noteApproachTime", -123);
        Assert.AreEqual(expected, GameDifficultyRules.ApproachSeconds(difficulty, saved, song));
        Assert.AreEqual(before, GameSession.NoteApproachTime);
        Assert.AreEqual(hadKey, PlayerPrefs.HasKey("noteApproachTime"));
        Assert.AreEqual(stored, PlayerPrefs.GetFloat("noteApproachTime", -123));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void BothJudgesAndImuReceiveEasyOnlyForSongs(bool song)
    {
        var root = new GameObject("難易度設定テスト");
        try
        {
            var first = root.AddComponent<SaberCutJudge>();
            var secondGo = new GameObject("二本目"); secondGo.transform.SetParent(root.transform);
            var second = secondGo.AddComponent<SaberCutJudge>();
            var imu = root.AddComponent<CameraImuJudgment>();
            GameDifficultyRules.ConfigureJudges("Easy", song, first, second, imu);
            float expected = song ? 80 : 70;
            Assert.AreEqual(expected, first.directionToleranceDegrees);
            Assert.AreEqual(expected, second.directionToleranceDegrees);
            Assert.AreEqual(expected, imu.directionToleranceDegrees);
        }
        finally { Object.DestroyImmediate(root); }
    }

    [Test]
    public void CameraSnapshotPrecedesSensitivityAndHoldsTheLastPacket()
    {
        var go = new GameObject("小窓入力テスト");
        var input = go.AddComponent<InputPoint>(); input.enabled = false;
        try
        {
            input.camWidth = 1920; input.camHeight = 1080; input.sensitivity = 2;
            input.useDirectWorldMapping = true;
            Field(input, "positionCalibrationEnabled", false);
            Field(input, "rawX", 1728f); Field(input, "rawY", 540f);
            Field(input, "rawReceiveTimestampTicks", SwingMonotonicClock.Timestamp);
            Field(input, "hasNewData", true); Field(input, "hasStickData", false);
            typeof(InputPoint).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(input, null);
            Assert.AreEqual(.9f, input.LastCameraPosition01.x, .00001f);
            Assert.AreEqual(.5f, input.LastCameraPosition01.y, .00001f);
            Assert.AreEqual(1, input.NormalizedPosition.x, .00001f);
            double received = input.LastReceivedTime;
            input.sensitivity = 3;
            typeof(InputPoint).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(input, null);
            Assert.AreEqual(.9f, input.LastCameraPosition01.x, .00001f);
            Assert.AreEqual(received, input.LastReceivedTime);
            Assert.Less(input.LastReceivedTime2, 0);
        }
        finally { Object.DestroyImmediate(go); }
    }

    [TestCase(.05f, .5f, PlayRecoveryRules.Left, "最後は左の端でした。少し右へ")]
    [TestCase(.95f, .5f, PlayRecoveryRules.Right, "最後は右の端でした。少し左へ")]
    [TestCase(.5f, .05f, PlayRecoveryRules.Bottom, "最後は下の端でした。少し上へ")]
    [TestCase(.5f, .95f, PlayRecoveryRules.Top, "最後は上の端でした。少し下へ")]
    public void EdgeHintsDescribeTheLastPosition(float x, float y, int edge, string hint)
    {
        Vector2 p = PlayRecoveryRules.Display01(new Vector2(x, y), false, false);
        Assert.AreEqual(edge, PlayRecoveryRules.Edges(p));
        Assert.AreEqual(hint, PlayRecoveryRules.Hint(p));
    }

    [Test]
    public void SnapshotAgeCornersAndDisplayDirectionAreExplicit()
    {
        Assert.AreEqual(0, PlayRecoveryRules.PointAlpha(10, -1000));
        Assert.AreEqual(1, PlayRecoveryRules.PointAlpha(10.25, 10));
        Assert.AreEqual(.3f, PlayRecoveryRules.PointAlpha(10.251, 10));
        Assert.AreEqual(PlayRecoveryRules.Left | PlayRecoveryRules.Top,
            PlayRecoveryRules.Edges(new Vector2(.1f, .9f)));
        Assert.AreEqual("", PlayRecoveryRules.Hint(new Vector2(.5f, .5f)));
        Vector2 mirrored = PlayRecoveryRules.Display01(new Vector2(.95f, .5f), true, false);
        Assert.AreEqual(PlayRecoveryRules.Left, PlayRecoveryRules.Edges(mirrored));
    }

    [Test]
    public void MiniWindowHidesUnseenColorsAndDimsOldPositions()
    {
        var root = new GameObject("小窓表示テスト");
        var inputGo = new GameObject("小窓受信テスト");
        var input = inputGo.AddComponent<InputPoint>(); input.enabled = false;
        try
        {
            var view = PlayTrackingStatus.Create(root.transform, 262);
            view.Show("", PlayTrackingStatus.PausedText, false);
            typeof(InputPoint).GetProperty("LastCameraPosition01").SetValue(input, new Vector2(.95f, .5f));
            typeof(InputPoint).GetProperty("LastCameraReceivedTime").SetValue(input, 10d);
            typeof(InputPoint).GetProperty("LastCameraReceivedTime2").SetValue(input, -1000d);
            view.ShowRecovery(true, input, 10.251, 0);
            var mini = view.transform.Find("TrackingNotice/LastCameraPosition");
            Assert.AreEqual(new Vector2(320, 180), ((RectTransform)mini).sizeDelta);
            var frame = mini.GetComponent<SongSelectDiscGraphic>();
            Assert.AreEqual(2, frame.Width); Assert.AreEqual(Color.white, frame.Edge);
            Assert.AreEqual(new Vector2(18, 18), ((RectTransform)mini.Find("RedPoint")).sizeDelta);
            Assert.AreEqual(.3f, mini.Find("RedPoint").GetComponent<SongSelectDiscGraphic>().color.a);
            Assert.False(mini.Find("BluePoint").gameObject.activeSelf);
            var line = mini.Find("LastPositionHint").GetComponent<TextMeshProUGUI>();
            Assert.AreEqual(26, line.fontSize);
            Assert.AreEqual(SongSelectSkin.Gold, line.color);
            view.ShowRecovery(false, input, 11, 0);
            Assert.False(view.Recovery.MiniShown);
            Assert.Zero(view.Recovery.ShownPoseMask);
        }
        finally { Object.DestroyImmediate(root); Object.DestroyImmediate(inputGo); }
    }

    [Test]
    public void SilentWaitUsesPlayStartAndDoesNotOverlapAPause()
    {
        var wait = new ResumedSongWait();
        Assert.False(wait.Read(2, 5, false, out _, out _), "初回の開始演出には追加しない");
        wait.Begin(2.2, 5);
        Assert.True(wait.Read(2.2, 5, false, out int number, out float progress));
        Assert.AreEqual(3, number); Assert.AreEqual(0, progress, .00001f);
        Assert.AreEqual("曲が始まるまで 3", wait.Caption);
        Assert.False(wait.Read(2.2, 5, true, out _, out _), "再開の3・2・1とは重ねない");
        Assert.True(wait.Read(4.99, 5, false, out number, out progress));
        Assert.AreEqual(1, number); Assert.Greater(progress, .99f);
        Assert.False(wait.Read(5, 5, false, out _, out _));
        wait.Begin(-1.01, 0);
        Assert.True(wait.Read(-1.01, 0, false, out number, out _)); Assert.AreEqual(2, number);
        wait.Begin(10, 0);
        Assert.False(wait.Read(10, 0, false, out _, out _));
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public void ReadyPoseRequiresEveryUsedColorAndDisappearsDuringCountdown(int mask)
    {
        var watch = Paused(mask);
        for (int i = 0; i <= 3; i++)
        {
            double now = 4 + i * .1;
            Observe(watch, mask, now); watch.Step(now, false, mask);
            Assert.AreEqual(i < 3 ? mask : 0, watch.PoseMask);
            Assert.AreEqual(i < 3 ? PlayTrackingWatch.Phase.Waiting : PlayTrackingWatch.Phase.Countdown, watch.State);
        }
        Observe(watch, mask, 4.4);
        watch.Step(4.4, false, 0);
        Assert.AreEqual(PlayTrackingWatch.Phase.Countdown, watch.State, "数える間は輪を要求しない");
        watch.Step(4.7, false, mask);
        Assert.AreEqual(PlayTrackingWatch.Phase.Waiting, watch.State, "途切れたら待ち直す");
    }

    [Test]
    public void PoseHoldRestartsOnLeavingOrStaleInput()
    {
        var pose = new PlayResumePose();
        pose.Step(0, true, 3, 3, 3);
        pose.Step(.15, true, 3, 3, 3);
        pose.Step(.2, true, 3, 1, 3);
        Assert.False(pose.Held);
        pose.Step(.3, true, 3, 3, 3);
        pose.Step(.45, true, 3, 3, 1);
        pose.Step(.6, true, 3, 3, 3);
        pose.Step(.75, true, 3, 3, 3);
        Assert.False(pose.Held);
        pose.Step(.9, true, 3, 3, 3);
        Assert.True(pose.Held);
        Assert.True(PlayRecoveryRules.Inside(new Vector2(.3f, 0), Vector2.zero, .6f));
        Assert.False(PlayRecoveryRules.Inside(new Vector2(.301f, 0), Vector2.zero, .6f));
    }

    [Test]
    public void FiveSecondsReturnsToTheExistingOneColorAutomaticResume()
    {
        var watch = Paused(3);
        for (int i = 0; i <= 50; i++)
        {
            double now = 4 + i * .1;
            Observe(watch, 1, now); watch.Step(now, false);
            Assert.AreEqual(i < 50 ? PlayTrackingWatch.Phase.Waiting : PlayTrackingWatch.Phase.Countdown, watch.State);
        }
        Assert.Zero(watch.PoseMask);
        for (int i = 1; i <= 30; i++)
        {
            double now = 9 + i * .1;
            Observe(watch, 1, now);
            Assert.AreEqual(i < 30 ? PlayTrackingWatch.Request.None : PlayTrackingWatch.Request.Resume,
                watch.Step(now, false));
        }
    }

    static PlayTrackingWatch Paused(int mask)
    {
        var watch = new PlayTrackingWatch();
        Observe(watch, mask, 0);
        Assert.AreEqual(PlayTrackingWatch.Request.Pause, watch.Step(3.1, true));
        return watch;
    }

    static void Observe(PlayTrackingWatch watch, int mask, double now)
    {
        for (int i = 0; i < 2; i++) if ((mask & (1 << i)) != 0) watch.ObserveUsable(i, now);
    }

    static void Field(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
}
