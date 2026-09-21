using NUnit.Framework;
using UnityEngine;

public class BleBridgeAutoStartPolicyTests
{
    [Test]
    public void StatusParser_PreservesPhysicalSaberSide()
    {
        Assert.IsTrue(BleBridgeStatusParser.TryParse(
            "STATE:BLE:RIGHT:NOTIFICATIONS_ACTIVE:XIAO-SABER-R",
            out BleBridgeStatusUpdate status));
        Assert.AreEqual(SaberSide.Right, status.Side);
        Assert.AreEqual(BleBridgeConnectionState.NotificationsActive, status.State);
        Assert.AreEqual("XIAO-SABER-R", status.DeviceName);
    }
    [Test]
    public void DefaultOffNeverStarts()
    {
        Assert.IsFalse(BleBridgeAutoStartPolicy.CanStart(
            false,
            true,
            BleBridgeAutoStartPolicy.GameScenePath));
    }

    [TestCase("")]
    [TestCase("Assets/InitTestScene123.unity")]
    [TestCase("Assets/Scenes/InputTest.unity")]
    [TestCase("Assets/Scenes/SampleScene.unity")]
    public void NonGameFlowAndTemporaryScenesNeverStart(string scenePath)
    {
        Assert.IsFalse(BleBridgeAutoStartPolicy.CanStart(true, true, scenePath));
    }

    [Test]
    public void ExplicitOptInStartsInEveryGameFlowSceneOnlyWhilePlaying()
    {
        foreach (string path in new[] {
                     BleBridgeAutoStartPolicy.TitleScenePath,
                     BleBridgeAutoStartPolicy.SongSelectScenePath,
                     BleBridgeAutoStartPolicy.GameScenePath,
                     BleBridgeAutoStartPolicy.ResultScenePath })
            Assert.IsTrue(BleBridgeAutoStartPolicy.CanStart(true, true, path), path);
        Assert.IsFalse(BleBridgeAutoStartPolicy.CanStart(
            true,
            false,
            BleBridgeAutoStartPolicy.GameScenePath));
    }

    [Test]
    public void GenericBleStatusIsDistinctFromProcessReady()
    {
        Assert.IsTrue(BleBridgeStatusParser.TryParse(
            "STATE:BRIDGE_READY",
            out BleBridgeStatusUpdate ready));
        Assert.IsTrue(ready.IsBridgeReady);

        Assert.IsTrue(BleBridgeStatusParser.TryParse(
            "STATE:BLE:NOTIFICATIONS_ACTIVE:XIAO-LSM6DSV16X",
            out BleBridgeStatusUpdate active));
        Assert.IsFalse(active.IsBridgeReady);
        Assert.AreEqual(
            BleBridgeConnectionState.NotificationsActive,
            active.State);
    }

    [Test]
    public void PersistentAutoStartSettingsAssetExists()
    {
        Assert.IsNotNull(
            Resources.Load<ImuBleServiceSettings>("ImuBleServiceSettings"),
            "Title/Menu起動時に読むBLE共通設定assetが必要");
    }
}
