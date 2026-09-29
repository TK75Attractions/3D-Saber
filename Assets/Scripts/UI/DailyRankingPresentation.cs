using TMPro;
using UnityEngine;
using UnityEngine.UI;

// 既存ResultRevealの時計/スキップに同期。追加のUpdateや入力経路は持たない。
public sealed class DailyRankingPresentation : MonoBehaviour
{
    public const float PanelTime = 2.42f;
    public const float RankTime = 2.78f;
    public const float LandTime = 3.08f;
    public const float EndTime = 3.58f;
    ResultReveal reveal;
    CanvasGroup numberGroup, detailGroup;
    RectTransform number, sweep;
    Image sweepImage, glow;
    Color accent;
    bool available;
    AudioSource audioSource;
    AudioClip chime;
    bool landed;
    DailyRankingWheel wheel;

    public static float CompletionTime(DailyRankingStore.Result result)
        => result != null && result.Available && result.Rank <= 3 && !DisplaySettings.ReducedEffects
            ? DailyRankingWheel.EndTime : EndTime;

    public static DailyRankingPresentation Build(Transform parent, ResultReveal reveal, DailyRankingStore.Result result)
    {
        var root = new GameObject("DailyRanking", typeof(RectTransform), typeof(CanvasGroup));
        root.transform.SetParent(parent, false);
        var rt = root.GetComponent<RectTransform>();
        rt.anchoredPosition = new Vector2(0, -269);
        rt.sizeDelta = new Vector2(1520, 116);
        var group = root.GetComponent<CanvasGroup>();
        group.interactable = group.blocksRaycasts = false;
        var view = root.AddComponent<DailyRankingPresentation>();
        view.reveal = reveal;
        view.available = result != null && result.Available;
        view.accent = Accent(view.available ? result.Rank : 0);
        view.BuildContents(result);
        if (view.available && result.Rank <= 3)
            view.wheel = DailyRankingWheel.Create(parent, result.Rank, result.TotalPlays, view.accent);
        if (view.available)
        {
            view.audioSource = root.AddComponent<AudioSource>();
            view.audioSource.playOnAwake = false;
            view.audioSource.spatialBlend = 0;
            view.chime = CreateChime(result.Rank <= 3);
        }
        reveal.Add(root, PanelTime, .3f, ResultReveal.Kind.Slide,
            DisplaySettings.ReducedEffects ? Vector2.zero : new Vector2(0, -20));
        reveal.TimeChanged += view.Tick;
        view.Tick(0);
        return view;
    }

    public static Color Accent(int rank)
    {
        if (rank == 1) return new Color(1f, .79f, .31f);
        if (rank == 2) return new Color(.78f, .89f, 1f);
        if (rank == 3) return new Color(1f, .62f, .38f);
        return UISkinPalette.Cyan;
    }

    void BuildContents(DailyRankingStore.Result result)
    {
        Box("Panel", transform, Vector2.zero, new Vector2(1520, 116), new Color(.025f, .041f, .10f, .96f));
        Box("TopEdge", transform, new Vector2(0, 57), new Vector2(1520, 2), accent * new Color(1, 1, 1, .52f));
        Box("BottomEdge", transform, new Vector2(0, -57), new Vector2(1520, 2), accent * new Color(1, 1, 1, .2f));
        Box("Accent", transform, new Vector2(-757, 0), new Vector2(6, 116), accent);
        Text("Heading", "本日", new Vector2(-652, 10), new Vector2(155, 47), 36, accent);
        Text("Eyebrow", "DAILY RANK", new Vector2(-650, -29), new Vector2(155, 24), 17, UISkinPalette.SubtleGray);

        var numberRoot = new GameObject("Rank", typeof(RectTransform), typeof(CanvasGroup));
        numberRoot.transform.SetParent(transform, false);
        number = numberRoot.GetComponent<RectTransform>();
        number.anchoredPosition = new Vector2(-425, 0);
        number.sizeDelta = new Vector2(310, 104);
        numberGroup = numberRoot.GetComponent<CanvasGroup>();
        numberGroup.blocksRaycasts = numberGroup.interactable = false;
        var value = Text("Value", available ? result.Rank.ToString("N0") + "<size=34> 位</size>" : "—",
            Vector2.zero, new Vector2(310, 94), 80, accent, number);
        value.enableAutoSizing = true;
        value.fontSizeMin = 36;
        value.fontSizeMax = 80;

        Box("Divider", transform, new Vector2(-245, 0), new Vector2(1, 70), new Color(.23f, .3f, .42f));
        var details = new GameObject("Details", typeof(RectTransform), typeof(CanvasGroup));
        details.transform.SetParent(transform, false);
        detailGroup = details.GetComponent<CanvasGroup>();
        detailGroup.interactable = detailGroup.blocksRaycasts = false;
        string headline = !available ? (result == null ? "プレイ終了後に表示" : "本日の順位を取得できませんでした")
            : result.TotalPlays == 1 ? "本日最初の記録！"
            : result.IsNewBest ? "本日のトップスコア更新！"
            : result.Rank == 1 ? "本日のトップスコア！"
            : result.Rank <= 3 ? "本日の TOP 3 にランクイン！" : "ランクイン！";
        var status = Text("Status", headline, new Vector2(30, 19), new Vector2(500, 42), 29, accent, details.transform);
        status.alignment = TextAlignmentOptions.MidlineLeft;
        string note = !available ? "次のプレイでもう一度チャレンジ"
            : result.PointsToNext > 0 ? "ひとつ上の順位まで あと " + result.PointsToNext.ToString("N0") + " 点"
            : result.TiedPlays > 1 ? "同点 " + result.TiedPlays.ToString("N0") + " 人で同率1位" : "この調子で次のプレイへ！";
        if (available && result.TiedPlays > 1 && result.Rank > 1) note = "同率 " + result.Rank + " 位  /  " + note;
        var sub = Text("Next", note, new Vector2(65, -23), new Vector2(570, 32), 22, UISkinPalette.OffWhite, details.transform);
        sub.alignment = TextAlignmentOptions.MidlineLeft;
        string count = available ? result.TotalPlays.ToString("N0") + " 人中" : "DAILY RANKING";
        var people = Text("Count", count, new Vector2(555, 12), new Vector2(350, 52), 38, UISkinPalette.OffWhite);
        people.enableAutoSizing = true; people.fontSizeMin = 22; people.fontSizeMax = 38;
        Text("Date", available ? result.Day.Replace('-', '/') : "", new Vector2(555, -28), new Vector2(350, 26), 18, UISkinPalette.SubtleGray);

        glow = Box("LandingGlow", transform, new Vector2(-425, 0), new Vector2(460, 114), Color.clear);
        glow.sprite = UISkinKit.SoftGlow();
        glow.transform.SetSiblingIndex(1);
        sweepImage = Box("Sweep", transform, new Vector2(-750, 55), new Vector2(80, 3), Color.clear);
        sweep = sweepImage.rectTransform;
    }

    public void Tick(float time)
    {
        bool low = DisplaySettings.ReducedEffects;
        bool special = wheel != null && !low;
        float landing = special ? DailyRankingWheel.ImpactTime : LandTime;
        float arrival = special ? DailyRankingWheel.StopTime : RankTime;
        if (wheel != null) wheel.Tick(time, low);
        if (!landed && time >= landing)
        {
            landed = true;
            // スキップで効果音が遅れて鳴らない。手動Tickの巻き戻しでも再生し直さない。
            if (!special && time < EndTime && audioSource != null && chime != null) audioSource.PlayOneShot(chime, .3f);
        }
        float p = Mathf.Clamp01((time - arrival) / (landing - arrival));
        numberGroup.alpha = p;
        float settle = Mathf.Clamp01((time - landing) / .34f);
        float scale = low ? 1 : (p < 1 ? Mathf.Lerp(1.32f, 1, p * p) : 1 + .065f * Mathf.Sin(settle * Mathf.PI) * (1 - settle));
        number.localScale = Vector3.one * scale;
        detailGroup.alpha = Mathf.Clamp01((time - landing) / .22f);
        float flash = low || !available ? 0 : Mathf.Clamp01(1 - (time - landing) / .48f);
        if (time < landing) flash = 0;
        glow.color = new Color(accent.r, accent.g, accent.b, flash * .36f);
        float sweepProgress = Mathf.Clamp01((time - landing) / .5f);
        sweep.anchoredPosition = new Vector2(Mathf.Lerp(-710, 710, sweepProgress), 55);
        sweepImage.color = new Color(accent.r, accent.g, accent.b, low ? 0 : Mathf.Sin(sweepProgress * Mathf.PI));
    }

    void OnDestroy()
    {
        if (reveal != null) reveal.TimeChanged -= Tick;
        if (chime != null) Destroy(chime);
        if (wheel != null) Destroy(wheel.gameObject);
    }

    static AudioClip CreateChime(bool podium)
    {
        const int rate = 22050;
        var samples = new float[(int)(rate * .34f)];
        float root = podium ? 880 : 660;
        for (int i = 0; i < samples.Length; i++)
        {
            float t = i / (float)rate;
            float envelope = Mathf.Min(t / .008f, 1) * Mathf.Pow(1 - i / (float)samples.Length, 3);
            samples[i] = envelope * .24f * (Mathf.Sin(2 * Mathf.PI * root * t)
                + .5f * Mathf.Sin(2 * Mathf.PI * root * 1.25f * t) + .25f * Mathf.Sin(2 * Mathf.PI * root * 1.5f * t));
        }
        var clip = AudioClip.Create("DailyRankChime", samples.Length, 1, rate, false);
        clip.SetData(samples, 0);
        return clip;
    }

    TextMeshProUGUI Text(string name, string text, Vector2 pos, Vector2 size, float fontSize, Color color, Transform parent = null)
    {
        return UISkinKit.MakeTMP(parent != null ? parent : transform, name, text, fontSize, color,
            TextAlignmentOptions.Center, pos, size, FontStyles.Normal, 0, UISkinKit.FontAsset("Oxanium-Bold"));
    }

    static Image Box(string name, Transform parent, Vector2 pos, Vector2 size, Color color)
    {
        var root = new GameObject(name, typeof(RectTransform), typeof(Image));
        root.transform.SetParent(parent, false);
        var image = root.GetComponent<Image>();
        image.rectTransform.anchoredPosition = pos;
        image.rectTransform.sizeDelta = size;
        image.color = color;
        image.raycastTarget = false;
        return image;
    }
}
