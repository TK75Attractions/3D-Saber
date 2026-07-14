using NUnit.Framework;

// 曲選択ホイール(SongWheelView)の行配置純関数の検証。
public class SongWheelViewTests
{
    [Test]
    public void RowScale_ShrinksWithDistance_AndClamps()
    {
        Assert.AreEqual(1f, SongWheelView.RowScale(0f), 1e-4f, "中央は等倍");
        Assert.AreEqual(0.93f, SongWheelView.RowScale(1f), 1e-4f, "1行で0.07縮む");
        Assert.AreEqual(0.72f, SongWheelView.RowScale(5f), 1e-4f, "下限0.72");
    }

    [Test]
    public void RowAlpha_FadesWithDistance_AndHidesBeyondThree()
    {
        Assert.AreEqual(1f, SongWheelView.RowAlpha(0f), 1e-4f, "中央は不透明");
        Assert.AreEqual(0.57f, SongWheelView.RowAlpha(1f), 1e-4f, "隣接は0.57");
        Assert.AreEqual(0.39f, SongWheelView.RowAlpha(2f), 1e-4f);
        Assert.AreEqual(0f, SongWheelView.RowAlpha(3.5f), 1e-4f, "3行超は非表示");
    }

    [Test]
    public void RowAlpha_IsContinuousBetweenCenterAndNeighbor()
    {
        // アニメ中(0<off<1)も値が飛ばない
        float nearCenter = SongWheelView.RowAlpha(0.01f);
        float nearOne = SongWheelView.RowAlpha(0.99f);
        Assert.Greater(nearCenter, 0.99f);
        Assert.AreEqual(0.57f, nearOne, 0.01f);
    }

    [Test]
    public void FormatLevel_ZeroPadsAndShowsDashesForMissing()
    {
        Assert.AreEqual("06", SongWheelView.FormatLevel(6));
        Assert.AreEqual("10", SongWheelView.FormatLevel(10));
        Assert.AreEqual("--", SongWheelView.FormatLevel(0), "譜面なし");
        Assert.AreEqual("--", SongWheelView.FormatLevel(-1));
    }
}
