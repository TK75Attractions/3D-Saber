using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

// 実績解除の知らせ(2026-10-29)。右上の配置・実時間の寿命・段の安定な並べ替え・音と光を確かめる。
// 描画する色も調べ、演出を下げる設定では年齢を変えても動かないことを確かめる。
public class AchievementToastTests
{
    const double Frame = 1.0 / 60.0;

    [TearDown]
    public void TearDown()
    {
        if (AchievementToast.Instance != null) Object.DestroyImmediate(AchievementToast.Instance.gameObject);
    }

    [Test]
    public void EachCardFadesInHoldsAndIsGoneWithinFiveSeconds()
    {
        Assert.LessOrEqual(AchievementToast.LifeSeconds, 5f, "1枚は5秒以内に消える");
        Assert.AreEqual(AchievementToast.EnterSeconds + AchievementToast.HoldSeconds + AchievementToast.ExitSeconds, AchievementToast.LifeSeconds);
        Assert.AreEqual(0f, AchievementToast.CardPose(0).Alpha);
        Assert.AreEqual(40f, AchievementToast.CardPose(0).Offset, "右から入る");
        Assert.AreEqual(1f, AchievementToast.CardPose(AchievementToast.EnterSeconds).Alpha, 1e-5f);
        var middle = AchievementToast.CardPose(AchievementToast.EnterSeconds + AchievementToast.HoldSeconds * .5);
        Assert.AreEqual(1f, middle.Alpha);
        Assert.AreEqual(0f, middle.Offset);
        Assert.AreEqual(1f, middle.Scale);
        var leaving = AchievementToast.CardPose(AchievementToast.LifeSeconds - .1);
        Assert.Less(leaving.Alpha, .5f);
        Assert.Greater(leaving.Offset, 0f, "消えるときも右へ抜ける");
        Assert.AreEqual(0f, AchievementToast.CardPose(AchievementToast.LifeSeconds).Alpha);
        Assert.AreEqual(0f, AchievementToast.CardPose(-1).Alpha);
        Assert.AreEqual(0f, AchievementToast.CardPose(.15, true).Offset);
        Assert.AreEqual(1f, AchievementToast.CardPose(.15, true).Scale);
        Assert.AreEqual(0f, AchievementToast.CardPose(4.2, true).Offset);
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
            maxVisible = Math.Max(maxVisible, now.Count);
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

    // 簡単なものから出すので、後ろに待っているカードがある段なしのカードは短く(2.2 秒)、段のあるカードと最後の1枚は 4.5 秒。
    [Test]
    public void PlainCardsAreShortWhileOthersWaitSoTheHardestComesSooner()
    {
        Assert.LessOrEqual(AchievementToast.ShortLifeSeconds, 2.5f);
        Assert.GreaterOrEqual(AchievementToast.ShortHoldSeconds, 1f, "読める長さは止まる");
        var queue = new AchievementToastQueue(AchievementToast.MaxVisible);
        foreach (string m in new[] { "a", "b", "c" }) queue.Enqueue(m);
        queue.Enqueue("bronze", AchievementTier.Bronze);
        queue.Enqueue("gold", AchievementTier.Gold);
        var life = new Dictionary<string, float>();
        var shownAt = new Dictionary<string, double>();
        for (double time = 0; !queue.Idle && time < 30; time += Frame)
        {
            queue.Tick(time, true);
            for (int slot = 0; slot < queue.Capacity; slot++)
            {
                var card = queue.At(slot);
                if (card != null && !life.ContainsKey(card.Message)) { life[card.Message] = card.Life; shownAt[card.Message] = time; }
            }
        }
        Assert.IsTrue(queue.Idle);
        foreach (string m in new[] { "a", "b", "c" }) Assert.AreEqual(AchievementToast.ShortLifeSeconds, life[m], 1e-6, m);
        Assert.AreEqual(AchievementToast.LifeSeconds, life["bronze"], 1e-6, "段のあるカードは短くしない");
        Assert.AreEqual(AchievementToast.LifeSeconds, life["gold"], 1e-6);
        Assert.Less(shownAt["gold"], 2 * AchievementToast.ShortLifeSeconds + 2 * AchievementToast.StaggerSeconds + 3 * Frame,
            "いちばん難しい金は、段なし3枚のあとすぐ(5秒より前)に出る");
        var single = new AchievementToastQueue(AchievementToast.MaxVisible);
        single.Enqueue("only");
        single.Tick(0, true);
        Assert.AreEqual(AchievementToast.LifeSeconds, single.At(0).Life, 1e-6, "最後の1枚は段なしでも 4.5 秒");
        var pose = AchievementToast.CardPose(AchievementToast.ShortLifeSeconds - AchievementToast.ExitSeconds - .01,
            AchievementToast.ShortLifeSeconds, false);
        Assert.AreEqual(1f, pose.Alpha, "短いカードも止まっている間は見える");
        Assert.AreEqual(0f, AchievementToast.CardPose(AchievementToast.ShortLifeSeconds, AchievementToast.ShortLifeSeconds, false).Alpha);
    }

    // 段は色だけでなく◆の数でも分かる。結果画面の順位のリールが回っている間は、新しいカードを出さない(音がぶつかる)。
    [Test]
    public void TiersAreMarkedByDiamondsAndTheRankingWheelHoldsNewCards()
    {
        Assert.AreEqual("実績解除", AchievementToast.CaptionFor(AchievementTier.None));
        Assert.AreEqual("実績解除  ◆", AchievementToast.CaptionFor(AchievementTier.Bronze));
        Assert.AreEqual("実績解除  ◆◆", AchievementToast.CaptionFor(AchievementTier.Silver));
        Assert.AreEqual("実績解除  ◆◆◆", AchievementToast.CaptionFor(AchievementTier.Gold));
        Assert.AreEqual("実績解除  ◆◆◆◆", AchievementToast.CaptionFor(AchievementTier.Rainbow));
        Assert.IsTrue(AchievementToast.CanStart("Result", false));
        Assert.IsFalse(AchievementToast.CanStart("Result", false, true), "順位のリールの間は出さない");
        Assert.IsFalse(AchievementToast.CanStart("Game", false), "演奏中は出さない");
        Assert.IsFalse(AchievementToast.CanStart("Result", true), "幕の間は出さない");
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
        Assert.AreEqual("Hardをクリアした  <color=#FFD84D><b>初獲得！</b></color>", AchievementToast.Message("Hardをクリアした", 1),
            "1人目は「1人目」でなく「初獲得！」");
        Assert.AreEqual("Hardをクリアした  <color=#FFD84D><b>2人目</b></color>", AchievementToast.Message("Hardをクリアした", 2));
        Assert.AreEqual("Hardをクリアした", AchievementToast.Message("Hardをクリアした", 0), "番号が無い(記録できない)ときは文だけ");
        Assert.AreEqual("", AchievementToast.Message(null, 0));
    }

    [Test]
    public void CardsUseTheTopRightAndStayAboveTheJudgeRows()
    {
        Assert.AreEqual(new Vector2(-24f, -18f), AchievementToast.SlotPosition(0));
        Assert.AreEqual(new Vector2(-24f, -156f), AchievementToast.SlotPosition(1));
        Assert.AreEqual(540f, AchievementToast.CardWidth);
        Assert.AreEqual(126f, AchievementToast.CardHeight);
        Assert.AreEqual(84f, AchievementToast.IconSize);
        Assert.AreEqual(282f, AchievementToast.CoveredHeight);
        Assert.LessOrEqual(AchievementToast.CoveredHeight, 300f);
        float judgeTop = 1080f * .5f - 186f - 58f * .5f;
        Assert.Less(AchievementToast.CoveredHeight, judgeTop, "判定の表の上の端より上");
        Assert.Less(AchievementToast.CoveredHeight + AchievementCardFrame.MaxGlowWidth, judgeTop, "虹の光も表にかからない");
        Assert.AreEqual(910, AchievementToast.SortingOrder);
        Assert.AreEqual(ResultSkin.RankLandingTime, AchievementToast.ResultLandingSeconds);
    }

    static AchievementUnlock Unlock(string id, AchievementTier tier) =>
        new AchievementUnlock(new AchievementCatalog.Definition(id, id + "を達成した", "出さない名前", "試験", tier, _ => true), 3);

    [Test]
    public void ShowSortsByTierAndPreservesTheSuppliedOrderWithinEachTier()
    {
        var unlocks = new[]
        {
            Unlock("gold", AchievementTier.Gold), Unlock("silver-b", AchievementTier.Silver),
            Unlock("none-b", AchievementTier.None), Unlock("bronze-b", AchievementTier.Bronze),
            Unlock("rainbow", AchievementTier.Rainbow), Unlock("silver-a", AchievementTier.Silver),
            Unlock("none-a", AchievementTier.None), Unlock("bronze-a", AchievementTier.Bronze),
        };
        AchievementToast.Show(unlocks);
        var queue = AchievementToast.Instance.Queue;
        var seen = new HashSet<AchievementToastQueue.Card>();
        var shown = new List<AchievementToastQueue.Card>();
        for (double now = 10; now < 40 && !queue.Idle; now += .2)
        {
            queue.Tick(now, true);
            for (int slot = 0; slot < queue.Capacity; slot++)
            {
                var card = queue.At(slot);
                if (card != null && seen.Add(card)) shown.Add(card);
            }
        }
        Assert.IsTrue(queue.Idle);
        var expected = new[] { unlocks[2], unlocks[6], unlocks[3], unlocks[7], unlocks[1], unlocks[5], unlocks[0], unlocks[4] };
        CollectionAssert.AreEqual(expected.Select(u => AchievementToast.Message(u.Text, u.Number)).ToArray(),
            shown.Select(c => c.Message).ToArray());
        CollectionAssert.AreEqual(expected.Select(u => u.Tier).ToArray(), shown.Select(c => c.Tier).ToArray());
    }

    [Test]
    public void SameOrLowerTierWaitsButHigherTierAlwaysSounds()
    {
        var host = AchievementToast.Ensure();
        int before = MenuSfx.PlayCount;
        host.Queue.Enqueue("最初", AchievementTier.Bronze);
        host.Step(10, true);
        Assert.AreEqual(before + 1, MenuSfx.PlayCount);
        Assert.AreEqual("AchievementChimeBronze", MenuSfx.LastPlayed);

        host.Queue.Enqueue("同じ段", AchievementTier.Bronze);
        host.Step(10.2, true);
        Assert.AreEqual(before + 1, MenuSfx.PlayCount, "同じ段は0.4秒以内に重ねない");

        host.Queue.Clear();
        host.Queue.Enqueue("上の段", AchievementTier.Gold);
        host.Step(10.3, true);
        Assert.AreEqual(before + 2, MenuSfx.PlayCount, "上の段は間隔によらず鳴る");
        Assert.AreEqual("AchievementChimeGold", MenuSfx.LastPlayed);

        host.Queue.Clear();
        host.Queue.Enqueue("下の段", AchievementTier.Silver);
        host.Step(10.5, true);
        Assert.AreEqual(before + 2, MenuSfx.PlayCount);

        host.Queue.Clear();
        host.Queue.Enqueue("間隔をあけた同じ段", AchievementTier.Gold);
        host.Step(10.7, true);
        Assert.AreEqual(before + 3, MenuSfx.PlayCount, "鳴らさなかったカードは間隔の起点にしない");
    }

    [TestCase(AchievementTier.None, .6, -18.0)]
    [TestCase(AchievementTier.Bronze, .7, -17.0)]
    [TestCase(AchievementTier.Silver, .9, -16.0)]
    [TestCase(AchievementTier.Gold, 1.4, -15.0)]
    [TestCase(AchievementTier.Rainbow, 2.0, -14.0)]
    public void ChimesHaveTheExpectedLengthPeakAndRepeatableWaveform(AchievementTier tier, double seconds, double peakDb)
    {
        var first = AchievementToast.ChimeSamples(tier);
        SfxLab.Reseed(999);
        SfxLab.Noise(.01);
        var second = AchievementToast.ChimeSamples(tier);
        Assert.AreEqual(seconds, first.Length / (double)SfxLab.Rate, 1.0 / SfxLab.Rate);
        Assert.LessOrEqual(first.Length / (double)SfxLab.Rate, 2.2);
        Assert.AreEqual(peakDb, SfxLab.PeakDb(first), .5);
        Assert.IsTrue(first.All(v => !float.IsNaN(v) && !float.IsInfinity(v)));
        CollectionAssert.AreEqual(first, second, "作り直しても同じ波形");
        if (tier == AchievementTier.None) CollectionAssert.AreEqual(first, AchievementToast.ChimeSamples());
    }

    [Test]
    public void FramesHaveDistinctColorsAndNoneHasNoGlowOrStreak()
    {
        var tiers = (AchievementTier[])Enum.GetValues(typeof(AchievementTier));
        Assert.AreEqual(tiers.Length, tiers.Select(t => AchievementCardFrame.EdgeColor(t, 0f, 0, true)).Distinct().Count());
        CollectionAssert.AreEqual(new[] { 0f, 10f, 14f, 18f, 24f }, tiers.Select(AchievementCardFrame.GlowWidth).ToArray());
        Assert.AreEqual(0f, AchievementCardFrame.GlowColor(AchievementTier.None, .2f, .3, false).a);
        Assert.AreEqual(0f, AchievementCardFrame.StreakColor(AchievementTier.None, .2f, .3, false).a);
        Assert.AreEqual(2.4f, AchievementCardFrame.CycleSeconds(AchievementTier.Bronze));
        Assert.AreEqual(1.8f, AchievementCardFrame.CycleSeconds(AchievementTier.Silver));
        Assert.AreEqual(1.3f, AchievementCardFrame.CycleSeconds(AchievementTier.Gold));
        Assert.AreNotEqual(AchievementCardFrame.EdgeColor(AchievementTier.Rainbow, 0f, 1, true),
            AchievementCardFrame.EdgeColor(AchievementTier.Rainbow, .5f, 1, true), "止めても虹の並びは残る");
        Assert.AreNotEqual(AchievementCardFrame.EdgeColor(AchievementTier.Rainbow, .2f, .3, false),
            AchievementCardFrame.EdgeColor(AchievementTier.Rainbow, .2f, 1.3, false), "通常は色が回る");
        Assert.Greater(AchievementCardFrame.EntryPulse(AchievementToast.EnterSeconds, false),
            AchievementCardFrame.EntryPulse(1, false));
    }

    [TestCase(AchievementTier.None)]
    [TestCase(AchievementTier.Bronze)]
    [TestCase(AchievementTier.Silver)]
    [TestCase(AchievementTier.Gold)]
    [TestCase(AchievementTier.Rainbow)]
    public void ReducedEffectsKeepTheActualFrameAndTrophyMeshColorsStill(AchievementTier tier)
    {
        var frameObject = new GameObject("試験のふち", typeof(RectTransform));
        var trophyObject = new GameObject("試験のトロフィー", typeof(RectTransform));
        try
        {
            var frame = frameObject.AddComponent<AchievementCardFrame>();
            frame.rectTransform.sizeDelta = new Vector2(AchievementToast.CardWidth, AchievementToast.CardHeight);
            var trophy = trophyObject.AddComponent<AchievementTrophyGraphic>();
            trophy.rectTransform.sizeDelta = Vector2.one * AchievementToast.IconSize;
            frame.SetAppearance(tier, .3, true);
            trophy.SetAppearance(tier, .3, true);
            var frameColors = MeshColors(frame);
            var trophyColors = MeshColors(trophy);
            Assert.IsNotEmpty(frameColors);
            Assert.IsNotEmpty(trophyColors);
            frame.SetAppearance(tier, 3.2, true);
            trophy.SetAppearance(tier, 3.2, true);
            CollectionAssert.AreEqual(frameColors, MeshColors(frame));
            CollectionAssert.AreEqual(trophyColors, MeshColors(trophy));
            Assert.AreEqual(AchievementCardFrame.EdgeColor(tier, 0f, .3, true),
                AchievementCardFrame.EdgeColor(tier, 0f, 3.2, true), "見出しの色も動かない");
            Assert.AreEqual(0f, AchievementCardFrame.StreakColor(tier, .2f, 3.2, true).a);
            Assert.AreEqual(0f, AchievementCardFrame.EntryPulse(.3, true));
            if (tier != AchievementTier.None) Assert.Greater(AchievementCardFrame.GlowColor(tier, .2f, 3.2, true).a, 0f);
        }
        finally
        {
            Object.DestroyImmediate(frameObject);
            Object.DestroyImmediate(trophyObject);
        }
    }

    static Color32[] MeshColors(MaskableGraphic graphic)
    {
        var mesh = new Mesh();
        try
        {
            using (var helper = new VertexHelper())
            {
                // Graphic には古い OnPopulateMesh(Mesh) もあるので、VertexHelper の方を選ぶ。
                var populate = graphic.GetType().GetMethod("OnPopulateMesh", BindingFlags.Instance | BindingFlags.NonPublic, null,
                    new[] { typeof(VertexHelper) }, null);
                Assert.NotNull(populate);
                populate.Invoke(graphic, new object[] { helper });
                helper.FillMesh(mesh);
                return mesh.colors32;
            }
        }
        finally { Object.DestroyImmediate(mesh); }
    }
}
