using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// 判定の説明と曲の締め。発光しないUIを使い、追加光はPerfectだけという約束を守る。
// GamePlayManagerが一か所からTickする。ポップアップは上限付きで再利用する。
public sealed class GameplayFeedbackPresenter : MonoBehaviour
{
    public const int PopupCapacity = 8;
    sealed class Popup
    {
        public RectTransform root;
        public CanvasGroup group;
        public TextMeshProUGUI title, detail;
        public GameplayFeedbackGlyph glyph;
        public Image rail;
        public Vector2 position;
        public float age = 99, lifetime;
        public int noteId;
    }
    readonly Popup[] popups = new Popup[PopupCapacity];
    readonly HashSet<CuttableNote> tracked = new HashSet<CuttableNote>();
    readonly List<CuttableNote> expired = new List<CuttableNote>();
    NoteSpawner spawner;
    ScoreManager score;
    GameHUDSkin hud;
    RectTransform canvasRect, outroRule;
    TextMeshProUGUI fc, milestone, ending, endingDetail;
    CanvasGroup endingGroup;
    float milestoneAge = 99, endingAge = -1;
    int latestMilestone;
    public int ActivePopupCount { get; private set; }
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
        canvasRect = (RectTransform)transform;
        // 曲中の初回判定でフォントアトラスを増やさないよう、使う字を開始前に準備する。
        var labelFont = UISkinKit.FontAsset("Oxanium-Bold");
        if (labelFont != null) labelFont.TryAddCharacters("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 /,.ms");
        for (int i = 0; i < popups.Length; i++)
        {
            var root = Rect(transform, "Judgment" + i, new Vector2(230, 68), Vector2.zero);
            var background = root.gameObject.AddComponent<Image>(); background.color = new Color(.025f, .045f, .06f, .9f); background.raycastTarget = false;
            var popup = new Popup { root = root, group = root.gameObject.AddComponent<CanvasGroup>() };
            popup.group.blocksRaycasts = popup.group.interactable = false;
            popup.rail = Rect(root, "Edge", new Vector2(3, 54), new Vector2(-111, 0)).gameObject.AddComponent<Image>(); popup.rail.raycastTarget = false;
            popup.glyph = Rect(root, "Symbol", new Vector2(26, 26), new Vector2(-88, 0)).gameObject.AddComponent<GameplayFeedbackGlyph>(); popup.glyph.raycastTarget = false;
            popup.title = Text(root, "Label", "", 26, new Vector2(172, 32), new Vector2(21, 12), TextAlignmentOptions.MidlineLeft);
            popup.detail = Text(root, "Detail", "", 16, new Vector2(172, 24), new Vector2(21, -16), TextAlignmentOptions.MidlineLeft);
            root.gameObject.SetActive(false); popups[i] = popup;
        }
        fc = Text(transform, "FullComboStatus", "FC READY", 21, new Vector2(280, 32), Vector2.zero, TextAlignmentOptions.TopRight);
        TopRight(fc.rectTransform, new Vector2(-74, -206));
        fc.color = new Color(.57f, .64f, .7f);
        milestone = Text(transform, "ComboMilestone", "", 25, new Vector2(300, 36), Vector2.zero, TextAlignmentOptions.TopRight);
        TopRight(milestone.rectTransform, new Vector2(-74, -244));
        var endRoot = Rect(transform, "TrackEnding", new Vector2(720, 170), new Vector2(0, 15));
        endingGroup = endRoot.gameObject.AddComponent<CanvasGroup>(); endingGroup.blocksRaycasts = endingGroup.interactable = false; endingGroup.alpha = 0;
        var endBackground = endRoot.gameObject.AddComponent<Image>(); endBackground.color = new Color(.02f, .035f, .05f, .92f); endBackground.raycastTarget = false;
        ending = Text(endRoot, "Title", "", 58, new Vector2(680, 76), new Vector2(0, 20));
        endingDetail = Text(endRoot, "Detail", "", 20, new Vector2(680, 30), new Vector2(0, -39));
        outroRule = Rect(endRoot, "FinishRule", new Vector2(0, 3), new Vector2(0, -69));
        var line = outroRule.gameObject.AddComponent<Image>(); line.color = new Color(.3f, .85f, .87f); line.raycastTarget = false;
        hud = Object.FindFirstObjectByType<GameHUDSkin>();
        if (hud != null) hud.UseLocalJudgments = true;
        if (spawner != null) spawner.OnNoteSpawned += Track;
        if (score != null) score.OnJudgment += Scored;
    }

    public void Track(CuttableNote note)
    {
        if (note == null || !tracked.Add(note)) return;
        note.OnJudged += Judged; note.OnMiss += Missed; note.OnRejected += Rejected;
    }
    void Untrack(CuttableNote note)
    {
        if (!tracked.Remove(note) || note == null) return;
        note.OnJudged -= Judged; note.OnMiss -= Missed; note.OnRejected -= Rejected;
    }
    void Judged(CuttableNote note, JudgmentTier tier, Vector3 point, Vector3 velocity)
    {
        string detail = score == null ? "" : GameHUDSkin.FormatTimingHint(tier, score.LastErrorValid, score.LastErrorMs);
        if (note.RequiredCutCount > 1) detail = note.CutsAchieved + " / " + note.RequiredCutCount + " CUTS";
        if (!note.LastCutCorrectDirection) detail = "CHECK DIRECTION";
        if (tier == JudgmentTier.Perfect && score != null && spawner != null &&
            score.HitCount + score.MissCount == spawner.TotalNoteCount) detail = "LAST CUT";
        Show(note, point, JudgmentTierHelper.Label(tier), detail, GameHUDSkin.TierColor(tier),
            tier == JudgmentTier.Miss ? GameplayFeedbackGlyph.Kind.Cross : GameplayFeedbackGlyph.Kind.Slash, CutDirection.None, .52f);
        Untrack(note);
    }
    void Missed(CuttableNote note)
    {
        Show(note, note.transform.position, "MISS", "", new Color(.94f, .58f, .5f), GameplayFeedbackGlyph.Kind.Cross, CutDirection.None, .65f);
        Untrack(note);
    }
    void Rejected(CuttableNote note, CutRejectionReason reason, Vector3 point)
    {
        string title, detail;
        Color color = new Color(1, .77f, .4f);
        var symbol = GameplayFeedbackGlyph.Kind.Slash;
        if (reason == CutRejectionReason.Hand)
        {
            bool left = note.RequiredHand == SaberHand.Left;
            title = left ? "LEFT HAND" : "RIGHT HAND"; detail = left ? "BLUE NOTE" : "RED NOTE";
            color = left ? new Color(.38f, .72f, 1) : new Color(1, .47f, .42f);
        }
        else if (reason == CutRejectionReason.Direction)
        { title = "DIRECTION"; detail = "FOLLOW THE ARROW"; symbol = GameplayFeedbackGlyph.Kind.Arrow; }
        else { title = "TOO SLOW"; detail = "SWING THROUGH"; }
        Show(note, point, title, detail, color, symbol, note.RequiredDirection, .68f);
    }

    void Show(CuttableNote note, Vector3 point, string title, string detail, Color color, GameplayFeedbackGlyph.Kind kind, CutDirection direction, float lifetime)
    {
        if (!isActiveAndEnabled || ScreenTransition.IsBusy) return;
        var camera = Camera.main;
        if (camera == null) return;
        Vector3 viewport = camera.WorldToViewportPoint(point);
        if (viewport.z <= 0 || float.IsNaN(viewport.x) || float.IsNaN(viewport.y)) return;
        int id = note.GetInstanceID();
        Popup selected = null;
        foreach (var popup in popups) if (popup.noteId == id && popup.age < popup.lifetime) { selected = popup; break; }
        if (selected == null) foreach (var popup in popups) if (popup.age >= popup.lifetime) { selected = popup; break; }
        if (selected == null)
        {
            selected = popups[0];
            foreach (var popup in popups) if (popup.age > selected.age) selected = popup;
        }
        Vector2 bounds = canvasRect.rect.size;
        if (bounds.x < 1 || bounds.y < 1) bounds = new Vector2(1920, 1080);
        Vector2 position = PopupPosition(viewport, bounds);
        for (int attempt = 0; attempt < 4; attempt++)
        {
            bool overlap = false;
            foreach (var popup in popups)
                if (popup != selected && popup.age < popup.lifetime && Mathf.Abs(popup.position.x - position.x) < 236 && Mathf.Abs(popup.position.y - position.y) < 72) { overlap = true; break; }
            if (!overlap) break;
            position.y -= 74;
        }
        position.y = Mathf.Clamp(position.y, -bounds.y * .5f + 60, bounds.y * .5f - 220);
        // 高密度の同位置連打では、画面端へ押し込んだ古い表示を更新して文字同士の重なりを防ぐ。
        foreach (var popup in popups)
            if (popup != selected && popup.age < popup.lifetime && Mathf.Abs(popup.position.x - position.x) < 236 && Mathf.Abs(popup.position.y - position.y) < 72)
            { popup.age = 99; popup.root.gameObject.SetActive(false); }
        selected.position = position; selected.root.anchoredPosition = position;
        selected.noteId = id; selected.age = 0; selected.lifetime = lifetime;
        selected.title.text = title; selected.title.color = color;
        selected.detail.text = detail; selected.detail.color = new Color(.86f, .89f, .92f);
        selected.title.rectTransform.anchoredPosition = new Vector2(21, string.IsNullOrEmpty(detail) ? 0 : 12);
        selected.glyph.Set(kind, direction, color); selected.rail.color = color;
        selected.group.alpha = 1; selected.root.gameObject.SetActive(true);
        CountActive();
    }

    public static Vector2 PopupPosition(Vector2 viewport, Vector2 bounds)
    {
        float side = viewport.x < .5f ? -1 : 1;
        return new Vector2(Mathf.Clamp((viewport.x - .5f) * bounds.x + side * 100, -bounds.x * .5f + 132, bounds.x * .5f - 132),
            Mathf.Clamp((viewport.y - .5f) * bounds.y - 74, -bounds.y * .5f + 60, bounds.y * .5f - 220));
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
        expired.Clear(); foreach (var note in tracked) if (note == null || note.IsFinalized) expired.Add(note);
        foreach (var note in expired) Untrack(note);
        foreach (var popup in popups)
        {
            if (popup.age >= popup.lifetime) continue;
            popup.age += delta;
            float t = popup.age / popup.lifetime;
            popup.group.alpha = 1 - Mathf.InverseLerp(.55f, 1f, t);
            popup.root.anchoredPosition = popup.position + Vector2.up * (DisplaySettings.ReducedEffects ? 0 : Mathf.Min(1, t) * 14);
            if (t >= 1) popup.root.gameObject.SetActive(false);
        }
        CountActive(); UpdateFullCombo();
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
        foreach (var popup in popups) { popup.age = 99; popup.root.gameObject.SetActive(false); }
        CountActive();
    }
    void CountActive() { ActivePopupCount = 0; foreach (var popup in popups) if (popup.age < popup.lifetime) ActivePopupCount++; }
    void OnDisable()
    {
        foreach (var popup in popups) if (popup != null) { popup.age = 99; popup.root.gameObject.SetActive(false); }
        ActivePopupCount = 0;
    }
    void OnDestroy()
    {
        if (spawner != null) spawner.OnNoteSpawned -= Track;
        if (score != null) score.OnJudgment -= Scored;
        foreach (var note in tracked) if (note != null)
        { note.OnJudged -= Judged; note.OnMiss -= Missed; note.OnRejected -= Rejected; }
        tracked.Clear(); if (hud != null) hud.UseLocalJudgments = false;
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

