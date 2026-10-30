using NUnit.Framework;

// EditMode テストアセンブリ全体の既定: PhoneSaber P2P bridge を自動起動しない。
// InputPoint を作るテストでも本物の bridge(初回は swiftc build)を起動させないため。
// 台同士のランキング同期(RankingSync、UDP 5017)も自動では作らない(テストの回を本物の台へ送らないため)。
[SetUpFixture]
public sealed class PhoneSaberP2PBridgeEditorTestDefaults
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
