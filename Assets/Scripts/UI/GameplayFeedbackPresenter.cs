using TMPro;
using UnityEngine;
using UnityEngine.UI;

// コンボ節目・FC状態・曲の締めを表示。判定文字は従来のGameHUDSkinに任せる。
// GamePlayManagerが一か所からTickする。
public sealed class GameplayFeedbackPresenter : MonoBehaviour
{
    NoteSpawner spawner;
    ScoreManager score;
    RectTransform outroRule;
    TextMeshProUGUI fc, milestone, ending, endingDetail;
    CanvasGroup endingGroup;
    float milestoneAge = 99, endingAge = -1;
    int latestMilestone;
    // コンボ数の後ろの炎(2026-09-23 ユーザー依頼): AP中は虹色、FC中は金色、曲の進行で強くなり、条件が崩れたら消える。
    ComboFlameGraphic flame;
    readonly ComboFlameEnvelope flameEnvelope = new ComboFlameEnvelope();
    float flameTime;
    public ComboFlameMode FlameMode => flameEnvelope.Shown;
    public float FlameLevel => flameEnvelope.Level;
    public bool OutroStarted => endingAge >= 0;
    public string FullComboLabel => fc != null ? fc.text : "";
    public string EndingLabel => ending != null ? ending.text : "";
    public int LatestMilestone => latestMilestone;

    public static GameplayFeedbackPresenter Create(NoteSpawner spawner, ScoreManager score)
    {
        var go = new GameObject("GameplayFeedback", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
        var result = go.AddComponent<GameplayFeedbackPresenter>();
        result.Build(spawner, score);
        return result;
    }

    void Build(NoteSpawner owner, ScoreManager scoring)
    {
        spawner = owner; score = scoring;
        var canvas = GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 505;
        var scaler = GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = .5f;
        // 曲中の初回判定でフォントアトラスを増やさないよう、使う字を開始前に準備する。
        var labelFont = UISkinKit.FontAsset("Oxanium-Bold");
        if (labelFont != null) labelFont.TryAddCharacters("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 /,.ms");
        fc = Text(transform, "FullComboStatus", "FC READY", 21, new Vector2(280, 32), Vector2.zero, TextAlignmentOptions.TopRight);
        TopRight(fc.rectTransform, new Vector2(-74, -376));
        fc.color = new Color(.57f, .64f, .7f);
        milestone = Text(transform, "ComboMilestone", "", 25, new Vector2(300, 36), Vector2.zero, TextAlignmentOptions.TopRight);
        TopRight(milestone.rectTransform, new Vector2(-74, -414));
        var endRoot = Rect(transform, "TrackEnding", new Vector2(720, 170), new Vector2(0, 15));
        endingGroup = endRoot.gameObject.AddComponent<CanvasGroup>(); endingGroup.blocksRaycasts = endingGroup.interactable = false; endingGroup.alpha = 0;
        var endBackground = endRoot.gameObject.AddComponent<Image>(); endBackground.color = new Color(.02f, .035f, .05f, .92f); endBackground.raycastTarget = false;
        ending = Text(endRoot, "Title", "", 58, new Vector2(680, 76), new Vector2(0, 20));
        endingDetail = Text(endRoot, "Detail", "", 20, new Vector2(680, 30), new Vector2(0, -39));
        outroRule = Rect(endRoot, "FinishRule", new Vector2(0, 3), new Vector2(0, -69));
        var line = outroRule.gameObject.AddComponent<Image>(); line.color = new Color(.3f, .85f, .87f); line.raycastTarget = false;
        BuildFlame();
        if (score != null) score.OnJudgment += Scored;
    }

    // 炎は HUD(sortingOrder 500)のコンボ数字の下に描く。数字の右端(-70)と下端(約-292)に矩形の右下を合わせる。
    void BuildFlame()
    {
        var holder = new GameObject("ComboFlame", typeof(RectTransform), typeof(Canvas));
        holder.transform.SetParent(transform, false);
        var canvas = holder.GetComponent<Canvas>(); canvas.overrideSorting = true; canvas.sortingOrder = 499;
        var holderRect = (RectTransform)holder.transform;
        holderRect.anchorMin = holderRect.anchorMax = Vector2.one; holderRect.pivot = new Vector2(1, 0);
        holderRect.anchoredPosition = new Vector2(-70, -292); holderRect.sizeDelta = new Vector2(340, 260);
        var graphicGo = new GameObject("ComboFlameGraphic", typeof(RectTransform));
        graphicGo.transform.SetParent(holder.transform, false);
        var graphicRect = (RectTransform)graphicGo.transform;
        graphicRect.anchorMin = graphicRect.anchorMax = new Vector2(1, 0); graphicRect.pivot = new Vector2(1, 0);
        graphicRect.anchoredPosition = Vector2.zero; graphicRect.sizeDelta = new Vector2(340, 260);
        flame = graphicGo.AddComponent<ComboFlameGraphic>(); flame.raycastTarget = false;
    }

    void TickFlame(float delta, double songTime, double duration)
    {
        flameTime += delta;
        var desired = score == null ? ComboFlameMode.None
            : ComboFlameLogic.DesiredMode(score.HitCount, score.PerfectCount, score.BadCount, score.MissCount);
        flameEnvelope.Tick(desired, delta);
        if (flame == null) return;
        int digits = score != null ? Mathf.Max(1, score.Combo).ToString().Length : 1;
        flame.SetState(flameEnvelope.Shown, ComboFlameLogic.Intensity(songTime, duration), flameEnvelope.Level, flameTime,
            ComboFlameLogic.FlameWidth(digits));
    }

    public static bool FullComboEligible(int hits, int bad, int miss) => hits > 0 && bad == 0 && miss == 0;
    public static bool IsMilestone(int combo) => combo > 0 && combo % 50 == 0;
    public static bool CanEnd(double time, double duration, double lastNote, int total, int judged, int spawned)
        => total > 0 && judged >= total && spawned >= total && duration > 0 && time >= System.Math.Max(duration, lastNote);

    void Scored(JudgmentTier tier, int award)
    {
        if (score == null || !isActiveAndEnabled) return;
        if (score.Combo > 0 && IsMilestone(score.Combo))
        { latestMilestone = score.Combo; milestoneAge = 0; milestone.text = score.Combo + " CHAIN"; }
        if (score.Combo == 0) { milestoneAge = 99; milestone.text = ""; }
        UpdateFullCombo();
    }
    void UpdateFullCombo()
    {
        if (score == null) return;
        bool intact = FullComboEligible(score.HitCount, score.BadCount, score.MissCount);
        fc.text = intact ? "FC ACTIVE" : score.BadCount + score.MissCount > 0 ? "FC LOST" : "FC READY";
        fc.color = intact ? new Color(.57f, .86f, .84f) : new Color(.5f, .55f, .61f);
    }

    public void Tick(float delta, double songTime, double duration, double lastNote)
    {
        if (!isActiveAndEnabled) return;
        delta = Mathf.Clamp(delta, 0, .1f);
        UpdateFullCombo();
        TickFlame(delta, songTime, duration);
        milestoneAge += delta;
        milestone.color = new Color(.79f, .9f, .93f, 1 - Mathf.InverseLerp(.6f, 1.1f, milestoneAge));
        milestone.rectTransform.localScale = Vector3.one * (1 + (DisplaySettings.ReducedEffects ? 0 : .12f) * Mathf.Clamp01(1 - milestoneAge / .22f));
        if (!OutroStarted && score != null && spawner != null && CanEnd(songTime, duration, lastNote, spawner.TotalNoteCount,
            score.HitCount + score.MissCount, spawner.NextIndex)) BeginOutro();
        if (OutroStarted)
        {
            endingAge += delta;
            endingGroup.alpha = Mathf.Clamp01(endingAge / .25f);
            outroRule.sizeDelta = new Vector2(560 * (DisplaySettings.ReducedEffects ? 1 : Mathf.SmoothStep(0, 1, endingAge / .8f)), 3);
        }
    }
    public void BeginOutro()
    {
        if (OutroStarted) return;
        endingAge = 0;
        bool full = score != null && FullComboEligible(score.HitCount, score.BadCount, score.MissCount);
        ending.text = full ? "FULL COMBO" : "TRACK CLEAR";
        ending.color = full ? new Color(.6f, .94f, .88f) : new Color(.88f, .92f, .97f);
        endingDetail.text = score == null ? "" : "BEST CHAIN  " + score.MaxCombo + "    /    SCORE  " + score.Score.ToString("N0");
    }
    void OnDestroy()
    {
        if (score != null) score.OnJudgment -= Scored;
    }
    static RectTransform Rect(Transform parent, string name, Vector2 size, Vector2 position)
    {
        var root = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        root.SetParent(parent, false); root.anchorMin = root.anchorMax = root.pivot = new Vector2(.5f, .5f);
        root.sizeDelta = size; root.anchoredPosition = position; return root;
    }
    static TextMeshProUGUI Text(Transform parent, string name, string text, float size, Vector2 bounds, Vector2 position, TextAlignmentOptions alignment = TextAlignmentOptions.Center)
    {
        var result = UISkinKit.MakeTMP(parent, name, text, size, new Color(.83f, .89f, .92f), alignment,
            position, bounds, FontStyles.Normal, 0, UISkinKit.FontAsset("Oxanium-Bold"));
        result.raycastTarget = false; result.enableAutoSizing = true; result.fontSizeMin = size * .8f; result.fontSizeMax = size;
        return result;
    }
    static void TopRight(RectTransform root, Vector2 position)
    { root.anchorMin = root.anchorMax = root.pivot = Vector2.one; root.anchoredPosition = position; }
}
