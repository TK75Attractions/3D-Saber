using NUnit.Framework;

// 着地ゴーストの「薄く大きく現れて、たたく瞬間にノーツとピッタリ同サイズへ収束する」純関数の検証。
public class NoteTimingCueGhostScaleTests
{
    [Test]
    public void GhostScale_AppearsLarge()
    {
        // 出現の瞬間(残り時間 = 可視秒数)は startScale 倍
        Assert.AreEqual(2.4f, NoteTimingCue.ComputeGhostScale(0.7, 0.7f, 2.4f), 1e-3f);
    }

    [Test]
    public void GhostScale_ConvergesToExactSizeAtHitTime()
    {
        Assert.AreEqual(1f, NoteTimingCue.ComputeGhostScale(0.0, 0.7f, 2.4f), 1e-3f, "HitTime ちょうどで等倍");
        Assert.AreEqual(1f, NoteTimingCue.ComputeGhostScale(-0.2, 0.7f, 2.4f), 1e-3f, "過ぎても等倍を維持");
    }

    [Test]
    public void GhostScale_ShrinksLinearly()
    {
        // 線形収縮: 残り半分で startScale と 1.0 の中間
        float mid = NoteTimingCue.ComputeGhostScale(0.35, 0.7f, 2.4f);
        Assert.AreEqual((2.4f + 1f) * 0.5f, mid, 1e-3f);
        Assert.Greater(NoteTimingCue.ComputeGhostScale(0.6, 0.7f, 2.4f),
                       NoteTimingCue.ComputeGhostScale(0.3, 0.7f, 2.4f), "近づくほど小さい");
    }

    [Test]
    public void GhostScale_ZeroWindowIsSafe()
    {
        Assert.AreEqual(1f, NoteTimingCue.ComputeGhostScale(0.5, 0f, 2.4f), 1e-3f);
    }
}
