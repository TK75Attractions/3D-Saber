using NUnit.Framework;

// PlayMode テストアセンブリ全体の既定: PhoneSaber P2P bridge を自動起動しない。
// InputPoint が UDP 受信を始めても本物の bridge(初回は swiftc build)を起動させないため。
[SetUpFixture]
public sealed class PhoneSaberP2PBridgePlayModeTestDefaults
{
    bool saved;

    [OneTimeSetUp]
    public void DisableBridgeAutoStart()
    {
        saved = PhoneSaberP2PBridgeProcess.AutoStartEnabled;
        PhoneSaberP2PBridgeProcess.AutoStartEnabled = false;
    }

    [OneTimeTearDown]
    public void RestoreBridgeAutoStart()
    {
        PhoneSaberP2PBridgeProcess.AutoStartEnabled = saved;
    }
}
