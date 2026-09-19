using UnityEngine;

// 選曲画面の発射演出と短い操作待機。1秒照準と再発射のロックはAimPointerが担当。
[DefaultExecutionOrder(-80)]
public class SongSelectNoteMenu : MonoBehaviour
{
    public const float MinimumCutSpeed = 3f;
    public const float CooldownSeconds = .2f;
    public static SongSelectNoteMenu Instance { get; private set; }
    public bool IsReady => !ScreenTransition.IsBusy && Time.unscaledTime >= readyAt;
    float readyAt;
    public SongSelectShotEffect ShotEffect { get; private set; }

    public static SongSelectNoteMenu Build()
    {
        if (Instance != null) return Instance;
        return new GameObject("SongSelectNoteMenu").AddComponent<SongSelectNoteMenu>();
    }

    void Awake()
    {
        Instance = this;
        readyAt = Time.unscaledTime + .5f; // 画面を開いた直後の移動で切らない。
        var effect = new GameObject("MenuShotBurst");
        effect.transform.SetParent(transform, false);
        ShotEffect = effect.AddComponent<SongSelectShotEffect>();
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
    }

    public void PlayShot(CuttableNote note)
    {
        if (ShotEffect != null && note != null) ShotEffect.Play(note);
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }
}
