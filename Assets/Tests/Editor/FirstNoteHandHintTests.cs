using NUnit.Framework;
using TMPro;
using UnityEngine;

public class FirstNoteHandHintTests
{
    [TestCase("red", "右手から")]
    [TestCase("blue", "左手から")]
    [TestCase("red,blue", "両手から")]
    [TestCase("gold", "どちらの手でも")]
    [TestCase("gold,red", "右手から")]
    [TestCase("blue,gold", "左手から")]
    [TestCase("gold,red,blue", "両手から")]
    [TestCase("default", "どちらの手でも")]
    public void EarliestChordDeterminesTextAndColor(string colors, string expected)
    {
        var chart = new ChartData { offsetMs = -250 };
        chart.notes.Add(new NoteData { time = 5000, color = "blue" });
        foreach (string color in colors.Split(',')) chart.notes.Add(new NoteData { time = 1000, color = color });
        Assert.True(FirstNoteHandHint.TryDescribe(chart, out string text, out Color tint));
        Assert.AreEqual(expected, text);
        Color expectedColor = expected == "両手から" ? Color.white : expected == "右手から" ? UISkinPalette.LogoRed
            : expected == "左手から" ? UISkinPalette.LogoBlue : colors == "gold" ? UISkinPalette.NoteGold : Color.white;
        Assert.AreEqual(expectedColor, tint);
        Assert.False(FirstNoteHandHint.TryDescribe(new ChartData(), out _, out _));
    }

    // 左右に分かれた金2個の同時は両手(escapade の最初)。同じ側の金2個は、今までどおりどちらの手でも。
    [TestCase(-1.65f, 1.65f, "両手から")]
    [TestCase(.6f, 1.65f, "どちらの手でも")]
    [TestCase(-1.2f, -.4f, "どちらの手でも")]
    public void SimultaneousGoldOnBothSidesAsksForBothHands(float x1, float x2, string expected)
    {
        var chart = new ChartData();
        chart.notes.Add(new NoteData { color = "gold", time = 1000, x = x1 });
        chart.notes.Add(new NoteData { color = "gold", time = 1000, x = x2 });
        Assert.True(FirstNoteHandHint.TryDescribe(chart, out string text, out Color tint));
        Assert.AreEqual(expected, text);
        Assert.AreEqual(expected == "両手から" ? Color.white : UISkinPalette.NoteGold, tint);
    }

    [Test]
    public void HintStaysBelowTheStageAfterStartAndEndsBeforeArrival()
    {
        var root = new GameObject("HintCountdown", typeof(RectTransform));
        var countdown = root.AddComponent<GameStartCountdown>();
        try
        {
            var chart = new ChartData();
            chart.notes.Add(new NoteData { color = "red", time = 0 });
            double arrival = countdown.Begin(120, "normal", 0, 1, 0);
            countdown.ConfigureFirstHandHint(chart, 0, false, false);
            var view = GameObject.Find("FirstHandHint");
            Assert.NotNull(view, "予告は 3・2・1 の部品とは別の表示");
            Assert.AreNotSame(root.transform, view.transform.parent);
            var hint = view.GetComponentInChildren<TextMeshProUGUI>();
            Assert.AreEqual(new Vector2(0, -360), hint.rectTransform.anchoredPosition);
            Assert.AreEqual(new Vector2(720, 64), hint.rectTransform.sizeDelta);
            Assert.AreEqual(44, hint.fontSize);
            Assert.False(hint.raycastTarget);
            double one = countdown.FirstBeatDspTime + countdown.CountLength * 2, end = arrival - .15;
            Assert.AreEqual(0, FirstNoteHandHint.AlphaAt(one - .01, one, end));
            Assert.AreEqual(.5, FirstNoteHandHint.AlphaAt(one + .09, one, end), .001);
            Assert.AreEqual(1, FirstNoteHandHint.AlphaAt(one + .18, one, end), .001);
            Assert.AreEqual(.5, FirstNoteHandHint.AlphaAt(arrival - .24, one, end), .001);
            Assert.AreEqual(0, FirstNoteHandHint.AlphaAt(end, one, end), .001, "到着0.15秒前までに消えきる");
            countdown.Apply(countdown.EndDspTime + .01);
            Assert.True(countdown == null, "3・2・1 の部品は START のすぐあとに消える(今までどおり)");
            Assert.NotNull(GameObject.Find("FirstHandHint"), "予告は最初のノーツの前まで残る");
        }
        finally
        {
            if (root != null) Object.DestroyImmediate(root);
            FirstNoteHandHintView.Hide();
        }
    }

    [Test]
    public void HintFollowsTheSongClockAndHidesWhilePaused()
    {
        try
        {
            FirstNoteHandHintView.Show("右手から", Color.red, 10, 12);
            var label = GameObject.Find("FirstHandHint").GetComponentInChildren<TextMeshProUGUI>();
            FirstNoteHandHintView.Apply(9.9, false);
            Assert.AreEqual(0, label.alpha, .001);
            FirstNoteHandHintView.Apply(10.09, false);
            Assert.AreEqual(.5, label.alpha, .001);
            FirstNoteHandHintView.Apply(11, true);
            Assert.AreEqual(0, label.alpha, .001, "曲が止まっている間は隠す");
            Assert.NotNull(GameObject.Find("FirstHandHint"), "止まっている間は消さない(再開したら戻る)");
            FirstNoteHandHintView.Apply(11, false);
            Assert.AreEqual(1, label.alpha, .001);
            FirstNoteHandHintView.Apply(12, false);
            Assert.IsNull(GameObject.Find("FirstHandHint"), "最初のノーツの0.15秒前で消える");
            FirstNoteHandHintView.Apply(12.5, false);
        }
        finally { FirstNoteHandHintView.Hide(); }
    }

    [Test]
    public void HintWindowIsInSongSecondsFromThePlayStart()
    {
        var root = new GameObject("HintSongClock", typeof(RectTransform));
        var countdown = root.AddComponent<GameStartCountdown>();
        try
        {
            var chart = new ChartData();
            chart.notes.Add(new NoteData { color = "blue", time = 32000 });
            double start = countdown.Begin(120, "normal", 0, 1, 2);
            // 音源の30秒から鳴らし、最初のノーツはそこから2秒後(音源の32秒)。
            countdown.ConfigureFirstHandHint(chart, 2, false, false, 30);
            Assert.AreEqual(31.85, FirstNoteHandHintView.CurrentEnd, 1e-6, "最初のノーツの0.15秒前(音源の秒)");
            double one = countdown.FirstBeatDspTime + countdown.CountLength * 2;
            Assert.AreEqual(30 + (one - start), FirstNoteHandHintView.CurrentBegin, 1e-6, "「1」の時刻を音源の秒に直す");
        }
        finally
        {
            Object.DestroyImmediate(root);
            FirstNoteHandHintView.Hide();
        }
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    public void TutorialAndCalibrationDoNotCreateHint(bool tutorial, bool calibration)
    {
        var root = new GameObject("ExcludedCountdown", typeof(RectTransform));
        try
        {
            var countdown = root.AddComponent<GameStartCountdown>();
            var chart = new ChartData();
            chart.notes.Add(new NoteData { color = "red" });
            countdown.Begin(120, "normal", 0, 1, 0);
            countdown.ConfigureFirstHandHint(chart, 0, tutorial, calibration);
            Assert.IsNull(GameObject.Find("FirstHandHint"));
        }
        finally
        {
            Object.DestroyImmediate(root);
            FirstNoteHandHintView.Hide();
        }
    }
}
