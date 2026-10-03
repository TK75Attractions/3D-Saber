using System.Runtime.CompilerServices;

// EditMode テストから、テスト専用の internal API(例: PhoneSaberP2PBridgeProcess の環境変数差し替え用 Start)を呼べるようにする。
[assembly: InternalsVisibleTo("SaberTests.Editor")]
