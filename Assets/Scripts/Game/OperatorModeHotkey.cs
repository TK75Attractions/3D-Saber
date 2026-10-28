using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

// F12 で運営モード(OperatorMode)を切り替える常駐の部品(2026-10-29)。起動時に1つだけ作り、シーンをまたいで残す。テスト中は作らない。
// 結果画面の一言の欄で打っている間・幕(暗転)の間は何もしない。曲の本編の演奏中(Game シーン)は切り替えず、運営の知らせで断る
// (その回の表は曲を始めたときに決まる。判定調整・練習の間は切り替えてよい)。F1〜F12 は来場者の操作に数えない(PhoneSaberOperatorKeys)。
public sealed class OperatorModeHotkey : MonoBehaviour
{
    public const Key ToggleKey = Key.F12;
    public const string OnNotice = "運営モード オン: ランキングと実績は本番用(2台で共有)";
    public const string OffNotice = "運営モード オフ: ふだんのランキングと実績";
    public const string PlayingNotice = "演奏中は切り替えません";
    public const float NoticeSeconds = 4f;
    const string GameSceneName = "Game";
    // Test Runner の窓から回したテストでも作らない(RankingSync と同じ)。
    const string TestRunnerObjectName = "Code-based tests runner";

    public enum Decision { Ignore, Refuse, Toggle }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void CreateAtStartup()
    {
        if (!PhoneSaberP2PBridgeProcess.AutoStartEnabled) return; // テスト中は作らない
        if (GameObject.Find(TestRunnerObjectName) != null || FindFirstObjectByType<OperatorModeHotkey>() != null) return;
        var go = new GameObject("OperatorModeHotkey");
        DontDestroyOnLoad(go);
        go.AddComponent<OperatorModeHotkey>();
    }

    // F12 を押したときの決め方。
    public static Decision Decide(bool textEntry, bool transitionBusy, bool playingSong)
    {
        if (textEntry || transitionBusy) return Decision.Ignore;
        return playingSong ? Decision.Refuse : Decision.Toggle;
    }

    // 曲の本編を演奏している場面か(Game シーンで、判定調整・練習ではない)。
    public static bool PlayingSong()
    {
        if (SceneManager.GetActiveScene().name != GameSceneName || GameSession.IsCalibrationMode) return false;
        var manager = FindFirstObjectByType<GamePlayManager>();
        return manager == null || manager.Tutorial == null;
    }

    void Update()
    {
        // 作ったあとにテストが始まった(SetUpFixture が AutoStartEnabled を切った)ら、すぐ消える。
        if (!PhoneSaberP2PBridgeProcess.AutoStartEnabled) { Destroy(gameObject); return; }
        var keyboard = Keyboard.current;
        if (keyboard == null || !keyboard[ToggleKey].wasPressedThisFrame) return;
        Press(Decide(PhoneSaberOperatorKeys.TextEntryActive, ScreenTransition.IsBusy, PlayingSong()));
    }

    // 決めたとおりに切り替える・断る(テストからも呼ぶ)。
    public static void Press(Decision decision)
    {
        if (decision == Decision.Refuse)
        {
            OperatorNotice.Show(PlayingNotice, NoticeSeconds);
            return;
        }
        if (decision != Decision.Toggle) return;
        OperatorMode.Toggle();
        bool on = OperatorMode.IsOn;
        OperatorNotice.Show(on ? OnNotice : OffNotice, NoticeSeconds);
        PhoneSaberEventLog.Record(PhoneSaberStation.Read(), "SYSTEM", "operator-mode", on ? "on" : "off");
    }
}
