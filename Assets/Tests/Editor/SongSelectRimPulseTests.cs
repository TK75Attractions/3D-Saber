using NUnit.Framework;

public class SongSelectRimPulseTests
{
    [Test]
    public void DifficultyPulsesOnceAndSongChangesCancelThePulse()
    {
        var pulse = new SongSelectRimPulse();
        pulse.Select(0, 0, false, 0);
        Assert.AreEqual(3, pulse.WidthAt(.07));
        pulse.Select(0, 1, false, 1);
        Assert.AreEqual(3, pulse.WidthAt(1), .001);
        Assert.AreEqual(7, pulse.WidthAt(1.07), .001);
        pulse.Select(0, 1, false, 1.1);
        Assert.AreEqual(5, pulse.WidthAt(1.145), .001, "同じ選択の再通知では再始動しない");
        Assert.AreEqual(3, pulse.WidthAt(1.22), .001);
        pulse.Select(0, 2, false, 2);
        pulse.Select(1, 0, false, 2.03);
        Assert.AreEqual(3, pulse.WidthAt(2.07), .001, "曲送り時の難易度補正でも弾ませない");
        pulse.Select(1, 2, true, 3);
        Assert.AreEqual(7, pulse.WidthAt(3.07), .001);
        pulse.Select(1, 2, true, 3.1);
        Assert.AreEqual(3, pulse.WidthAt(3.22), .001);
        for (int i = 0; i < 3; i++)
            Assert.AreEqual(SongSelectSkin.DifficultyColor(i), SongSelectRimPulse.ColorFor(i, false));
        Assert.AreEqual(RushESecretState.Purple, SongSelectRimPulse.ColorFor(2, true));
    }
}
