using System.Linq;
using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// 2026-10-30 ユーザー「④もすべてやる」: 実際に生成したUIの位置と寸法、表示条件、発射通知をEditModeで確かめる。
public class SongSelectHelpTests
{
    GameObject root;
    SongSelectSkin skin;
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [SetUp] public void Open()
    {
        root = new GameObject("HelpLayoutTest", typeof(RectTransform), typeof(Canvas));
        ((RectTransform)root.transform).sizeDelta = new Vector2(1920, 1080);
        var ctl = root.AddComponent<SongSelectController>(); ctl.enabled = false;
        skin = root.AddComponent<SongSelectSkin>(); skin.enabled = false;
        typeof(SongSelectSkin).GetField("ctl", Private).SetValue(skin, ctl);
        typeof(SongSelectSkin).GetField("layout", Private).SetValue(skin, (RectTransform)root.transform);
        foreach (var method in new[] { "BuildDiscs", "BuildTitle", "BuildDifficulties", "BuildSecretDifficulty", "BuildAchievements", "BuildTopScores" })
            typeof(SongSelectSkin).GetMethod(method, Private).Invoke(skin, null);
    }

    [TearDown] public void Close() { Object.DestroyImmediate(root); }

    RectTransform Find(string name) =>
        root.GetComponentsInChildren<RectTransform>(true).Single(t => t.name == name);

    Rect Box(RectTransform rect)
    {
        var corners = new Vector3[4]; rect.GetWorldCorners(corners);
        var points = corners.Select(p => root.transform.InverseTransformPoint(p))
            .Select(p => new Vector2(p.x + 960, 540 - p.y)).ToArray();
        return Rect.MinMaxRect(points.Min(p => p.x), points.Min(p => p.y), points.Max(p => p.x), points.Max(p => p.y));
    }

    Rect Box(string name) => Box(Find(name));

    [Test] public void SlotLabelsStayOnLayoutAndDoNotOverlapOtherControls()
    {
        var names = new[] { "PreviousSongTag", "NextSongTag" };
        for (int i = 0; i < names.Length; i++)
        {
            var tag = Find(names[i]);
            Assert.AreSame(root.transform, tag.parent);
            Assert.AreEqual(new Vector2(i == 0 ? 481 : 1439, 715), Box(tag).center);
            Assert.AreEqual(new Vector2(280, 44), tag.sizeDelta);
            Assert.AreEqual(SongSelectSkin.Cyan, tag.GetComponent<SongSelectDiscGraphic>().Edge);
            var label = tag.Find("Action").GetComponent<TextMeshProUGUI>();
            Assert.AreEqual(i == 0 ? "◀ まえの曲" : "つぎの曲 ▶", label.text);
            Assert.AreEqual(30, label.fontSize);
            Assert.True((label.fontStyle & FontStyles.Bold) != 0);
            Assert.AreSame(UISkinKit.JapaneseFallbackFontAsset(), label.font);
            Assert.AreEqual(22, tag.Find("Seconds").GetComponent<TextMeshProUGUI>().fontSize);
            foreach (var other in new[] { "StartHint", "Achievements", "TopScores", "SongTitlePanel" })
                Assert.False(Box(tag).Overlaps(Box(other)), names[i] + " / " + other);
            for (int d = 0; d < 3; d++)
                Assert.False(Box(tag).Overlaps(Box((RectTransform)Find("Difficulty" + d).Find("HitArea"))));
        }

        var hint = Find("StartHint").GetComponent<TextMeshProUGUI>();
        Assert.AreEqual(34, hint.fontSize);
        Assert.AreEqual(new Vector2(960, 710), Box("StartHint").center);
        Assert.AreEqual(new Vector2(660, 44), hint.rectTransform.sizeDelta);
        Rect library = Box("LibraryPosition"), records = Box("Achievements");
        Assert.AreEqual(new Vector2(421, 813), library.center);
        Assert.True(records.Contains(library.min) && records.Contains(library.max));
        Assert.False(library.Overlaps(Box("AchievementHeading")));
        Assert.False(library.Overlaps(Box("AchievementDifficulty")));
    }

    [Test] public void DifficultyTargetsAreLargeSeparatedAndClearOfPanels()
    {
        Rect previous = default;
        for (int i = 0; i < 3; i++)
        {
            var item = Find("Difficulty" + i);
            Assert.AreEqual(new Vector2(680 + 280 * i, 830), Box(item).center);
            Assert.AreEqual(new Vector2(150, 150), ((RectTransform)item.Find("Circle")).sizeDelta);
            Assert.AreEqual(new Vector2(150, 150), ((RectTransform)item.Find("CircleEdge")).sizeDelta);
            var target = item.GetComponent<SongSelectDiscTarget>();
            Assert.AreEqual(1, target.HoldSeconds);
            Assert.False(target.Circle);
            Assert.AreEqual(new Vector2(240, 200), target.HitArea.sizeDelta);
            Rect hit = Box(target.HitArea);
            Assert.AreEqual(740, hit.yMin); Assert.AreEqual(940, hit.yMax);
            if (i > 0) Assert.AreEqual(40, hit.xMin - previous.xMax);
            previous = hit;
            foreach (var name in new[] { "PreviousSongTag", "NextSongTag", "StartHint", "Achievements", "TopScores", "SongTitlePanel", "TitleShadow" })
                Assert.False(hit.Overlaps(Box(name)), item.name + " / " + name);
        }
        Assert.AreEqual(new Vector2(840, 162), Find("DifficultyTray").sizeDelta);
        foreach (var name in new[] { "Achievements", "TopScores", "SongTitlePanel", "PreviousSongTag", "NextSongTag" })
            Assert.False(Box("DifficultyTray").Overlaps(Box(name)), name);
        Assert.AreEqual(new Vector2(960, 832), Box("RushEOnlyDifficulty").center);
    }

    [Test] public void GhostWaitsEightThenTwelveSecondsAndStopsAfterTwoShots()
    {
        var state = new SongSelectGhostState();
        state.Tick(7.99f); Assert.False(state.ShouldShow(false, false, false, false, 0));
        state.Tick(.02f); Assert.True(state.ShouldShow(false, false, false, false, 0));
        Assert.False(state.ShouldShow(true, false, false, false, 0));
        Assert.False(state.ShouldShow(false, true, false, false, 0));
        Assert.False(state.ShouldShow(false, false, true, false, 0));
        Assert.False(state.ShouldShow(false, false, false, true, 0));
        Assert.False(state.ShouldShow(false, false, false, false, .01f));
        state.RegisterShot();
        state.Tick(float.NaN); state.Tick(float.PositiveInfinity); state.Tick(-10);
        state.Tick(11.99f); Assert.False(state.ShouldShow(false, false, false, false, 0));
        state.Tick(.02f); Assert.True(state.ShouldShow(false, false, false, false, 0));
        state.RegisterShot(); state.Tick(100);
        Assert.AreEqual(2, state.Shots);
        Assert.False(state.ShouldShow(false, false, false, false, 0));
        var nextVisit = new SongSelectGhostState(); nextVisit.Tick(8);
        Assert.True(nextVisit.ShouldShow(false, false, false, false, 0));
    }

    [Test] public void GhostShowsOneOneTwoSecondHoldsInASevenSecondLoop()
    {
        SongSelectGhostGuide.Sample(.5f, out var point, out float fill);
        Assert.AreEqual(SongSelectSkin.DiscSlotPosition(1), point); Assert.AreEqual(.5f, fill, .0001f);
        SongSelectGhostGuide.Sample(2.1f, out point, out fill);
        Assert.AreEqual(new Vector2(0, -288), point); Assert.AreEqual(.5f, fill, .0001f);
        SongSelectGhostGuide.Sample(4.2f, out point, out fill);
        Assert.AreEqual(SongSelectSkin.DiscSlotPosition(0), point); Assert.AreEqual(.5f, fill, .0001f);
        SongSelectGhostGuide.Sample(1.3f, out point, out fill);
        Assert.That(Vector2.Distance(point, Vector2.Lerp(SongSelectSkin.DiscSlotPosition(1), new Vector2(0, -288), .5f)), Is.LessThan(.001f));
        Assert.Zero(fill);
        SongSelectGhostGuide.Sample(7, out point, out fill);
        Assert.AreEqual(SongSelectSkin.DiscSlotPosition(1), point); Assert.Zero(fill);
    }

    [Test] public void TargetNotifiesOnceForClickOrAcceptedShot()
    {
        var go = new GameObject("Choice", typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(root.transform, false);
        var button = go.GetComponent<Button>();
        var target = SongSelectDiscTarget.Attach(button, 1, false);
        int fired = 0, clicked = 0;
        target.Fired += () => fired++;
        button.onClick.AddListener(() => clicked++);
        button.onClick.Invoke(); Assert.AreEqual(1, fired); Assert.AreEqual(1, clicked);
        Assert.True(target.TryShoot()); Assert.AreEqual(2, fired); Assert.AreEqual(2, clicked);
        target.ShootOverride = () => false;
        Assert.False(target.TryShoot()); Assert.AreEqual(2, fired);
        target.ShootOverride = () => true;
        Assert.True(target.TryShoot()); Assert.AreEqual(3, fired); Assert.AreEqual(2, clicked);
        button.interactable = false;
        Assert.False(target.TryShoot()); Assert.AreEqual(3, fired);
    }
}
