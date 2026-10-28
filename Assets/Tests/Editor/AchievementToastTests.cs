using System.Collections.Generic;
using NUnit.Framework;

// 実績解除の知らせ(AchievementToast・AchievementToastQueue、2026-10-29)。1枚は出し始めた実時刻から5秒以内に消える(処理落ちしても)・
// 一度に見えるのは2枚まで、残りは空いた段へ順番に・演奏中と幕の間は出さない・結果画面の RANK の見出しにかからない・文の形・控えめな音。
public class AchievementToastTests
{
    const double Frame = 1.0 / 60.0;

    [Test]
    public void EachCardFadesInHoldsAndIsGoneWithinFiveSeconds()
    {
        Assert.LessOrEqual(AchievementToast.LifeSeconds, 5f, "1枚は5秒以内に消える");
        Assert.AreEqual(AchievementToast.EnterSeconds + AchievementToast.HoldSeconds + AchievementToast.ExitSeconds, AchievementToast.LifeSeconds);
        Assert.AreEqual(0f, AchievementToast.CardPose(0).Alpha);
        Assert.Greater(AchievementToast.CardPose(0).Offset, 0f, "上から降りてくる");
        Assert.AreEqual(1f, AchievementToast.CardPose(AchievementToast.EnterSeconds).Alpha, 1e-5f);
        var middle = AchievementToast.CardPose(AchievementToast.EnterSeconds + AchievementToast.HoldSeconds * .5);
        Assert.AreEqual(1f, middle.Alpha);
        Assert.AreEqual(0f, middle.Offset);
        Assert.AreEqual(1f, middle.Scale);
        var leaving = AchievementToast.CardPose(AchievementToast.LifeSeconds - .1);
        Assert.Less(leaving.Alpha, .5f);
        Assert.Greater(leaving.Offset, 0f, "消えるときは少し上がる");
        Assert.AreEqual(0f, AchievementToast.CardPose(AchievementToast.LifeSeconds).Alpha);
        Assert.AreEqual(0f, AchievementToast.CardPose(-1).Alpha);
    }

    [Test]
    public void AtMostTwoCardsAreShownAndTheRestFollowInOrder()
    {
        var queue = new AchievementToastQueue(AchievementToast.MaxVisible);
        var messages = new[] { "a", "b", "c", "d", "e" };
        foreach (string m in messages) queue.Enqueue(m);
        var shownAt = new Dictionary<string, double>();
        var goneAt = new Dictionary<string, double>();
        var slotOf = new Dictionary<string, int>();
        int maxVisible = 0;
        double time = 100;
        var previous = new HashSet<string>();
        while (!queue.Idle && time < 130)
        {
            queue.Tick(time, true);
            var now = new HashSet<string>();
            for (int slot = 0; slot < queue.Capacity; slot++)
            {
                var card = queue.At(slot);
                if (card == null) continue;
                now.Add(card.Message);
                if (!shownAt.ContainsKey(card.Message)) { shownAt[card.Message] = time; slotOf[card.Message] = slot; }
            }
            foreach (string m in previous) if (!now.Contains(m)) goneAt[m] = time;
            maxVisible = System.Math.Max(maxVisible, now.Count);
            previous = now;
            time += Frame;
        }
        Assert.IsTrue(queue.Idle);
        Assert.AreEqual(2, AchievementToast.MaxVisible);
        Assert.AreEqual(AchievementToast.MaxVisible, maxVisible, "一度に見えるのは2枚まで");
        Assert.AreEqual(100, shownAt["a"], 1e-9, "すぐに出し始める");
        for (int i = 1; i < messages.Length; i++) Assert.Greater(shownAt[messages[i]], shownAt[messages[i - 1]], "順番に出る");
        Assert.AreEqual(AchievementToast.StaggerSeconds, shownAt["b"] - shownAt["a"], Frame * 1.5, "少しずらして入る");
        Assert.AreEqual(0, slotOf["a"]); Assert.AreEqual(1, slotOf["b"]);
        Assert.GreaterOrEqual(shownAt["c"], goneAt["a"] - 1e-9, "3枚目は空いてから");
        Assert.AreEqual(0, slotOf["c"], "空いた上の段へ");
        foreach (string m in messages)
            Assert.LessOrEqual(goneAt[m] - shownAt[m], AchievementToast.LifeSeconds + Frame * 1.5, m + " は5秒以内に消える");
    }

    [Test]
    public void ALongFrameDoesNotStretchTheCardBeyondItsRealLifetime()
    {
        var queue = new AchievementToastQueue(AchievementToast.MaxVisible);
        queue.Enqueue("slow");
        queue.Tick(10, true);
        Assert.AreEqual(1, queue.VisibleCount);
        queue.Tick(11, true);
        queue.Tick(13.5, true); // 2.5 秒止まったコマ
        Assert.AreEqual(1, queue.VisibleCount);
        queue.Tick(10 + AchievementToast.LifeSeconds - .01, true);
        Assert.AreEqual(1, queue.VisibleCount);
        queue.Tick(10 + AchievementToast.LifeSeconds, true);
        Assert.AreEqual(0, queue.VisibleCount, "出し始めてから実時間 4.5 秒で消える");
    }

    [Test]
    public void NothingStartsWhilePlayingOrDuringTheCurtainButShownCardsStillLeave()
    {
        Assert.IsFalse(AchievementToast.CanStart("Game", false), "演奏中は出さない");
        Assert.IsFalse(AchievementToast.CanStart("Result", true), "幕の間は出さない");
        Assert.IsTrue(AchievementToast.CanStart("Result", false));
        Assert.IsTrue(AchievementToast.CanStart("SongSelect", false));

        var queue = new AchievementToastQueue(AchievementToast.MaxVisible);
        queue.Enqueue("first");
        for (int i = 0; i < 600; i++) Assert.AreEqual(0, queue.Tick(i * Frame, false));
        Assert.AreEqual(0, queue.VisibleCount);
        Assert.AreEqual(1, queue.Waiting);
        Assert.AreEqual(1, queue.Tick(20, true), "出せるようになったら出す");
        queue.Enqueue("second");
        queue.Enqueue("third");
        queue.Tick(21, false);
        Assert.AreEqual(1, queue.VisibleCount, "出せない間は次を出さない");
        queue.Tick(20 + AchievementToast.LifeSeconds, false);
        Assert.AreEqual(0, queue.VisibleCount, "出ているカードは消えていく");
        Assert.AreEqual(2, queue.Waiting);
        queue.Clear();
        Assert.IsTrue(queue.Idle);
    }

    [Test]
    public void MessageShowsTheNumberOfPeopleInGold()
    {
        Assert.AreEqual("Hardをクリアした  <color=#FFD84D><b>37人目</b></color>", AchievementToast.Message("Hardをクリアした", 37));
        Assert.AreEqual("Hardをクリアした", AchievementToast.Message("Hardをクリアした", 0), "番号が無い(記録できない)ときは文だけ");
        Assert.AreEqual("", AchievementToast.Message(null, 0));
    }

    [Test]
    public void CardsStayAboveTheRankHeadingOfTheResult()
    {
        Assert.AreEqual(0f, AchievementToast.SlotPosition(0).x);
        Assert.AreEqual(-AchievementToast.TopMargin, AchievementToast.SlotPosition(0).y);
        Assert.AreEqual(-(AchievementToast.TopMargin + AchievementToast.CardHeight + AchievementToast.Gap), AchievementToast.SlotPosition(1).y);
        Assert.AreEqual(AchievementToast.TopMargin + 2 * AchievementToast.CardHeight + AchievementToast.Gap, AchievementToast.CoveredHeight);
        // 結果画面の RANK の見出しは上から約189px(中心)。86px の字の上の端(約159px)より上に収める。
        Assert.LessOrEqual(AchievementToast.CoveredHeight, 150f, "RANK の見出しにかからない");
        Assert.Greater(AchievementToast.SortingOrder, 900, "照準より上");
        Assert.Less(AchievementToast.SortingOrder, 920, "印の一言の欄・運営の知らせより下");
        Assert.AreEqual(ResultSkin.RankLandingTime, AchievementToast.ResultLandingSeconds, "結果画面ではランクの札が着地したら出す");
    }

    [Test]
    public void TheChimeIsShortAndQuiet()
    {
        var samples = AchievementToast.ChimeSamples();
        Assert.Less(samples.Length / (float)SfxLab.Rate, 1f, "短い");
        Assert.AreEqual(AchievementToast.ChimePeakDb, SfxLab.PeakDb(samples), .1, "控えめ(ピーク -18 dBFS)");
        Assert.LessOrEqual(AchievementToast.ChimePeakDb, -15);
    }
}
