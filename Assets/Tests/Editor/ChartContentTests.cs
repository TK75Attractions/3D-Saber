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
                // 範囲は譜面エディターの配置可能域(X ±2.5 / Y ±1.5)+ 誤差マージン。
                // ユーザーがエディターで端に置いた譜面も正当なデータとして通す。
                Assert.IsTrue(n.x >= -2.6f && n.x <= 2.6f, diff + ": x範囲 " + n.x);
                Assert.IsTrue(n.y >= -1.6f && n.y <= 1.6f, diff + ": y範囲 " + n.y);
                // long のカット回数はエディター/Normalize の許容範囲(2..99)に合わせる
                // (エディターのスライダーは 2..20。生成譜面の 2..5 はコンテンツの性質でありテストしない)
                if (n.IsLong) Assert.IsTrue(n.count >= 2 && n.count <= 99, diff + ": longカウント " + n.count);
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

    // 注: 「hard > normal > easy」の物量比較テストは撤去した(2026-07-13)。
    // ユーザーが譜面エディターで編集するコンテンツの相対量は不変条件ではない。
}
