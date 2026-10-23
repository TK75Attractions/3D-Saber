using NUnit.Framework;
using System.Reflection;
using UnityEngine.InputSystem;

public class PhoneSaberOperatorKeysTests
{
    [Test]
    public void QueueingDefaultsResetsBothOperatorToggles()
    {
        var type = typeof(PhoneSaberOperatorKeys);
        var trackLock = type.GetField("lastSentTrackLock", BindingFlags.NonPublic | BindingFlags.Static);
        var exposure = type.GetField("exposureIndex", BindingFlags.NonPublic | BindingFlags.Static);
        object savedTrackLock = trackLock.GetValue(null), savedExposure = exposure.GetValue(null);
        try
        {
            trackLock.SetValue(null, false);
            exposure.SetValue(null, 0);
            PhoneSaberDiscoveryResponder.QueueCommand(PhoneSaberDiscoveryResponder.DefaultsCommand);
            Assert.AreEqual(true, trackLock.GetValue(null));
            Assert.AreEqual(2, exposure.GetValue(null));
            Assert.AreEqual("track-lock=0", PhoneSaberOperatorKeys.CommandFor(Key.T, (int)exposure.GetValue(null),
                out _, (bool)trackLock.GetValue(null)).Value.command);
            Assert.AreEqual("exposure-bias=0", PhoneSaberOperatorKeys.CommandFor(Key.E, (int)exposure.GetValue(null), out _).Value.command);
        }
        finally
        {
            trackLock.SetValue(null, savedTrackLock);
            exposure.SetValue(null, savedExposure);
        }
    }

    [Test]
    public void ShutterCyclesAndDefaultsResetTo2000ThenNextPressIsOff()
    {
        int index = 0;
        foreach (int value in new[] { 500, 1000, 2000, 0, 500 })
        {
            Assert.AreEqual("shutter=" + value, PhoneSaberOperatorKeys.CommandFor(Key.S, 2, out int ev, true, index).Value.command);
            Assert.AreEqual(2, ev);
            Assert.AreEqual("shutter=" + value, PhoneSaberOperatorKeys.ShutterCommand(index, out index).command);
        }
        var field = typeof(PhoneSaberOperatorKeys).GetField("shutterIndex", BindingFlags.NonPublic | BindingFlags.Static);
        object saved = field.GetValue(null);
        try
        {
            field.SetValue(null, 0);
            PhoneSaberDiscoveryResponder.QueueCommand(PhoneSaberDiscoveryResponder.DefaultsCommand);
            Assert.AreEqual(3, field.GetValue(null));
            Assert.AreEqual("shutter=0", PhoneSaberOperatorKeys.CommandFor(Key.S, 2, out _).Value.command);
            Assert.AreEqual("shutter=0", PhoneSaberOperatorKeys.CommandFor(Key.S, 2, out _, true, (int)field.GetValue(null)).Value.command);
            StringAssert.Contains("1/2000", PhoneSaberOperatorKeys.PendingLabel(PhoneSaberDiscoveryResponder.DefaultsCommand));
            PhoneSaberOperatorKeys.SelectShutter(1000);
            Assert.AreEqual(2, field.GetValue(null));
        }
        finally { field.SetValue(null, saved); }
    }

    [Test]
    public void ISOCyclesAndDefaultsResetToProduction()
    {
        int index = 0;
        foreach (int value in new[] { 250, 200, 160, 125, 100, 80, 64, 2000, 250 })
        {
            Assert.AreEqual("shutter-iso=" + value, PhoneSaberOperatorKeys.CommandFor(Key.D, 2, out int ev, true, 3, index).Value.command);
            Assert.AreEqual(2, ev);
            Assert.AreEqual("shutter-iso=" + value, PhoneSaberOperatorKeys.ShutterISOCommand(index, out index).command);
        }
        var field = typeof(PhoneSaberOperatorKeys).GetField("shutterISOIndex", BindingFlags.NonPublic | BindingFlags.Static);
        object saved = field.GetValue(null);
        try
        {
            PhoneSaberOperatorKeys.SelectShutterISO(250);
            Assert.AreEqual(1, field.GetValue(null));
            PhoneSaberDiscoveryResponder.QueueCommand(PhoneSaberDiscoveryResponder.DefaultsCommand);
            Assert.AreEqual(0, field.GetValue(null));
            Assert.AreEqual("shutter-iso=250", PhoneSaberOperatorKeys.CommandFor(Key.D, 2, out _).Value.command);
        }
        finally { field.SetValue(null, saved); }
    }

    [Test]
    public void KeysMapToPhoneCommands()
    {
        Assert.AreEqual("ignore=red", PhoneSaberOperatorKeys.CommandFor(Key.R, 0, out _).Value.command);
        Assert.AreEqual("ignore=blue", PhoneSaberOperatorKeys.CommandFor(Key.B, 0, out _).Value.command);
        Assert.AreEqual("ignore=both", PhoneSaberOperatorKeys.CommandFor(Key.I, 0, out _).Value.command);
        Assert.AreEqual("clear-ignore=1", PhoneSaberOperatorKeys.CommandFor(Key.C, 0, out _).Value.command);
        Assert.AreEqual("clip=1", PhoneSaberOperatorKeys.CommandFor(Key.K, 0, out _).Value.command);
        Assert.AreEqual(PhoneSaberDiscoveryResponder.LearnAreaCommand, PhoneSaberOperatorKeys.CommandFor(Key.P, 0, out _).Value.command);
        Assert.AreEqual(PhoneSaberDiscoveryResponder.ClearAreaCommand, PhoneSaberOperatorKeys.CommandFor(Key.X, 0, out _).Value.command);
        // ゲームが使うキーは割り当てない。
        foreach (var key in new[] { Key.A, Key.W, Key.N, Key.Digit1, Key.Space, Key.Enter, Key.Escape })
            Assert.IsNull(PhoneSaberOperatorKeys.CommandFor(key, 0, out _));
    }

    [Test]
    public void ExposureKeyStartsAtMinusTwoThenCyclesZeroMinusOneMinusTwo()
    {
        int index = 2;
        Assert.AreEqual("exposure-bias=0", PhoneSaberOperatorKeys.CommandFor(Key.E, index, out index).Value.command);
        Assert.AreEqual("exposure-bias=-1", PhoneSaberOperatorKeys.CommandFor(Key.E, index, out index).Value.command);
        Assert.AreEqual("exposure-bias=-2", PhoneSaberOperatorKeys.CommandFor(Key.E, index, out index).Value.command);
        Assert.AreEqual(2, index);
    }
    [Test]
    public void TrackLockKeyAlternatesFromOnWithoutChangingExposure()
    {
        Assert.AreEqual("track-lock=0", PhoneSaberOperatorKeys.CommandFor(Key.T, 2, out _).Value.command);
        bool lastSent = true;
        foreach (bool enabled in new[] { false, true, false, true })
        {
            var action = PhoneSaberOperatorKeys.CommandFor(Key.T, 2, out int exposure, lastSent).Value;
            Assert.AreEqual("track-lock=" + (enabled ? "1" : "0"), action.command);
            Assert.AreEqual("スマホ: 剣を追いかける " + (enabled ? "ON" : "OFF"), action.label);
            Assert.AreEqual(2, exposure);
            lastSent = enabled;
        }
        Assert.AreEqual("ignore=red", PhoneSaberOperatorKeys.CommandFor(Key.R, 0, out _, true).Value.command);
    }

    [TestCase("A")]
    [TestCase("B")]
    public void NextOperatorKeysFollowCurrentPhoneControls(string station)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var type = typeof(PhoneSaberOperatorKeys);
        string[] names = { "lastSentTrackLock", "exposureIndex", "shutterIndex", "shutterISOIndex", "lastPhoneState" };
        var saved = System.Array.ConvertAll(names, name => type.GetField(name, flags).GetValue(null));
        try
        {
            PhoneSaberOperatorKeys.ResetDefaultsState();
            var phone = new PhoneSaberMetrics { Station = station, Platform = "ios", Controls = "0022", ReceivedAt = 20 };
            Assert.IsTrue(PhoneSaberOperatorKeys.FollowPhoneState(phone, station, 21, 10));
            foreach (var key in new[] { Key.T, Key.E, Key.S, Key.D })
            {
                var action = PhoneSaberOperatorKeys.CommandFor(key,
                    (int)type.GetField("exposureIndex", flags).GetValue(null), out _,
                    (bool)type.GetField("lastSentTrackLock", flags).GetValue(null),
                    (int)type.GetField("shutterIndex", flags).GetValue(null),
                    (int)type.GetField("shutterISOIndex", flags).GetValue(null));
                string expected = key == Key.T ? "track-lock=1" : key == Key.E ? "exposure-bias=-1" :
                    key == Key.S ? "shutter=2000" : "shutter-iso=160";
                Assert.AreEqual(expected, action.Value.command);
            }
            Assert.IsFalse(PhoneSaberOperatorKeys.FollowPhoneState(phone, station, 22, 10), "同じ計測で再適用しない");
            foreach (var invalid in new[] {
                new PhoneSaberMetrics { Station = station == "A" ? "B" : "A", Platform = "ios", Controls = "1230", ReceivedAt = 22 },
                new PhoneSaberMetrics { Station = station, Platform = "android", Controls = "1230", ReceivedAt = 22 },
                new PhoneSaberMetrics { Station = station, Platform = "ios", Controls = "1230", ReceivedAt = 1 },
                new PhoneSaberMetrics { Station = station, Platform = "ios", Controls = "1230", ReceivedAt = 30 },
                new PhoneSaberMetrics { Station = station, Platform = "ios", Controls = "1230", ReceivedAt = 10 },
                new PhoneSaberMetrics { Station = station, Platform = "ios", ReceivedAt = 22 } })
                Assert.IsFalse(PhoneSaberOperatorKeys.FollowPhoneState(invalid, station, 22, 10));
        }
        finally { for (int i = 0; i < names.Length; i++) type.GetField(names[i], flags).SetValue(null, saved[i]); }
    }

    [Test]
    public void MetricsControlsAreOptionalAndValidated()
    {
        var path = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "PhoneSaber/tools/fixtures/protocol_metrics.tsv");
        string original = System.IO.File.ReadAllLines(path)[1].Split('\t')[1];
        bool Parse(string suffix, out PhoneSaberMetrics m) {
            var bytes = System.Text.Encoding.ASCII.GetBytes(original + suffix);
            return PhoneSaberMetrics.TryParse(bytes, bytes.Length, out m);
        }
        Assert.IsTrue(Parse("", out var old)); Assert.IsNull(old.Controls);
        Assert.IsTrue(Parse(" ctl=0022", out var current)); Assert.AreEqual("0022", current.Controls);
        for (int index = 0; index < 8; index++)
        {
            Assert.IsTrue(Parse(" ctl=123" + index, out var iso));
            Assert.AreEqual("123" + index, iso.Controls);
        }
        foreach (string invalid in new[] { " ctl=002", " ctl=00220", " ctl=2022", " ctl=0322", " ctl=0042", " ctl=0028", " ctl=002x", " ctl=0022 ctl=1230" })
            Assert.IsFalse(Parse(invalid, out _), invalid);
    }

}
