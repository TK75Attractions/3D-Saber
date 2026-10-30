using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

// どの画面からでも、右クリックを2秒以内に3回でタイトルへ戻る(スタッフ用、2026-10-30 ユーザー「どんな状況でも右クリック3回でホームに戻れるように」)。
// 起動時に1つだけ作り、シーンをまたいで残す(消えていたらシーンを読むたびに作り直す)。テスト中は作らない(OperatorModeHotkey と同じ)。
// 出口は各画面の Esc と同じ道: 本編・練習・判定調整・終わりの演出は GamePlayManager.LeaveToTitle、選曲は SongSelectController.ReturnToTitle、
// 結果は ResultController.ReturnToTitle、タイトルの問いかけは TitleSceneSkin.ReturnToStart。出口が無い・断った画面はタイトルを読み直す。
// 幕(ScreenTransition)の間に3回目を押したら覚えておき、幕が明けた画面から出る。1回目・2回目は運営の知らせで残りの回数を出す。
// 右ボタンを押さないかぎり何もしない(来場者の操作には右クリックを使っていない)。
public sealed class RightClickHome : MonoBehaviour
{
    public const string TitleScene = "Title", GameScene = "Game", SongSelectScene = "SongSelect", ResultScene = "Result";
    // Test Runner の窓から回したテストでも作らない(OperatorModeHotkey と同じ)。
    const string TestRunnerObjectName = "Code-based tests runner";

    static RightClickHome instance;
    readonly RightClickHomeGate gate = new RightClickHomeGate();
    bool pending;

    static bool TestsRunning => !PhoneSaberP2PBridgeProcess.AutoStartEnabled || GameObject.Find(TestRunnerObjectName) != null;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { instance = null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void CreateAtStartup()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
        Ensure();
    }

    static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => Ensure();

    static void Ensure()
    {
        if (instance != null || TestsRunning) return;
        var go = new GameObject("RightClickHome");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<RightClickHome>();
    }

    void OnDestroy() { if (instance == this) instance = null; }

    void Update()
    {
        // 作ったあとにテストが始まった(SetUpFixture が AutoStartEnabled を切った)ら、すぐ消える。
        if (!PhoneSaberP2PBridgeProcess.AutoStartEnabled) { Destroy(gameObject); return; }
        var mouse = Mouse.current;
        if (mouse != null && mouse.rightButton.wasPressedThisFrame) Press();
        if (pending && !ScreenTransition.IsBusy) { pending = false; GoHome(); }
    }

    void Press()
    {
        // タイトルの最初の状態(もうホーム)では数えない。
        if (AlreadyHome()) { gate.Reset(); return; }
        if (gate.Press(Time.unscaledTimeAsDouble, out int remaining)) { GoHome(); return; }
        OperatorNotice.Show(RightClickHomeGate.Notice(remaining));
    }

    // 今の画面の出口からタイトルへ。幕の間は覚えておき、幕が明けてから出る。
    void GoHome()
    {
        gate.Reset();
        if (ScreenTransition.IsBusy) { pending = true; return; }
        if (AlreadyHome()) return;
        string scene = SceneManager.GetActiveScene().name;
        PhoneSaberEventLog.Record(PhoneSaberStation.Read(), "SYSTEM", "right-click-home", scene);
        StaffReturnLog.Record("right-click");
        LeaveBy(scene);
        if (ScreenTransition.IsBusy) return;
        // 出口が無い・断った(幕が始まらなかった)ときは、タイトルをそのまま読み直す。判定調整の印は消してから。
        if (scene == GameScene && GameSession.IsCalibrationMode) GamePlayManager.ExitCalibration(TitleScene);
        else ScreenTransition.Load(TitleScene, ScreenTransition.Style.Back);
    }

    static void LeaveBy(string scene)
    {
        switch (scene)
        {
            case GameScene:
                var game = FindFirstObjectByType<GamePlayManager>();
                if (game != null) game.LeaveToTitle();
                break;
            case SongSelectScene:
                var select = FindFirstObjectByType<SongSelectController>();
                if (select != null) select.ReturnToTitle();
                break;
            case ResultScene:
                var result = FindFirstObjectByType<ResultController>();
                if (result != null) result.ReturnToTitle();
                break;
            case TitleScene:
                var title = FindFirstObjectByType<TitleSceneSkin>();
                if (title != null) title.ReturnToStart();
                break;
        }
    }

    // タイトルの最初の状態で、幕も無い(もうホームにいる)。
    static bool AlreadyHome()
    {
        if (ScreenTransition.IsBusy || SceneManager.GetActiveScene().name != TitleScene) return false;
        var title = FindFirstObjectByType<TitleSceneSkin>();
        return title != null && title.AtStart;
    }
}

// 右クリックを2秒以内に3回(RightClickHome)。滑る窓で数える(2秒より前の押下は捨てる)。純粋(時刻は呼び出し側が渡す)。
public sealed class RightClickHomeGate
{
    public const int PressesNeeded = 3;
    public const double WindowSeconds = 2;
    readonly Queue<double> presses = new Queue<double>();

    // 押した瞬間に呼ぶ。2秒以内の3回目なら true(数え直しに戻る)。それ以外は、あと何回で戻るかを remaining に入れる。
    public bool Press(double now, out int remaining)
    {
        while (presses.Count > 0 && now - presses.Peek() > WindowSeconds) presses.Dequeue();
        presses.Enqueue(now);
        if (presses.Count >= PressesNeeded) { presses.Clear(); remaining = 0; return true; }
        remaining = PressesNeeded - presses.Count;
        return false;
    }

    public void Reset() => presses.Clear();

    // 1回目・2回目の知らせ(EscapeTwiceGate の文に合わせる)。
    public static string Notice(int remaining) =>
        "タイトルへ戻るには、２秒以内にあと" + (remaining >= 2 ? "２" : "１") + "回 右クリック";
}
