using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public class PhoneSaberIgnoreZonesTests
{
    [Test]
    public void ZoneSurroundsTheObjectAndOnlyDropsSamplesFullyInside()
    {
        // 左下の赤いコーン（カメラ画像の 0..1 座標）。
        Rect zone = PhoneSaberIgnoreZones.ZoneFor(new Vector2(.10f, .12f), new Vector2(.12f, .20f));
        var zones = new List<Rect> { zone };
        Assert.IsTrue(PhoneSaberIgnoreZones.Covers(zones, new Vector2(.11f, .13f), new Vector2(.12f, .19f)));
        // 剣が範囲を横切っても、反対の端が外なら捨てない。
        Assert.IsFalse(PhoneSaberIgnoreZones.Covers(zones, new Vector2(.11f, .15f), new Vector2(.50f, .60f)));
        Assert.IsFalse(PhoneSaberIgnoreZones.Covers(zones, new Vector2(.6f, .6f), new Vector2(.7f, .7f)));
        Assert.IsFalse(PhoneSaberIgnoreZones.Covers(null, Vector2.zero, Vector2.zero));
        // 点のような物でも最小の大きさを持つ。
        Rect tiny = PhoneSaberIgnoreZones.ZoneFor(new Vector2(.5f, .5f), new Vector2(.5f, .5f));
        Assert.GreaterOrEqual(tiny.width, PhoneSaberIgnoreZones.MinimumSize - 1e-6f);
    }

    [Test]
    public void FormatParseRoundTripAndRejectsBrokenText()
    {
        var zones = new List<Rect> { new Rect(.1f, .2f, .3f, .4f), new Rect(.5f, .5f, .06f, .06f) };
        var parsed = PhoneSaberIgnoreZones.Parse(PhoneSaberIgnoreZones.Format(zones));
        Assert.AreEqual(2, parsed.Count);
        Assert.AreEqual(.3f, parsed[0].width, 1e-4f);
        Assert.AreEqual(0, PhoneSaberIgnoreZones.Parse("x;1,2;0,0,-1,1;").Count);
    }

    [Test]
    public void AddKeepsAtMostFourPerStationAndClearRemovesThem()
    {
        string key = PhoneSaberIgnoreZones.Key("ign-a");
        PlayerPrefs.DeleteKey(key);
        try
        {
            int revision = PhoneSaberIgnoreZones.Revision;
            for (int i = 0; i < 5; i++)
                Assert.IsTrue(PhoneSaberIgnoreZones.Add("ign-a", new Vector2(i * .1f, .1f), new Vector2(i * .1f + .02f, .15f)));
            var zones = PhoneSaberIgnoreZones.Load("ign-a");
            Assert.AreEqual(PhoneSaberIgnoreZones.MaximumZones, zones.Count);
            Assert.Greater(zones[0].center.x, .05f); // 一番古い範囲が消えた
            Assert.AreNotEqual(revision, PhoneSaberIgnoreZones.Revision);
            Assert.IsFalse(PhoneSaberIgnoreZones.Add("ign-a", new Vector2(float.NaN, 0), Vector2.zero));
            PhoneSaberIgnoreZones.Clear("ign-a");
            Assert.AreEqual(0, PhoneSaberIgnoreZones.Load("ign-a").Count);
        }
        finally { PlayerPrefs.DeleteKey(key); }
    }

    [Test]
    public void PointJumpGateDropsOneFarSample()
    {
        var gate = new PhoneSaberPointJumpGate();
        Vector2 right = new Vector2(1500, 200), left = new Vector2(150, 150);
        Assert.AreEqual(right, gate.Apply(1, right, 270));
        Assert.AreEqual(right, gate.Apply(2, left, 270));   // 1回目の跳びは捨てる
        Assert.AreEqual(right, gate.Apply(2, left, 270));   // 同じ受信は一度だけ数える
        Assert.AreEqual(right, gate.Apply(3, right + Vector2.one, 270) - Vector2.one); // 戻れば受け入れ
        Assert.AreEqual(right + Vector2.one, gate.Apply(4, left, 270));
        Assert.AreEqual(left, gate.Apply(5, left, 270));    // 2回続けば本当の移動
        gate.Reset();
        Assert.AreEqual(right, gate.Apply(7, right, 270));
    }
}
