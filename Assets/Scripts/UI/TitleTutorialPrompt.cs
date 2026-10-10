using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

// タイトルの開始ノーツを切った直後、幕(前進演出の暗転)の中で「チュートリアルをしますか？」と問いかける。
// 答えは「はい」「いいえ」のノーツを切る(タイトルの刃と同じ判定)か、Enter / Space = はい、N = いいえ。
// 答えがなければ DefaultTimeoutSeconds で「はい」(初めての人ほど固まるため)。
public sealed class TitleTutorialPrompt : MonoBehaviour
{
    int shownSeconds = -1;
    public const float DefaultTimeoutSeconds = 10f;
    // 前進演出(ロゴが去り、幕の色まで暗くなる)を見せ切ってから問いかけを出す。
    public const float RevealDelaySeconds = TitlePresentationMotion.DepartureDuration;
    public const float ProceedDelaySeconds = .3f;
    public static readonly Vector3 YesPosition = new Vector3(-1.9f, -.25f, 0f);
    public static readonly Vector3 NoPosition = new Vector3(1.9f, -.25f, 0f);
    public static readonly Color NoColor = new Color(.55f, .62f, .75f);

    public bool IsOpen { get; private set; }          // 問いかけが見えて、答えを受け付けている
    public bool Answered { get; private set; }
    public bool AnswerIsTutorial { get; private set; }
    public TitleStartNote YesNote { get; private set; }
    public TitleStartNote NoNote { get; private set; }
    public float RemainingSeconds => !IsOpen || timeout <= 0f ? timeout : Mathf.Max(0f, timeout - (Time.unscaledTime - openedAt));

    Canvas canvas;
    TitlePresentationMotion motion;
    System.Action<bool> onAnswer;
    float timeout, startedAt, openedAt;
    CanvasGroup group;
    TextMeshProUGUI remaining, yesLabel, noLabel;
    AudioClip chimeLow, chimeHigh;

    public static TitleTutorialPrompt Begin(Canvas canvas, TitlePresentationMotion motion, System.Action<bool> onAnswer,
        float timeoutSeconds = DefaultTimeoutSeconds)
    {
        var go = new GameObject("TitleTutorialPrompt", typeof(RectTransform), typeof(CanvasGroup));
        go.transform.SetParent(canvas.transform, false);
        go.transform.SetAsLastSibling();
        SongSelectVisuals.Stretch(go.GetComponent<RectTransform>());
        var prompt = go.AddComponent<TitleTutorialPrompt>();
        prompt.canvas = canvas; prompt.motion = motion; prompt.onAnswer = onAnswer; prompt.timeout = timeoutSeconds;
        prompt.group = go.GetComponent<CanvasGroup>();
        prompt.group.alpha = 0f; prompt.group.blocksRaycasts = false; prompt.group.interactable = false;
        prompt.BuildText();
        prompt.startedAt = Time.unscaledTime;
        return prompt;
    }

    void BuildText()
    {
        var ink = new Color(.91f, .97f, 1f);
        var muted = new Color(.65f, .76f, .82f);
        var eyebrow = SongSelectVisuals.Label(transform, "Eyebrow", "TUTORIAL", 26, new Vector2(0, 300), new Vector2(600, 40),
            UISkinPalette.Cyan, TextAlignmentOptions.Center, true);
        eyebrow.characterSpacing = 12f;
        SongSelectVisuals.Label(transform, "Question", "チュートリアルをしますか？", 60, new Vector2(0, 222), new Vector2(1600, 96),
            ink, TextAlignmentOptions.Center, true);
        SongSelectVisuals.Label(transform, "Guide", "初めての方は「はい」を切ってください", 30, new Vector2(0, 152), new Vector2(1600, 48),
            muted, TextAlignmentOptions.Center, false);
        remaining = SongSelectVisuals.Label(transform, "Remaining", "", 26, new Vector2(0, -330), new Vector2(900, 40),
            muted, TextAlignmentOptions.Center, false);
        yesLabel = SongSelectVisuals.Label(transform, "YesLabel", "はい", 46, Vector2.zero, new Vector2(400, 70),
            UISkinPalette.NoteGold, TextAlignmentOptions.Center, true);
        noLabel = SongSelectVisuals.Label(transform, "NoLabel", "いいえ", 46, Vector2.zero, new Vector2(400, 70),
            NoColor, TextAlignmentOptions.Center, true);
        foreach (var t in GetComponentsInChildren<TextMeshProUGUI>(true)) t.raycastTarget = false;
    }

    void Update()
    {
        float age = Time.unscaledTime - startedAt;
        if (!IsOpen)
        {
            // 前進演出をこちらで進める。終端は本編の幕と同じ色の暗転になる。
            if (motion != null) motion.SetDeparture(Mathf.Clamp01(age / RevealDelaySeconds));
            if (age < RevealDelaySeconds) return;
            Open();
            return;
        }
        if (Answered) return;
        group.alpha = Mathf.MoveTowards(group.alpha, 1f, Time.unscaledDeltaTime / .25f);
        PlaceLabels();
        var keyboard = Keyboard.current;
        if (keyboard != null)
        {
            if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame || keyboard.spaceKey.wasPressedThisFrame)
            { Choose(true, "key"); return; }
            if (keyboard.nKey.wasPressedThisFrame) { Choose(false, "key"); return; }
        }
        if (timeout > 0f)
        {
            float left = timeout - (Time.unscaledTime - openedAt);
            // 表示する秒数が変わったときだけ文字列を作る（毎フレームの GC を避ける）。
            int shown = left > 0f ? Mathf.CeilToInt(left) : 0;
            if (remaining != null && shown != shownSeconds)
            {
                shownSeconds = shown;
                remaining.text = shown > 0 ? "あと " + shown + " 秒で「はい」" : "";
            }
            if (left <= 0f) Choose(true, "timeout");
        }
    }

    void Open()
    {
        IsOpen = true; openedAt = Time.unscaledTime; shownSeconds = -1;
        YesNote = TitleStartNote.Build(YesPosition, UISkinPalette.NoteGold);
        YesNote.name = "TutorialYesNote";
        NoNote = TitleStartNote.Build(NoPosition, NoColor);
        NoNote.name = "TutorialNoNote";
        YesNote.OnSlashed += () => Choose(true, "slash");
        NoNote.OnSlashed += () => Choose(false, "slash");
        PlaceLabels();
    }

    void PlaceLabels()
    {
        var cam = Camera.main;
        if (cam == null || canvas == null) return;
        Place(yesLabel, YesNote, cam);
        Place(noLabel, NoNote, cam);
    }

    void Place(TextMeshProUGUI label, TitleStartNote note, Camera cam)
    {
        if (label == null || note == null) return;
        Vector2 screen = cam.WorldToScreenPoint(note.transform.position + Vector3.down * 1.05f);
        var canvasCamera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle((RectTransform)canvas.transform, screen, canvasCamera, out var local))
            label.rectTransform.anchoredPosition = local;
    }

    public void Choose(bool tutorial, string how)
    {
        if (Answered || !IsOpen) return;
        Answered = true; AnswerIsTutorial = tutorial;
        Debug.Log("TitleTutorialPrompt: " + (tutorial ? "はい" : "いいえ") + " (" + how + ")");
        // 選ばなかった方は消し、選んだ方は砕ける演出を見せてから進む。
        var other = tutorial ? NoNote : YesNote;
        if (other != null) { other.Note.IsJudgeable = false; other.gameObject.SetActive(false); }
        var chosen = tutorial ? YesNote : NoNote;
        if (how != "slash" && chosen != null) chosen.SlashProgrammatically();
        PlayChime(tutorial);
        if (remaining != null) remaining.text = "";
        StartCoroutine(Proceed(tutorial));
    }

    IEnumerator Proceed(bool tutorial)
    {
        yield return new WaitForSecondsRealtime(ProceedDelaySeconds);
        onAnswer?.Invoke(tutorial);
    }

    void PlayChime(bool high)
    {
        if (chimeLow == null) chimeLow = JudgmentSfx.Beep(high ? 1046.5f : 659.3f, .16f);
        if (chimeHigh == null) chimeHigh = JudgmentSfx.Beep(high ? 1568f : 880f, .28f);
        var go = new GameObject("PromptSfx", typeof(AudioSource));
        go.transform.SetParent(transform, false);
        var src = go.GetComponent<AudioSource>();
        src.playOnAwake = false;
        src.PlayOneShot(chimeLow, .45f);
        src.PlayOneShot(chimeHigh, .35f);
        Destroy(go, 1.5f);
    }

    void OnDestroy()
    {
        if (YesNote != null) Destroy(YesNote.gameObject);
        if (NoNote != null) Destroy(NoNote.gameObject);
        UISkinKit.SafeDestroy(chimeLow);
        UISkinKit.SafeDestroy(chimeHigh);
        chimeLow = chimeHigh = null;
    }
}
