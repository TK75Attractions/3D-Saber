using UnityEngine;
using UnityEngine.SceneManagement;

// 音の出力先が変わったときの立て直し(2026-10-22)。
// 投影機やモニター(HDMI・DP の音)の電源の入れ直し・入力の切り替え・ケーブル・Bluetooth スピーカーで Windows の出力先が変わると、
// Unity は音の仕組みを作り直す。このとき再生中の音はすべて止まり、スクリプトで作った音と StreamingAssets から読んだ曲は
// 中身が消える(サンプル数 0。SetData でも戻らない)。同梱の音(Resources)は残り、DSP の時計も続く(このプロジェクトで確かめた)。
// 同日ユーザー「音の出力先が変わっても画面を戻さないで」: 画面は作り直さず、その場で音だけを立て直す。
// ・作った効果音は、鳴らす前に中身を確かめて同じ波形で作り直す(ProceduralSfx.Revive。MenuSfx・カウントインも同じ考え)。
// ・流しっぱなしの音(試聴・結果のループ曲・練習の曲)は、Restored を受けてその場で鳴らし直す。
// ・本編の曲の途中は、曲を止めずに(2026-10-24 ユーザー「曲止めないで上の表示だけにして」)裏で曲を読み直し、今の位置から鳴らし直す
//   (GamePlayManager.OnAudioOutputLost / ReloadSongInPlace)。知らせは上の1行だけ。
//   終わりの演出の間はそのまま続ける。判定調整は CalibrationController がその場で作り直す。
// 知らせは続けて来ることがあるので、最後の知らせから SettleSeconds 待ってから立て直す。
public sealed class AudioOutputRecovery : MonoBehaviour
{
    public enum Action { None, InPlace, ReloadSong, CalibrationInPlace }
    public const float SettleSeconds = .5f;
    public const float NoticeSeconds = 4f;
    public const string RestoredNotice = "音の出力先が変わったので、音を立て直しました";
    public const string ReloadNotice = "音の出力先が変わったので、曲の音を読み直しています(曲は止めません)";
    public const string ReloadFailedNotice = "曲を読み直せませんでした。音なしで続けます(スタッフへ: 音の出力先を確かめてください)";

    // 出力先が変わった直後(音はもう止まっている)。流しっぱなしの音の持ち主が、いま何を鳴らしていたかを覚えておく。
    public static event System.Action OutputChanged;
    // 知らせが落ち着いて、立て直すとき。流しっぱなしの音をその場で鳴らし直す。
    public static event System.Action Restored;

    static AudioOutputRecovery instance;
    static bool pending;
    static double lastChangeAt = double.NegativeInfinity;
    // 立て直した回数と、最後にしたこと(テストと確認用)。
    public static int RecoveryCount { get; private set; }
    public static Action LastAction { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        AudioSettings.OnAudioConfigurationChanged -= Changed;
        instance = null; pending = false; lastChangeAt = double.NegativeInfinity;
        OutputChanged = null; Restored = null;
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
    }

    // 出力先が変わった(引数は「機器が変わった」。設定の作り直し=false でも中身は消えるので同じに扱う)。
    // 本編の途中なら、読み直しかけの曲を取り消し、サビの音などの後始末だけをする(曲は止めない)。
    static void Changed(bool deviceWasChanged)
    {
        pending = true;
        lastChangeAt = Time.realtimeSinceStartupAsDouble;
        Debug.Log("AudioOutputRecovery: 音の出力先(設定)が変わりました deviceWasChanged=" + deviceWasChanged);
        if (!Application.isPlaying) return;
        var manager = Object.FindFirstObjectByType<GamePlayManager>();
        if (manager != null) manager.OnAudioOutputLost();
        OutputChanged?.Invoke();
    }

    void Update()
    {
        double now = Time.realtimeSinceStartupAsDouble;
        if (!pending || now - lastChangeAt < SettleSeconds) return;
        pending = false;
        Recover(SceneManager.GetActiveScene().name);
    }

    // その場での立て直し。テストからも呼ぶ。画面は作り直さない。
    public static Action Recover(string sceneName)
    {
        var manager = sceneName == "Game" ? Object.FindFirstObjectByType<GamePlayManager>() : null;
        Action action = manager != null ? manager.HandleAudioOutputChanged() : Action.InPlace;
        Restored?.Invoke();
        if (action == Action.InPlace) OperatorNotice.Show(RestoredNotice, NoticeSeconds);
        else if (action == Action.ReloadSong) OperatorNotice.Show(ReloadNotice, NoticeSeconds);
        RecoveryCount++; LastAction = action;
        Debug.Log("AudioOutputRecovery: " + sceneName + " → " + action);
        return action;
    }

    // 本編(Game シーン)で、どう立て直すか。純関数。
    // 曲を始める前(読み込み中)は、曲を予約する直前に GamePlayManager が音の中身を確かめて読み直すので、その場の立て直しだけ。
    public static Action PlanGame(bool calibration, bool tutorial, bool completing, bool finished, bool leaving, bool started)
    {
        if (leaving) return Action.None;
        if (calibration) return Action.CalibrationInPlace;
        if (tutorial || completing || !started) return Action.InPlace;
        if (finished) return Action.None;
        return Action.ReloadSong;
    }

    // テスト用: 出力先の変化が来たことにする(実際の AudioSettings.Reset を呼ばずに流れを確かめる)。
    public static void SimulateChangeForTest() => Changed(false);
}
