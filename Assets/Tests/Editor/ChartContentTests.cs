using System.Collections.Generic;
using NUnit.Framework;

// StreamingAssets の実譜面の健全性チェック。
// 曲一覧に出る全曲×全難易度を走査するので、曲を追加すると自動でカバーされる。
// しきい値は再生成・手編集に耐えるよう構造的な性質(読める・昇順・型/範囲)だけに絞る。
public class ChartContentTests
{
    static readonly string[] Difficulties = { "easy", "normal", "hard" };

    static void AssertChartIsSane(string songId, string diff, ChartData chart)
    {
        string label = songId + "/" + diff;
        Assert.IsNotNull(chart, label + " が読めるはず");
        Assert.Greater(chart.bpm, 30f, label + ": bpm");

        var valid = new HashSet<string> { "red", "blue", "gold", "default" };
        float prev = float.MinValue;
        foreach (NoteData n in chart.notes)
        {
            Assert.GreaterOrEqual(n.time, prev - 0.001f, label + ": 時刻昇順");
            prev = n.time;
            Assert.IsTrue(valid.Contains(n.color), label + ": 色 " + n.color);
            // 範囲は譜面エディターの配置可能域(X ±2.5 / Y ±1.5)+ 誤差マージン
            Assert.IsTrue(n.x >= -2.6f && n.x <= 2.6f, label + ": x範囲 " + n.x);
            Assert.IsTrue(n.y >= -1.6f && n.y <= 1.6f, label + ": y範囲 " + n.y);
            // long のカット回数はエディター/Normalize の許容範囲(2..99)
            if (n.IsLong) Assert.IsTrue(n.count >= 2 && n.count <= 99, label + ": longカウント " + n.count);
            else Assert.AreEqual(1, n.count, label + ": tapカウント");
        }
    }

    [Test]
    public void AllPlayableSongs_AllDifficulties_AreSane()
    {
        var songs = SongSelectController.EnumerateSongIds();
        Assert.Greater(songs.Count, 0, "プレイアブル曲が存在する");
        foreach (string songId in songs)
        {
            foreach (string diff in Difficulties)
            {
                AssertChartIsSane(songId, diff, ChartLoader.LoadFromStreamingAssets(songId, diff));
            }
        }
    }

    [Test]
    public void ElDorado_AllDifficulties_LoadAndHaveSubstance()
    {
        foreach (string diff in Difficulties)
        {
            ChartData chart = ChartLoader.LoadFromStreamingAssets("ElDorado", diff);
            AssertChartIsSane("ElDorado", diff, chart);
            Assert.Greater(chart.notes.Count, 80, diff + ": ノーツ数");
        }
    }

    [Test]
    public void ElDorado_HasGoldAtChorus_AndSpecialNotes()
    {
        ChartData chart = ChartLoader.LoadFromStreamingAssets("ElDorado", "normal");
        int gold = 0, longs = 0, dirs = 0;
        foreach (NoteData n in chart.notes)
        {
            if (n.color == "gold") gold++;
            if (n.IsLong) longs++;
            if (n.IsDirection) dirs++;
        }
        Assert.GreaterOrEqual(gold, 1, "サビ頭の金ノーツ");
        Assert.GreaterOrEqual(longs, 1, "ロングノーツ");
        Assert.GreaterOrEqual(dirs, 1, "方向ノーツ");
    }

    // 注: 「hard > normal > easy」の物量比較テストは撤去した(2026-07-13)。
    // ユーザーが譜面エディターで編集するコンテンツの相対量は不変条件ではない。
}
