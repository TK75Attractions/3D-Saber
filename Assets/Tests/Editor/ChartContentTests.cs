using System.Collections.Generic;
using NUnit.Framework;

// StreamingAssets の実譜面(ElDorado)の健全性チェック。
// 自動生成パイプラインの出力が壊れたらここで気付く。しきい値は再生成に耐えるよう緩めに。
public class ChartContentTests
{
    static readonly string[] Difficulties = { "easy", "normal", "hard" };

    [Test]
    public void ElDorado_AllDifficulties_LoadAndAreSane()
    {
        foreach (string diff in Difficulties)
        {
            ChartData chart = ChartLoader.LoadFromStreamingAssets("ElDorado", diff);
            Assert.IsNotNull(chart, diff + " が読めるはず");
            Assert.Greater(chart.bpm, 60f, diff + ": bpm");
            Assert.Greater(chart.notes.Count, 80, diff + ": ノーツ数");

            var valid = new HashSet<string> { "red", "blue", "gold", "default" };
            float prev = float.MinValue;
            foreach (NoteData n in chart.notes)
            {
                Assert.GreaterOrEqual(n.time, prev - 0.001f, diff + ": 時刻昇順");
                prev = n.time;
                Assert.IsTrue(valid.Contains(n.color), diff + ": 色 " + n.color);
                Assert.IsTrue(n.x >= -2.5f && n.x <= 2.5f, diff + ": x範囲 " + n.x);
                Assert.IsTrue(n.y >= -1.1f && n.y <= 1.2f, diff + ": y範囲 " + n.y);
                if (n.IsLong) Assert.IsTrue(n.count >= 2 && n.count <= 6, diff + ": longカウント");
                else Assert.AreEqual(1, n.count, diff + ": tapカウント");
            }
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

    [Test]
    public void ElDorado_Hard_HasMoreNotesThanNormal_ThanEasy()
    {
        int easy = ChartLoader.LoadFromStreamingAssets("ElDorado", "easy").notes.Count;
        int normal = ChartLoader.LoadFromStreamingAssets("ElDorado", "normal").notes.Count;
        int hard = ChartLoader.LoadFromStreamingAssets("ElDorado", "hard").notes.Count;
        Assert.Greater(normal, easy, "normal > easy");
        Assert.Greater(hard, normal, "hard > normal");
    }
}
