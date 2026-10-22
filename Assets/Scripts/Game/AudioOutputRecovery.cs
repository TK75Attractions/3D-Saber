using UnityEngine;
using UnityEngine.SceneManagement;

// 音の出力先が変わったときの立て直し(2026-10-22)。
// 投影機やモニター(HDMI・DP の音)の電源の入れ直し・入力の切り替え・ケーブル・Bluetooth スピーカーで Windows の出力先が変わると、
// Unity は音の仕組みを作り直す。このとき、スクリプトで作った音も、StreamingAssets から読んだ曲も中身が消え(サンプル数 0)、
// 再生中の音は止まる(Unity マニュアル「Audio Settings」。このプロジェクトでも AudioSettings.Reset で確かめた)。
// ・シーンをまたいで持つ音(MenuSfx・GameStartCountdown)は、鳴らす前に中身を確かめて作り直す(各クラス)。
// ・画面ごとに作った音は、その画面を作り直して取り戻す。選曲は曲・難易度・残り時間・操作したかを保つ。
// ・本編の途中は続きから鳴らし直せないので、記録せずにタイトルへ戻す。終わりの演出の間は結果を確定して結果へ。
//   練習は曲えらびへ。判定調整はその場で作り直す(CalibrationController)。
// 知らせは続けて来ることがあるので、最後の知らせから SettleSeconds 待ち、幕の間は幕が開いてから立て直す。
public sealed class AudioOutputRecovery : MonoBehaviour
{
    public enum Action { None, ReloadScreen, KeepSelection, AbortToTitle, FinishToResult, TutorialToSongSelect, CalibrationInPlace }
    public const float SettleSeconds = .5f;
    public const float NoticeSeconds = 4f;
    public const string ScreenNotice = "音の出力先が変わったので、画面を作り直しました";
    public const string AbortNotice = "音の出力先が変わったため、曲を中断しました（記録しません）";
    public const string TutorialNotice = "音の出力先が変わったため、練習を終えて曲えらびへ進みました";
    public const string ResultNotice = "音の出力先が変わったため、終わりの演出を飛ばして結果へ進みました";

    static AudioOutputRecovery instance;
    static bool pending;
    static double lastChangeAt = double.NegativeInfinity;
    static string noticeForNextScreen;
    static bool noticeShownForScene;
    // 立て直した回数と、最後にしたこと(テストと確認用)。
    public static int RecoveryCount { get; private set; }
    public static Action LastAction { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        AudioSettings.OnAudioConfigurationChanged -= Changed;
        SceneManager.sceneLoaded -= SceneLoaded;
        instance = null; pending = false; lastChangeAt = double.NegativeInfinity; noticeForNextScreen = null; noticeShownForScene = false;
        RecoveryCount = 0; LastAction = Action.None;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Initialize()
    {
        if (instance != null) return;
        var go = new GameObject("AudioOutputRecovery");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<AudioOutputRecovery>();
        AudioSettings.OnAudioConfigurationChanged -= Changed;
        AudioSettings.OnAudioConfigurationChanged += Changed;
        SceneManager.sceneLoaded -= SceneLoaded;
        SceneManager.sceneLoaded += SceneLoaded;
    }

    // 知らせは、立て直した先の画面で幕が開いてから出す(OperatorNotice はシーンを出ると消える)。
    static void SceneLoaded(Scene scene, LoadSceneMode mode) { noticeShownForScene = false; }

    // 出力先が変わった(引数は「機器が変わった」。設定の作り直し=false でも中身は消えるので同じに扱う)。
    // 本編の途中なら、立て直すまでの間に曲が終わって「完了」と記録されないよう、この場で曲を止めておく。
    static void Changed(bool deviceWasChanged)
    {
        pending = true;
        lastChangeAt = Time.realtimeSinceStartupAsDouble;
        Debug.Log("AudioOutputRecovery: 音の出力先(設定)が変わりました deviceWasChanged=" + deviceWasChanged);
        if (Application.isPlaying)
        {
            var manager = Object.FindFirstObjectByType<GamePlayManager>();
            if (manager != null) manager.HoldForAudioChange();
        }
    }

    void Update()
    {
        double now = Time.realtimeSinceStartupAsDouble;
        if (noticeForNextScreen != null && !noticeShownForScene && !ScreenTransition.IsBusy)
        {
            OperatorNotice.Show(noticeForNextScreen, NoticeSeconds);
            noticeForNextScreen = null; noticeShownForScene = true;
        }
        if (!pending || now - lastChangeAt < SettleSeconds || ScreenTransition.IsBusy) return;
        pending = false;
        Recover(SceneManager.GetActiveScene().name);
    }

    // 画面ごとの立て直し。テストからも呼ぶ。
    public static Action Recover(string sceneName)
    {
        var manager = sceneName == "Game" ? Object.FindFirstObjectByType<GamePlayManager>() : null;
        Action action = manager != null ? manager.HandleAudioOutputChanged() : PlanScreen(sceneName);
        switch (action)
        {
            case Action.ReloadScreen:
                if (ScreenTransition.Load(sceneName, sceneName == "Result" ? ScreenTransition.Style.Result : ScreenTransition.Style.Back))
                    noticeForNextScreen = ScreenNotice;
                break;
            case Action.KeepSelection:
                if (SongSelectSkin.ReloadKeepingSelection()) noticeForNextScreen = ScreenNotice;
                break;
            case Action.AbortToTitle: noticeForNextScreen = AbortNotice; break;
            case Action.FinishToResult: noticeForNextScreen = ResultNotice; break;
            case Action.TutorialToSongSelect: noticeForNextScreen = TutorialNotice; break;
        }
        noticeShownForScene = false;
        RecoveryCount++; LastAction = action;
        Debug.Log("AudioOutputRecovery: " + sceneName + " → " + action);
        return action;
    }

    // 本編以外の画面で、どう立て直すか。純関数。
    public static Action PlanScreen(string sceneName)
    {
        switch (sceneName)
        {
            case "Title": case "Result": return Action.ReloadScreen;
            case "SongSelect": return Action.KeepSelection;
            default: return Action.None;
        }
    }

    // 本編(Game シーン)で、どう立て直すか。純関数。
    public static Action PlanGame(bool calibration, bool tutorial, bool completing, bool finished, bool leaving)
    {
        if (leaving) return Action.None;
        if (calibration) return Action.CalibrationInPlace;
        if (tutorial) return Action.TutorialToSongSelect;
        if (completing) return Action.FinishToResult;
        if (finished) return Action.None;
        return Action.AbortToTitle;
    }

    // テスト用: 出力先の変化が来たことにする(実際の AudioSettings.Reset を呼ばずに流れを確かめる)。
    public static void SimulateChangeForTest() => Changed(false);
}
