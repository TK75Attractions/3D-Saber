using System;
using System.Collections.Generic;
using UnityEngine;

// メニューと幕の効果音(派手さカタログ 盤1・盤2・盤3・盤5・幕1、2026-10-20)。
// スタートやタイトルへの音は直後に画面が切り替わっても鳴り終わるよう、シーンをまたいで残る1つの再生役が鳴らす。
// 音は FlourishSfx で一度だけ作り、作った AudioClip はこの再生役が持ち続ける(使う側は解放しない)。
// 鳴らす大きさは FlourishSfx 側に作り込んであるので音量 1 で鳴らす。プレイ中でないとき(EditMode)は記録だけ残す。
public sealed class MenuSfx : MonoBehaviour
{
    static MenuSfx instance;
    static readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>();
    AudioSource source;

    // 最後に鳴らした音の名前と回数(テストと確認用)。
    public static string LastPlayed { get; private set; }
    public static int PlayCount { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { instance = null; clips.Clear(); LastPlayed = null; PlayCount = 0; }

    // 起動直後に、使う音の波形を裏のスレッドでまとめて作る(FlourishSfx は UnityEngine を使わない純関数)。
    // 画面を開いた瞬間や曲中に合成して処理落ちしないようにする。AudioClip は各画面がメインスレッドで作る。
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void WarmInBackground()
    {
        System.Threading.Tasks.Task.Run(() =>
        {
            try { FlourishSfx.PrewarmAll(); }
            catch (Exception error) { Debug.LogWarning("効果音の作り置きに失敗しました(その場で作ります): " + error.Message); }
        });
    }

    static MenuSfx Ensure()
    {
        if (instance != null) return instance;
        var go = new GameObject("MenuSfx");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<MenuSfx>();
        instance.source = go.AddComponent<AudioSource>();
        instance.source.playOnAwake = false;
        instance.source.spatialBlend = 0;
        return instance;
    }

    // 名前ごとに一度だけ作る。プレイ中でなければ null(EditMode では AudioClip を残さない)。
    public static AudioClip Clip(string key, Func<float[]> build)
    {
        if (!Application.isPlaying) return null;
        Ensure();
        if (clips.TryGetValue(key, out var clip) && clip != null) return clip;
        clip = ProceduralSfx.Clip("Menu" + key, FlourishSfx.Cached(key, build));
        clips[key] = clip;
        return clip;
    }

    // 再生役はプレイの終わりにだけ消える。作った音もそのときに解放する。
    void OnDestroy()
    {
        if (instance != this) return;
        foreach (var clip in clips.Values) UISkinKit.SafeDestroy(clip);
        clips.Clear();
        instance = null;
    }

    public static void Play(string key, Func<float[]> build)
    {
        LastPlayed = key;
        PlayCount++;
        if (!Application.isPlaying) return;
        var clip = Clip(key, build);
        var player = Ensure();
        if (clip != null && player.source != null) player.source.PlayOneShot(clip, 1f);
    }

    // ---- 選曲で鳴らす音 ----
    public static void DiscMove(bool right) => Play(right ? "DiscRight" : "DiscLeft", () => FlourishSfx.DiscMove(right));
    public static void Difficulty(int index)
    {
        int i = Mathf.Clamp(index, 0, 2);
        Play("Difficulty" + i, () => FlourishSfx.Difficulty(i));
    }
    public static void Calibration() => Play("Calibration", FlourishSfx.Calibration);
    public static void ToTitle() => Play("ToTitle", FlourishSfx.ToTitle);
    public static void Fire() => Play("Fire", FlourishSfx.Fire);
    public static void StartHit() => Play("StartHit", FlourishSfx.StartHit);
    public static void TimerTick(int secondsLeft)
    {
        int s = Mathf.Clamp(secondsLeft, 1, 10);
        Play("TimerTick" + s, () => FlourishSfx.TimerTick(s));
    }
    public static void TimerChime() => Play("TimerChime", FlourishSfx.TimerChime);

    // 幕が閉じる「シュッ」(幕1)。戻る幕だけ低くする。
    public static void Curtain(ScreenTransition.Style style)
    {
        bool back = style == ScreenTransition.Style.Back;
        Play(back ? "CurtainBack" : "Curtain", () => FlourishSfx.CurtainSwish(back));
    }

    // ためる音(盤2)。照準の再生役が自分の AudioSource で鳴らす。スタートは2秒の専用の音。
    public static AudioClip ChargeClip(float holdSeconds, bool start)
    {
        if (start) return Clip("StartCharge", FlourishSfx.StartCharge);
        bool longHold = holdSeconds > 1.5f;
        return Clip(longHold ? "Charge2" : "Charge1", () => FlourishSfx.Charge(longHold ? 2 : 1));
    }

    // 選曲画面を開いた時点で、裏のスレッドが作り終えた音から AudioClip にしておく。まだの音は最初に鳴らすときに作る。
    // (ここで合成まで待つと、開いた瞬間の1フレームが長くなり、幕や案内の時計が飛ぶ)
    static readonly string[] songSelectKeys =
    {
        "DiscLeft", "DiscRight", "Difficulty0", "Difficulty1", "Difficulty2", "Calibration", "ToTitle", "Fire", "StartHit",
        "Charge1", "Charge2", "StartCharge", "Curtain", "CurtainBack", "TimerChime",
        "TimerTick1", "TimerTick2", "TimerTick3", "TimerTick4", "TimerTick5", "TimerTick6", "TimerTick7", "TimerTick8", "TimerTick9", "TimerTick10",
    };
    public static int PreparedCount { get; private set; }
    public static void PrepareSongSelect()
    {
        if (!Application.isPlaying) return;
        PreparedCount = 0;
        foreach (var key in songSelectKeys)
            if (FlourishSfx.TryGetCached(key, out var samples)) { Clip(key, () => samples); PreparedCount++; }
    }
}
