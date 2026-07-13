using NUnit.Framework;
using UnityEngine;

// ハイスコアストア(挿入ロジックとPlayerPrefs永続化)の検証。
public class HighScoreStoreTests
{
    private const string TestSong = "__test_song__";
    private const string TestDiff = "normal";

    [TearDown]
    public void Cleanup()
    {
        HighScoreStore.Clear(TestSong, TestDiff);
    }

    private static HighScoreEntry Entry(int score)
    {
        return new HighScoreEntry { score = score, rank = "A", accuracy = 0.85f, date = "2026/07/13" };
    }

    // ---- Insert(純粋ロジック) ----

    [Test]
    public void Insert_OrdersDescending_AndReturnsIndex()
    {
        var table = new HighScoreTable();
        Assert.AreEqual(0, HighScoreStore.Insert(table, Entry(100), 5), "空なら先頭");
        Assert.AreEqual(0, HighScoreStore.Insert(table, Entry(300), 5), "最高点は先頭へ");
        Assert.AreEqual(1, HighScoreStore.Insert(table, Entry(200), 5), "間に入る");
        Assert.AreEqual(300, table.entries[0].score);
        Assert.AreEqual(200, table.entries[1].score);
        Assert.AreEqual(100, table.entries[2].score);
    }

    [Test]
    public void Insert_Tie_KeepsExistingAbove()
    {
        var table = new HighScoreTable();
        HighScoreStore.Insert(table, Entry(200), 5);
        int index = HighScoreStore.Insert(table, Entry(200), 5);
        Assert.AreEqual(1, index, "同点は先客が上位");
    }

    [Test]
    public void Insert_TrimsToMax_AndRejectsBelowCutoff()
    {
        var table = new HighScoreTable();
        for (int i = 0; i < 5; i++) HighScoreStore.Insert(table, Entry(1000 - i * 100), 5); // 1000..600

        Assert.AreEqual(-1, HighScoreStore.Insert(table, Entry(500), 5), "圏外は -1");
        Assert.AreEqual(5, table.entries.Count, "圏外挿入では変化しない");

        Assert.AreEqual(0, HighScoreStore.Insert(table, Entry(2000), 5), "首位更新");
        Assert.AreEqual(5, table.entries.Count, "上限維持");
        Assert.AreEqual(700, table.entries[4].score, "最下位(600)が押し出される");
    }

    // ---- 永続化 ----

    [Test]
    public void RecordAndLoad_RoundTripsThroughPlayerPrefs()
    {
        HighScoreStore.Clear(TestSong, TestDiff);

        int first = HighScoreStore.Record(TestSong, TestDiff, Entry(1200), out HighScoreTable t1);
        Assert.AreEqual(0, first);
        Assert.AreEqual(1, t1.entries.Count);

        int second = HighScoreStore.Record(TestSong, TestDiff, Entry(900), out HighScoreTable t2);
        Assert.AreEqual(1, second);

        HighScoreTable loaded = HighScoreStore.Load(TestSong, TestDiff);
        Assert.AreEqual(2, loaded.entries.Count);
        Assert.AreEqual(1200, loaded.entries[0].score);
        Assert.AreEqual(900, loaded.entries[1].score);
        Assert.AreEqual("A", loaded.entries[0].rank);
    }

    [Test]
    public void Load_BrokenJson_ReturnsEmptyTable()
    {
        PlayerPrefs.SetString(HighScoreStore.Key(TestSong, TestDiff), "{ broken");
        HighScoreTable table = HighScoreStore.Load(TestSong, TestDiff);
        Assert.IsNotNull(table);
        Assert.AreEqual(0, table.entries.Count);
    }

    [Test]
    public void Key_NormalizesDifficulty()
    {
        Assert.AreEqual(HighScoreStore.Key("Song", "Normal"), HighScoreStore.Key("Song", "normal"));
        Assert.AreEqual("hiscore_Song_normal", HighScoreStore.Key("Song", null), "難易度未指定は normal");
    }

    // ---- ランクラベルの復元(表示色に使う) ----

    [Test]
    public void PlayRank_FromLabel_RoundTrips()
    {
        foreach (PlayRank rank in System.Enum.GetValues(typeof(PlayRank)))
        {
            Assert.AreEqual(rank, PlayRankHelper.FromLabel(PlayRankHelper.Label(rank)));
        }
        Assert.AreEqual(PlayRank.C, PlayRankHelper.FromLabel("??"), "不明ラベルは C");
    }
}
