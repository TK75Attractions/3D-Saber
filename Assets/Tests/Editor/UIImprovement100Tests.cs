using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

// 読み取り専用の譜面分析・結果目標と、選曲の実ファイル経路を検証する。
public class UIImprovement100Tests
{
    string directory, songId;
    GameObject root;
    const string Ready = "{\"bpm\":120,\"notes\":[{\"time\":2000,\"color\":\"blue\"}]}";
    const string Empty = "{\"bpm\":120,\"notes\":[]}";

    [TearDown]
    public void Cleanup()
    {
        if (root != null) Object.DestroyImmediate(root);
        if (directory != null)
        {
            string songs = Path.GetFullPath(Path.Combine(Application.streamingAssetsPath, "Songs")) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(directory);
            Assert.True(full.StartsWith(songs, StringComparison.OrdinalIgnoreCase));
            Assert.True(Path.GetFileName(full).StartsWith("__UIImprovement100_", StringComparison.Ordinal));
            if (Directory.Exists(full)) Directory.Delete(full, true);
            if (File.Exists(full + ".meta")) File.Delete(full + ".meta");
        }
        ResultSelectionReturn.Remember(null, null);
    }

    SongSelectController CreateSong(bool onlyHard = false)
    {
        songId = "__UIImprovement100_" + Guid.NewGuid().ToString("N");
        directory = Path.Combine(Application.streamingAssetsPath, "Songs", songId);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "chart_easy.json"), onlyHard ? Empty : Ready);
        File.WriteAllText(Path.Combine(directory, "chart_normal.json"), Empty);
        File.WriteAllText(Path.Combine(directory, "chart_hard.json"), Ready);
        root = new GameObject("UIImprovementTest");
        var controller = root.AddComponent<SongSelectController>(); controller.enabled = false;
        controller.Populate();
        return controller;
    }
    int SongIndex(SongSelectController controller)
    {
        for (int i = 0; i < controller.SongCount; i++) if (controller.SongIdAt(i) == songId) return i;
        Assert.Fail("難易度別譜面のみでも選曲に登録する"); return -1;
    }

    [Test]
    public void DifficultyOnlySongIsListedAndSelectsPlayableHard()
    {
        var controller = CreateSong(true);
        CollectionAssert.Contains(SongSelectController.EnumerateSongIds(), songId);
        controller.Select(SongIndex(controller));
        Assert.AreEqual(2, controller.SelectedDifficultyIndex);
        Assert.Greater(controller.CurrentDifficultyLevel(), 0);
    }

    [Test]
    public void RelativeDifficultyNavigationSkipsUnmadeChartBothDirections()
    {
        var controller = CreateSong(); controller.Select(SongIndex(controller));
        controller.SetDifficulty(0); controller.ChangeDifficulty(1);
        Assert.AreEqual(2, controller.SelectedDifficultyIndex);
        controller.ChangeDifficulty(-1);
        Assert.AreEqual(0, controller.SelectedDifficultyIndex);
    }

    [Test]
    public void ReSelectingCurrentSongDoesNotNotifyOrRestartPreview()
    {
        var controller = CreateSong(); int selected = SongIndex(controller), events = 0;
        controller.OnSelectionChanged += _ => events++;
        controller.Select(selected); controller.Select(selected);
        Assert.AreEqual(1, events);
    }

    [Test]
    public void RepopulateReplacesOwnedRowsWithoutRemovingOtherChildren()
    {
        var controller = CreateSong();
        var content = new GameObject("Content", typeof(RectTransform)); content.transform.SetParent(root.transform);
        var decoration = new GameObject("Decoration"); decoration.transform.SetParent(content.transform);
        var prefab = new GameObject("Prefab", typeof(RectTransform), typeof(Image), typeof(Button)); prefab.transform.SetParent(root.transform);
        var label = new GameObject("Label", typeof(RectTransform), typeof(Text)); label.transform.SetParent(prefab.transform);
        controller.scrollContent = content.GetComponent<RectTransform>(); controller.buttonPrefab = prefab;
        controller.Populate(); controller.Select(SongIndex(controller));
        controller.Populate();
        Assert.AreEqual(controller.SongCount + 1, content.transform.childCount);
        Assert.AreEqual(songId, controller.SongIdAt(controller.SelectedIndex));
        Assert.NotNull(decoration);
    }

    [Test]
    public void ChartInsightsPreserveOrderAndCountLongDurationAndSimultaneousGroups()
    {
        var later = new NoteData { time = 3000, count = 4, lengthMs = 3000, type = "long" };
        var first = new NoteData { time = 1000, direction = "up" };
        var same = new NoteData { time = 1000, color = "red" };
        var chart = new ChartData { bpm = 196, offsetMs = 100, notes = new List<NoteData> { later, first, same } };
        var insight = SongChartInsights.From(chart);
        Assert.AreEqual(3, insight.Notes); Assert.AreEqual(1, insight.Longs); Assert.AreEqual(1, insight.Flicks);
        Assert.AreEqual(1, insight.SimultaneousGroups); Assert.AreEqual(6.1, insight.EndSeconds, .0001);
        Assert.AreSame(later, chart.notes[0]); Assert.AreEqual(3000, later.time);
        StringAssert.Contains("BPM 196", insight.Summary); StringAssert.Contains("0:07", insight.Summary);
    }

    [Test]
    public void EmptyChartInsightIsActionableAndInvalidTimeDoesNotPoisonDuration()
    {
        StringAssert.Contains("別の難易度", SongChartInsights.Empty.Techniques);
        var chart = new ChartData { notes = new List<NoteData> { null, new NoteData { time = float.NaN }, new NoteData { time = 2000 } } };
        var insight = SongChartInsights.From(chart);
        Assert.AreEqual(1, insight.Notes); Assert.AreEqual(2, insight.EndSeconds);
    }

    [Test]
    public void PortraitCoverKeepsSquarePixelsAndSpriteSubrectangle()
    {
        var texture = new Texture2D(400, 800);
        var sprite = Sprite.Create(texture, new Rect(100, 200, 200, 400), Vector2.one * .5f);
        try
        {
            Rect uv = SongSelectDiscGraphic.CoverUvRect(sprite);
            Assert.AreEqual(.25f, uv.x, .0001); Assert.AreEqual(.375f, uv.y, .0001);
            Assert.AreEqual(.5f, uv.width, .0001); Assert.AreEqual(.25f, uv.height, .0001);
            Assert.AreEqual(uv.width * texture.width, uv.height * texture.height, .0001);
        }
        finally { Object.DestroyImmediate(sprite); Object.DestroyImmediate(texture); }
    }

    [Test]
    public void ResultSelectionIsConsumedOnlyOnce()
    {
        ResultSelectionReturn.Remember("製作中", "Hard");
        ResultSelectionReturn.Consume(out var song, out var difficulty);
        Assert.AreEqual("製作中", song); Assert.AreEqual("Hard", difficulty);
        ResultSelectionReturn.Consume(out song, out difficulty);
        Assert.IsNull(song); Assert.IsNull(difficulty);
    }

    [TestCase(.5f, "A", "10.00")]
    [TestCase(.8f, "S+", "10.00")]
    [TestCase(.59999f, "A", "0.01")]
    public void NextRankGoalUsesRealThresholdsAndNeverRoundsSmallGapToZero(float accuracy, string rank, string points)
    {
        string message = ResultImprovementSummary.NextRank(accuracy, 100);
        StringAssert.Contains(rank, message); StringAssert.Contains(points, message);
    }

    [Test]
    public void EmptyResultDoesNotCongratulateAFalsePerfectOrRank()
    {
        StringAssert.Contains("ありません", ResultImprovementSummary.NextRank(1, 0));
        StringAssert.DoesNotContain("ALL PERFECT", ResultImprovementSummary.Achievement(0, 0, 0, 0, 0));
        Assert.AreEqual("--", ResultImprovementSummary.Percentage(0, 0));
    }

    [Test]
    public void AchievementGoalCountsBadAsComboBreakAndGreatAsApGap()
    {
        StringAssert.Contains("BAD + MISS が 3 個", ResultImprovementSummary.Achievement(10, 1, 2, 1, 2));
        StringAssert.Contains("APまであと 3 個", ResultImprovementSummary.Achievement(10, 1, 2, 0, 0));
        StringAssert.Contains("ALL PERFECT", ResultImprovementSummary.Achievement(10, 0, 0, 0, 0));
    }

    [Test]
    public void JudgmentPercentagesCompareAcrossDifferentChartLengths()
    {
        Assert.AreEqual("12.5%", ResultImprovementSummary.Percentage(25, 200));
        Assert.AreEqual("12.5%", ResultImprovementSummary.Percentage(50, 400));
        Assert.AreEqual("0.0%", ResultImprovementSummary.Percentage(0, 200));
    }

    [Test]
    public void BestDifferenceDistinguishesFirstTieImprovementAndGap()
    {
        Assert.AreEqual("FIRST RECORD", ResultImprovementSummary.BestDifference(100, 0, false));
        StringAssert.Contains("同点", ResultImprovementSummary.BestDifference(100, 100, true));
        StringAssert.Contains("+20", ResultImprovementSummary.BestDifference(120, 100, true));
        StringAssert.Contains("まで 20", ResultImprovementSummary.BestDifference(80, 100, true));
        Assert.AreEqual("MASTER / HARD", ResultImprovementSummary.Difficulty("Hard"));
    }
}
