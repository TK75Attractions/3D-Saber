using NUnit.Framework;

// PlayMode テストアセンブリ全体の既定: PhoneSaber P2P bridge を自動起動しない。
// InputPoint が UDP 受信を始めても本物の bridge(初回は swiftc build)を起動させないため。
// 台同士のランキング同期(RankingSync、UDP 5017)も止める(テストの回を本物の台へ送らないため)。
// Play に入ったとき(この SetUpFixture より先)に作られていても、開く前に切られたら何も送らずに消える。
[SetUpFixture]
public sealed class PhoneSaberP2PBridgePlayModeTestDefaults
{
    bool saved, savedRankingSync, savedClipRelay;

    [OneTimeSetUp]
    public void DisableBridgeAutoStart()
    {
        saved = PhoneSaberP2PBridgeProcess.AutoStartEnabled;
        PhoneSaberP2PBridgeProcess.AutoStartEnabled = false;
        savedRankingSync = RankingSync.AutoStartEnabled;
        RankingSync.AutoStartEnabled = false;
        savedClipRelay = PhoneSaberClipRelay.AutoStartEnabled;
        PhoneSaberClipRelay.AutoStartEnabled = false;
    }

    [OneTimeTearDown]
    public void RestoreBridgeAutoStart()
    {
        PhoneSaberP2PBridgeProcess.AutoStartEnabled = saved;
        RankingSync.AutoStartEnabled = savedRankingSync;
        PhoneSaberClipRelay.AutoStartEnabled = savedClipRelay;
    }
}
