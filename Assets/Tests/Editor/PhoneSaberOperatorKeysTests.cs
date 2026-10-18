using NUnit.Framework;
using UnityEngine.InputSystem;

public class PhoneSaberOperatorKeysTests
{
    [Test]
    public void KeysMapToPhoneCommands()
    {
        Assert.AreEqual("ignore=red", PhoneSaberOperatorKeys.CommandFor(Key.R, 0, out _).Value.command);
        Assert.AreEqual("ignore=blue", PhoneSaberOperatorKeys.CommandFor(Key.B, 0, out _).Value.command);
        Assert.AreEqual("ignore=both", PhoneSaberOperatorKeys.CommandFor(Key.I, 0, out _).Value.command);
        Assert.AreEqual("clear-ignore=1", PhoneSaberOperatorKeys.CommandFor(Key.C, 0, out _).Value.command);
        Assert.AreEqual(PhoneSaberDiscoveryResponder.LearnAreaCommand, PhoneSaberOperatorKeys.CommandFor(Key.P, 0, out _).Value.command);
        Assert.AreEqual(PhoneSaberDiscoveryResponder.ClearAreaCommand, PhoneSaberOperatorKeys.CommandFor(Key.X, 0, out _).Value.command);
        // ゲームが使うキーは割り当てない。
        foreach (var key in new[] { Key.A, Key.S, Key.D, Key.W, Key.N, Key.Digit1, Key.Space, Key.Enter, Key.Escape })
            Assert.IsNull(PhoneSaberOperatorKeys.CommandFor(key, 0, out _));
    }

    [Test]
    public void ExposureKeyCyclesZeroMinusOneMinusTwo()
    {
        int index = 0;
        Assert.AreEqual("exposure-bias=-1", PhoneSaberOperatorKeys.CommandFor(Key.E, index, out index).Value.command);
        Assert.AreEqual("exposure-bias=-2", PhoneSaberOperatorKeys.CommandFor(Key.E, index, out index).Value.command);
        Assert.AreEqual("exposure-bias=0", PhoneSaberOperatorKeys.CommandFor(Key.E, index, out index).Value.command);
        Assert.AreEqual(0, index);
    }
}
