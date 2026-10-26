using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Context = TransitionTips.Context;
using Destination = TransitionTips.Destination;

// 書式・条件・抽選は Unity オブジェクトを作らずに検証する。
public sealed class TransitionTipsTests
{
    bool savedEnabled;

    [SetUp]
    public void SetUp()
    {
        savedEnabled = TransitionTips.Enabled;
        TransitionTips.Enabled = true;
    }

    [TearDown]
    public void TearDown()
    {
        TransitionTips.Enabled = savedEnabled;
    }

    [Test]
    public void ParsesCombinedConditionsCommentsBlankLinesAndLiteralBody()
    {
        var tips = TransitionTips.FromText(
            "\uFEFF# コメント\r\n \r\n  # インデントしたコメント\n" +
            "[曲:Morning] [行き先:結果][裏2][裏で][まれ]  朝の話  \r\n" +
            "普通の本文 # ここは本文\n[まれ]\n");
        Assert.AreEqual(2, tips.Entries.Count);
        var entry = tips.Entries[0];
        Assert.AreEqual("朝の話", entry.Text);
        CollectionAssert.AreEqual(new[] { "Morning" }, entry.SongIds);
        CollectionAssert.AreEqual(new[] { Destination.Result }, entry.Destinations);
        Assert.AreEqual(2, entry.HintLevel);
        Assert.True(entry.OnlySecret);
        Assert.True(entry.Rare);
        Assert.AreEqual("普通の本文 # ここは本文", tips.Entries[1].Text);
        LogAssert.NoUnexpectedReceived();
    }

    [Test]
    public void UnknownConditionsWarnOnceEachAndKeepTheLineAndKnownConditions()
    {
        LogAssert.Expect(LogType.Warning, "TransitionTips: 読めない条件 [謎] を無視します。");
        LogAssert.Expect(LogType.Warning, "TransitionTips: 読めない条件 [行き先:宇宙] を無視します。");
        LogAssert.Expect(LogType.Warning, "TransitionTips: 読めない条件 [曲:] を無視します。");
        LogAssert.Expect(LogType.Warning, "TransitionTips: 読めない条件 [裏9] を無視します。");
        var tips = TransitionTips.FromText(
            "[謎][行き先:宇宙][曲:][裏9][行き先:結果] 一行目\n" +
            "[謎][行き先:宇宙][曲:][裏9] 二行目");
        Assert.AreEqual(2, tips.Entries.Count);
        Assert.AreEqual("一行目", tips.Entries[0].Text);
        Assert.False(tips.Entries[0].Matches(new Context(Destination.Play)));
        Assert.True(tips.Entries[0].Matches(new Context(Destination.Result)));
        Assert.AreEqual("二行目", tips.Entries[1].Text);
        Assert.True(tips.Entries[1].Matches(new Context(Destination.Title)));
        LogAssert.NoUnexpectedReceived();
    }

    [Test]
    public void UnclosedConditionKeepsTheText()
    {
        LogAssert.Expect(LogType.Warning, "TransitionTips: 閉じていない条件を本文として残します: [閉じ忘れ 本文");
        var tips = TransitionTips.FromText("[閉じ忘れ 本文");
        Assert.AreEqual("[閉じ忘れ 本文", tips.Entries[0].Text);
        LogAssert.NoUnexpectedReceived();
    }

    [TestCase(null, 0f)]
    [TestCase("", 0f)]
    [TestCase("abc", 1.5f)]
    [TestCase("ｱｲｳ", 1.5f)]
    [TestCase("ｶﾞ", 1f)]
    [TestCase("赤A ｶ", 2.5f)]
    [TestCase("ＡＢ", 2f)]
    [TestCase("\U00020BB7", 1f)]
    public void DisplayLengthCountsHalfWidthCharacters(string text, float expected)
    {
        Assert.AreEqual(expected, TransitionTips.DisplayLength(text), .0001f);
    }

    [Test]
    public void HoldSecondsHasBoundsFormulaAndMonotonicity()
    {
        Assert.AreEqual(.8f, TransitionTips.HoldSeconds(null), .0001f);
        Assert.AreEqual(.8f, TransitionTips.HoldSeconds("短い"), .0001f);
        Assert.AreEqual(.85f, TransitionTips.HoldSeconds(new string('あ', 10)), .0001f);
        Assert.AreEqual(1.35f, TransitionTips.HoldSeconds(new string('あ', 20)), .0001f);
        Assert.AreEqual(1.6f, TransitionTips.HoldSeconds(new string('あ', 25)), .0001f);
        Assert.AreEqual(1.6f, TransitionTips.HoldSeconds(new string('あ', 100)), .0001f);

        float previous = 0;
        for (int length = 0; length <= 100; length++)
        {
            float current = TransitionTips.HoldSeconds(new string('あ', length));
            Assert.That(current, Is.InRange(.8f, 1.6f));
            Assert.GreaterOrEqual(current, previous);
            previous = current;
        }
    }

    [TestCase("Title", false, false, Destination.Title)]
    [TestCase("SongSelect", true, true, Destination.SongSelect)]
    [TestCase("Result", false, false, Destination.Result)]
    [TestCase("Game", false, false, Destination.Play)]
    [TestCase("Game", true, false, Destination.Practice)]
    [TestCase("Game", false, true, Destination.Calibration)]
    [TestCase("Assets/Scenes/Game.unity", true, false, Destination.Practice)]
    [TestCase("Unknown", false, false, Destination.Unknown)]
    public void ResolvesDestinationBeforeTheNextSceneConsumesSessionFlags(
        string scene, bool tutorial, bool calibration, Destination expected)
    {
        Assert.AreEqual(expected, TransitionTips.DestinationFor(scene, tutorial, calibration));
    }

    [Test]
    public void FiltersSongsDestinationsSecretsAndAlreadyFoundSongHints()
    {
        var tips = TransitionTips.FromText(
            "共通\n" +
            "[曲:Morning] 曲\n" +
            "[曲:Morning][裏2] 曲ヒント\n" +
            "[曲:Morning][行き先:結果][裏3] 結果ヒント\n" +
            "[裏で] 裏専用\n" +
            "[行き先:練習] 練習\n" +
            "[行き先:調整] 調整\n" +
            "[行き先:選曲][裏1] 選曲ヒント\n" +
            "[曲:RushE][裏2] 別の曲\n" +
            "[曲:Morning][裏2][裏で] 発見済み\n");

        AssertMatches(tips, new Context(Destination.Play, "Morning"), "共通", "曲", "曲ヒント");
        AssertMatches(tips, new Context(Destination.Result, "Morning"), "共通", "曲", "曲ヒント", "結果ヒント");
        AssertMatches(tips, new Context(Destination.Play, "Morning", true), "共通", "曲", "裏専用");
        AssertMatches(tips, new Context(Destination.Result, "Morning", true), "共通", "曲", "裏専用");
        AssertMatches(tips, new Context(Destination.Practice, "Morning", true), "共通", "練習");
        AssertMatches(tips, new Context(Destination.Calibration, "Morning", true), "共通", "調整");
        AssertMatches(tips, new Context(Destination.SongSelect, "Morning", true), "共通", "選曲ヒント");
        AssertMatches(tips, new Context(Destination.Title, "Morning", true), "共通");
        AssertMatches(tips, new Context(Destination.Play, "RushE"), "共通", "別の曲");
    }

    static void AssertMatches(TransitionTips tips, Context context, params string[] expected)
    {
        CollectionAssert.AreEquivalent(expected, tips.Entries.Where(e => e.Matches(context)).Select(e => e.Text));
    }

    [Test]
    public void RepeatedConditionKindsStillRequireEveryCondition()
    {
        var tips = TransitionTips.FromText(
            "[曲:Morning][曲:RushE] 両方の曲\n" +
            "[行き先:結果][行き先:プレイ] 両方の画面");
        AssertMatches(tips, new Context(Destination.Play, "Morning"));
        AssertMatches(tips, new Context(Destination.Result, "RushE"));
        LogAssert.NoUnexpectedReceived();
    }

    [Test]
    public void WeightCombinesRareTagsAndOnlyBoostsSonglessHintsForSelection()
    {
        var tips = TransitionTips.FromText(
            "普通\n[裏1] 薄い\n[裏2] 中間\n[裏3] 濃い\n[まれ] 稀\n" +
            "[裏3][まれ] 両方\n[曲:Morning][裏1] 曲");
        var title = new Context(Destination.Title);
        var selection = new Context(Destination.SongSelect);
        double[] titleWeights = { 1, 1, 1, .25, .25, .0625, 1 };
        double[] selectionWeights = { 1, 2, 2, .5, .25, .125, 1 };
        for (int i = 0; i < tips.Entries.Count; i++)
        {
            Assert.AreEqual(titleWeights[i], tips.Entries[i].Weight(title));
            Assert.AreEqual(selectionWeights[i], tips.Entries[i].Weight(selection));
        }
    }

    [Test]
    public void SmallPoolsShowEveryRowBeforeRepeating()
    {
        var tips = TransitionTips.FromText("A\nB\n[まれ] C\n[裏3] D", new System.Random(182));
        var context = new Context(Destination.Title);
        string previous = null;
        for (int round = 0; round < 20; round++)
        {
            var seen = new HashSet<string>();
            for (int i = 0; i < tips.Entries.Count; i++)
            {
                var selected = tips.Select(context);
                Assert.NotNull(selected);
                Assert.True(seen.Add(selected.Text), "一巡する前に同じ行を選ばない");
                Assert.AreNotEqual(previous, selected.Text, "一巡の境目でも連続しない");
                previous = selected.Text;
            }
        }
    }

    [Test]
    public void LargePoolsNeverRepeatWithinTheRecentWindow()
    {
        var source = new StringBuilder();
        for (int i = 0; i < 30; i++) source.Append("行").Append(i).Append('\n');
        var tips = TransitionTips.FromText(source.ToString(), new System.Random(7));
        var context = new Context(Destination.Title);
        var history = new List<string>();
        for (int i = 0; i < 2000; i++)
        {
            string text = tips.Select(context).Text;
            int from = Math.Max(0, history.Count - TransitionTips.RecentWindow);
            CollectionAssert.DoesNotContain(history.GetRange(from, history.Count - from), text,
                "最近の回に出した行をくり返さない");
            history.Add(text);
        }
    }

    [Test]
    public void HistoryIsSharedAcrossContexts()
    {
        var shared = TransitionTips.FromText("共通\n[行き先:選曲] 選曲専用", new System.Random(24));
        Assert.AreEqual("共通", shared.Select(new Context(Destination.Title)).Text);
        Assert.AreEqual("選曲専用", shared.Select(new Context(Destination.SongSelect)).Text);

        var tips = TransitionTips.FromText(
            "[行き先:タイトル] T1\n[行き先:タイトル] T2\n" +
            "[行き先:選曲] S1\n[行き先:選曲] S2", new System.Random(53));
        var title = new Context(Destination.Title);
        var selection = new Context(Destination.SongSelect);
        string firstTitle = tips.Select(title).Text;
        tips.Select(selection);
        tips.Select(selection);
        tips.Select(selection);
        Assert.AreNotEqual(firstTitle, tips.Select(title).Text, "選曲の行を出しても、タイトルで出した行の記録は残る");
    }

    [Test]
    public void SolePreviousCandidateIsSuppressedAndClearSeenAllowsItAgain()
    {
        var tips = TransitionTips.FromText("ひとつ", new System.Random(4));
        var context = new Context(Destination.Title);
        Assert.NotNull(tips.Select(context));
        Assert.IsNull(tips.Select(context));
        tips.ClearSeen();
        Assert.NotNull(tips.Select(context));
    }

    [Test]
    public void DisabledEmptyMissingAndUnmatchedSourcesReturnNoTip()
    {
        var context = new Context(Destination.Title);
        Assert.IsNull(TransitionTips.FromText(null).Select(context));
        Assert.IsNull(TransitionTips.FromText("# コメント\n \n").Select(context));
        Assert.IsNull(TransitionTips.FromText("[行き先:結果] 結果のみ").Select(context));

        string missing = Path.Combine(Application.streamingAssetsPath, "Tips",
            "__missing_" + Guid.NewGuid().ToString("N") + ".txt");
        Assert.IsNull(TransitionTips.FromFile(missing).Select(context));

        var tips = TransitionTips.FromText("ひとつ");
        TransitionTips.Enabled = false;
        Assert.IsNull(tips.Select(context));
        TransitionTips.Enabled = true;
        Assert.NotNull(tips.Select(context), "無効な間は見た記録を進めない");
        LogAssert.NoUnexpectedReceived();
    }

    [Test]
    public void RareWeightsReduceIndependentDrawProbability()
    {
        // 重複禁止では一巡後の件数は等しくなるため、抽選の重みは履歴なしの試行で調べる。
        double rare = FirstDrawShare("普通\n[裏3] 稀", "稀", new Context(Destination.Title), 12345);
        double both = FirstDrawShare("普通\n[裏3][まれ] 稀", "稀", new Context(Destination.Title), 54321);
        Assert.That(rare, Is.InRange(.18, .22));
        Assert.That(both, Is.InRange(.045, .075));
    }

    [Test]
    public void RareRowsStayRareOverManyTransitions()
    {
        // くり返しを避けたまま何度も遷移しても、[裏3] は普通の行よりずっと少なく出る(全部見るまで待つ方式では同じ回数になる)。
        var source = new StringBuilder("[裏3] 稀\n");
        for (int i = 0; i < 40; i++) source.Append("普通").Append(i).Append('\n');
        var tips = TransitionTips.FromText(source.ToString(), new System.Random(99));
        var context = new Context(Destination.Title);
        const int trials = 40000;
        int rare = 0;
        for (int i = 0; i < trials; i++)
            if (tips.Select(context).Text == "稀") rare++;
        double rareShare = (double)rare / trials;
        double normalShare = (1 - rareShare) / 40;
        Assert.That(rareShare / normalShare, Is.InRange(.15, .45));
    }

    [Test]
    public void SecretWelcomeRowsShareTheSongPool()
    {
        double welcome = FirstDrawShare("一般1\n一般2\n一般3\n一般4\n一般5\n[裏で] ようこそ",
            "ようこそ", new Context(Destination.Play, "Morning", true), 314);
        Assert.That(welcome, Is.InRange(.47, .53));
    }

    [Test]
    public void SelectionBoostAndSongPoolUseTheirSpecifiedProbabilities()
    {
        double hint = FirstDrawShare("普通\n[裏1] ヒント", "ヒント",
            new Context(Destination.SongSelect), 217);
        Assert.That(hint, Is.InRange(.64, .69));

        double song = FirstDrawShare(
            "一般1\n一般2\n一般3\n一般4\n一般5\n[曲:Morning] 曲専用",
            "曲専用", new Context(Destination.Play, "Morning"), 791);
        Assert.That(song, Is.InRange(.47, .53));
    }

    static double FirstDrawShare(string source, string wanted, Context context, int seed)
    {
        const int trials = 10000;
        var tips = TransitionTips.FromText(source, new System.Random(seed));
        int count = 0;
        for (int i = 0; i < trials; i++)
        {
            tips.ClearSeen();
            if (tips.Select(context).Text == wanted) count++;
        }
        return (double)count / trials;
    }

    [Test]
    public void BundledTipsAreUtf8WithoutBomValidUniqueAndReferenceRealSongs()
    {
        string path = Path.Combine(Application.streamingAssetsPath, "Tips", "tips_ja.txt");
        Assert.True(File.Exists(path), path);
        byte[] bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "UTF-8 の BOM を付けない");
        var tips = TransitionTips.FromText(new UTF8Encoding(false, true).GetString(bytes));
        Assert.Greater(tips.Entries.Count, 0);
        LogAssert.NoUnexpectedReceived();

        string songsRoot = Path.Combine(Application.streamingAssetsPath, "Songs");
        var songIds = new HashSet<string>(
            Directory.EnumerateDirectories(songsRoot).Select(p => Path.GetFileName(p)), StringComparer.Ordinal);
        var bodies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in tips.Entries)
        {
            Assert.True(bodies.Add(entry.Text), "同じ本文が重複している: " + entry.Text);
            Assert.LessOrEqual(TransitionTips.DisplayLength(entry.Text), 28f, entry.Text);
            foreach (string song in entry.SongIds)
            {
                Assert.True(songIds.Contains(song), "曲フォルダがない: " + song);
                if (entry.HintLevel > 0)
                    Assert.NotNull(RushESecretState.For(song), "裏譜面のない曲に裏ヒントがある: " + song);
            }
        }
        LogAssert.NoUnexpectedReceived();
    }
}
