using NUnit.Framework;

// 難易度レーティング(1〜10)の検証。
public class ChartDifficultyRaterTests
{
    private static ChartData MakeChart(int count, float intervalMs, string type = "tap", int longCount = 1)
    {
        var chart = new ChartData { bpm = 120f };
        for (int i = 0; i < count; i++)
        {
            chart.notes.Add(new NoteData
            {
                time = 1000f + i * intervalMs,
                x = (i % 2 == 0) ? -1.5f : 1.5f,
                y = 0f,
                type = type,
                count = type == "long" ? longCount : 1,
            });
        }
        return chart;
    }

    [Test]
    public void Rate_EmptyOrNull_IsZero()
    {
        Assert.AreEqual(0, ChartDifficultyRater.Rate(null));
        Assert.AreEqual(0, ChartDifficultyRater.Rate(new ChartData()));
    }

    [Test]
    public void Rate_SparseChart_IsLow()
    {
        // 60秒に30ノーツ(0.5nps)のゆったり譜面
        int level = ChartDifficultyRater.Rate(MakeChart(30, 2000f));
        Assert.That(level, Is.InRange(1, 3), "スカスカ譜面は低難度: " + level);
    }

    [Test]
    public void Rate_DenseChart_IsHigh()
    {
        // 8分連打相当(150ms間隔)が60秒続く高密度譜面
        int level = ChartDifficultyRater.Rate(MakeChart(400, 150f));
        Assert.That(level, Is.InRange(8, 10), "高密度譜面は高難度: " + level);
    }

    [Test]
    public void Rate_MoreDensity_NeverLowersLevel()
    {
        int sparse = ChartDifficultyRater.Rate(MakeChart(60, 1000f));
        int mid = ChartDifficultyRater.Rate(MakeChart(120, 500f));
        int dense = ChartDifficultyRater.Rate(MakeChart(240, 250f));
        Assert.LessOrEqual(sparse, mid);
        Assert.LessOrEqual(mid, dense);
    }

    [Test]
    public void Rate_SimultaneousNotes_RaiseLevel()
    {
        var plain = MakeChart(100, 600f);
        var withDoubles = MakeChart(100, 600f);
        // 4個に1個を同時押し化(同時刻にもう1ノーツ)
        for (int i = 0; i < 100; i += 4)
        {
            withDoubles.notes.Add(new NoteData { time = 1000f + i * 600f, x = 2.2f, y = 0.5f, type = "tap" });
        }
        Assert.Greater(ChartDifficultyRater.Rate(withDoubles), ChartDifficultyRater.Rate(plain),
            "同時押しがあるほど難しい");
    }

    [Test]
    public void Rate_ElDoradoCharts_AreSaneAndOrdered()
    {
        int easy = ChartDifficultyRater.Rate(ChartLoader.LoadFromStreamingAssets("ElDorado", "easy"));
        int normal = ChartDifficultyRater.Rate(ChartLoader.LoadFromStreamingAssets("ElDorado", "normal"));
        int hard = ChartDifficultyRater.Rate(ChartLoader.LoadFromStreamingAssets("ElDorado", "hard"));

        Assert.That(easy, Is.InRange(1, 10));
        Assert.That(normal, Is.InRange(1, 10));
        Assert.That(hard, Is.InRange(1, 10));
        Assert.Less(easy, hard, "easy < hard の関係は維持される");
        Assert.That(easy, Is.InRange(2, 4), "easyは低〜中の下: " + easy);
        Assert.That(hard, Is.InRange(7, 10), "hardは高難度帯: " + hard);
    }

    [Test]
    public void Rate_EmptySongChart_IsZero_ForNewSong()
    {
        // 揺籠は譜面未制作(空チャート) → 0 = 数値非表示
        ChartData chart = ChartLoader.LoadFromStreamingAssets("揺籠", "normal");
        Assert.IsNotNull(chart, "空でも chart.json は読める");
        Assert.AreEqual(0, ChartDifficultyRater.Rate(chart));
    }
}
