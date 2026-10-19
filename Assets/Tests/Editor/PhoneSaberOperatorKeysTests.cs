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
        foreach (var key in new[] { Key.A, Key.S, Key.D, Key.W, Key.N, Key.Digit1, Key.Space, Key.Enter, Key.Escape })
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

}
