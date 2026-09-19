using UnityEngine;

// 選曲画面の全ノーツに共通する速度・連続発動制限。ゲーム本編の判定設定とは分離する。
[DefaultExecutionOrder(-80)]
public class SongSelectNoteMenu : MonoBehaviour
{
    public const float MinimumCutSpeed = 3f;
    public const float CooldownSeconds = .7f;
    public static SongSelectNoteMenu Instance { get; private set; }
    public bool IsReady => !ScreenTransition.IsBusy && Time.unscaledTime >= readyAt;
    float readyAt;
    AudioSource audioSource;
    AudioClip cutSound;

    public static SongSelectNoteMenu Build()
    {
        if (Instance != null) return Instance;
        return new GameObject("SongSelectNoteMenu").AddComponent<SongSelectNoteMenu>();
    }

    void Awake()
    {
        Instance = this;
        readyAt = Time.unscaledTime + .5f; // 画面を開いた直後の移動で切らない。
        audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.playOnAwake = false;
    }

    public void BeginCooldown()
    {
        readyAt = Time.unscaledTime + CooldownSeconds;
        // 同じ判定ループ内の別ノーツも直ちに無効化する。
        foreach (var target in Object.FindObjectsByType<MenuNoteAction>(FindObjectsSortMode.None))
            if (target.Note != null) target.Note.IsJudgeable = false;
        var nav = Object.FindFirstObjectByType<SongSelectSlashNav>();
        if (nav != null)
        {
            if (nav.UpNote != null) nav.UpNote.IsJudgeable = false;
            if (nav.DownNote != null) nav.DownNote.IsJudgeable = false;
        }
        if (Application.isPlaying)
        {
            if (cutSound == null) cutSound = JudgmentSfx.Beep(880, .07f);
            audioSource.PlayOneShot(cutSound, .17f);
        }
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        UISkinKit.SafeDestroy(cutSound);
    }
}
