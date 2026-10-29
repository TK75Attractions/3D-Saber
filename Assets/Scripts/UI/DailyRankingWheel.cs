using TMPro;
using UnityEngine;
using UnityEngine.UI;

// 上位3位だけの数字リール。ResultRevealの共通時計で回し、確定後は元のランキング帯へ戻す。
public sealed class DailyRankingWheel : MonoBehaviour
{
    public const float StartTime = 4.72f;
    public const float StopTime = 6.30f;
    public const float ImpactTime = 6.48f;
    public const float EndTime = 7.65f;
    public const float TransferTime = 7.10f;
    public const float BackdropEndTime = 7.34f;
    // 何人でも、この順位から数を下げて止める(2026-10-22 ユーザーの依頼: 1人でも 100位あたりから回して1位の演出に)。
    public const int SpinFrom = 100;
    // 刻みの音の最短の間隔。100マスを回すと始めは1秒に約190マス進むので、唸りにならないよう間引く。
    public const float TickInterval = .045f;
    const float CellHeight = 146;
    readonly TextMeshProUGUI[] cells = new TextMeshProUGUI[5];
    CanvasGroup group, reelGroup, stampGroup;
    RectTransform reel, stamp, ring;
    Image ringImage, impactGlow;
    TextMeshProUGUI stateLabel, stampValue;
    CanvasGroup frameGroup;
    Image dim;
    int rank, count, steps, lastCell = -1;
    float lastTickTime = -1;
    Color accent;
    AudioSource audioSource;
    AudioClip tickSound, impactSound;
    bool impacted;

    public static DailyRankingWheel Create(Transform parent, int rank, int count, Color accent)
    {
        var root = new GameObject("DailyRankingWheel", typeof(RectTransform), typeof(CanvasGroup));
        root.transform.SetParent(parent, false);
        root.GetComponent<RectTransform>().sizeDelta = new Vector2(1920, 1080);
        var view = root.AddComponent<DailyRankingWheel>();
        view.rank = rank; view.count = Mathf.Max(1, count); view.accent = accent;
        view.steps = Mathf.Max(1, SpinFrom - rank);
        view.Build(); view.Tick(0, false);
        return view;
    }

    // 残りのマス数 remaining のとき、上下の位置 offset(0=中央)に出す数字。1位より上(0以下)は空ける。純関数。
    public static string CellText(int rank, int remaining, int offset)
    {
        int value = rank + remaining + offset;
        return value >= 1 ? value.ToString("N0") : "";
    }

    void Build()
    {
        group = GetComponent<CanvasGroup>();
        group.blocksRaycasts = group.interactable = false;
        dim = Image("Dim", transform, Vector2.zero, new Vector2(1920, 1080), new Color(.015f, .023f, .065f, .94f));
        var frame = Rect("Frame", transform, new Vector2(980, 530), new Vector2(0, 10));
        frameGroup = frame.gameObject.AddComponent<CanvasGroup>();
        Image("Panel", frame, Vector2.zero, new Vector2(980, 530), new Color(.026f, .043f, .095f));
        Image("Top", frame, new Vector2(0, 264), new Vector2(980, 3), accent);
        Image("Bottom", frame, new Vector2(0, -264), new Vector2(980, 3), accent);
        Label("Heading", frame, "通算ランキング", new Vector2(0, 211), new Vector2(900, 50), 35, accent);
        Label("Count", frame, count.ToString("N0") + " 回中", new Vector2(0, 153), new Vector2(880, 56), 43, Color.white);
        stateLabel = Label("State", frame, "RANKING", new Vector2(0, -203), new Vector2(890, 52), 32, accent);

        var mask = Rect("WheelMask", frame, new Vector2(720, 244), new Vector2(0, -12));
        mask.gameObject.AddComponent<RectMask2D>();
        reel = Rect("Reel", mask, new Vector2(720, 244), Vector2.zero);
        reelGroup = reel.gameObject.AddComponent<CanvasGroup>();
        for (int i = 0; i < cells.Length; i++)
            cells[i] = Label("Cell" + i, reel, "1", Vector2.zero, new Vector2(540, 144), 126, Color.white);
        Image("LeftNotch", frame, new Vector2(-390, -12), new Vector2(55, 4), accent);
        Image("RightNotch", frame, new Vector2(390, -12), new Vector2(55, 4), accent);

        impactGlow = Image("ImpactGlow", frame, new Vector2(0, -12), new Vector2(790, 420), Color.clear);
        impactGlow.sprite = UISkinKit.SoftGlow();
        ringImage = Image("ImpactRing", frame, new Vector2(0, -12), new Vector2(440, 350), Color.clear);
        ringImage.sprite = UISkinKit.CircleRing(); ring = ringImage.rectTransform;
        // 背景のフェードと切り離し、確定した数字そのものを下の欄へ運ぶ。
        stamp = Rect("Stamp", transform, new Vector2(720, 240), new Vector2(0, -2));
        stampGroup = stamp.gameObject.AddComponent<CanvasGroup>();
        stampValue = Label("Value", stamp, rank + "<size=64> 位</size>", Vector2.zero, new Vector2(720, 240), 206, accent);
        stampValue.enableAutoSizing = false;
        stampValue.raycastTarget = false;
        audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.playOnAwake = false; audioSource.spatialBlend = 0;
        // 「デン」は響1 でドンと同じ倍音型にした(FlourishSfx.DailyRankDen)。この画面の音は 22050Hz なので半分に間引く。
        tickSound = Sound(false);
        impactSound = ProceduralSfx.UseLegacyDon ? Sound(true)
            : ProceduralSfx.Clip("DailyRankDEN", Halve(FlourishSfx.Cached("DailyRankDen", FlourishSfx.DailyRankDen)), 22050);
    }

    // 回っている間(暗幕が出ている間)。実績の知らせ(AchievementToast)はこの間、新しいカードを出さない(刻みの音・ドンと鈴がぶつかるため)。
    public static bool Showing { get; private set; }

    public void Tick(float time, bool reduced)
    {
        bool visible = !reduced && time > StartTime && time < EndTime;
        Showing = visible;
        group.alpha = visible ? Mathf.Clamp01((time - StartTime) / .16f) : 0;
        float background = 1 - Mathf.InverseLerp(TransferTime, BackdropEndTime, time);
        frameGroup.alpha = background;
        dim.color = new Color(.015f, .023f, .065f, .94f * background);
        float p = Mathf.Clamp01((time - StartTime) / (StopTime - StartTime));
        float remaining = steps * Mathf.Pow(1 - p, 3);
        int cellIndex = Mathf.FloorToInt(remaining);
        float fraction = remaining - cellIndex;
        for (int i = 0; i < cells.Length; i++)
        {
            int offset = i - 2;
            float y = (offset - fraction) * CellHeight;
            string text = CellText(rank, cellIndex, offset);
            if (cells[i].text != text) cells[i].text = text;
            cells[i].rectTransform.anchoredPosition = new Vector2(0, y);
            // 円筒状のホイールに見えるよう上下の数字をつぶして暗くする。
            float edge = Mathf.Clamp01(Mathf.Abs(y) / 190);
            cells[i].rectTransform.localScale = new Vector3(1 - .12f * edge, 1 - .58f * edge, 1);
            cells[i].color = new Color(1, 1, 1, 1 - .88f * edge);
        }
        reelGroup.alpha = time < StopTime ? 1 : 0;
        float slam = Mathf.Clamp01((time - StopTime) / (ImpactTime - StopTime));
        float after = Mathf.Max(0, time - ImpactTime);
        stampGroup.alpha = time >= StopTime ? 1 : 0;
        float scale = time < ImpactTime ? Mathf.Lerp(1.65f, 1, slam * slam)
            : 1 + .11f * Mathf.Sin(Mathf.Clamp01(after / .32f) * Mathf.PI) * Mathf.Clamp01(1 - after / .32f);
        float travel = Mathf.InverseLerp(TransferTime, EndTime, time);
        stamp.localScale = Vector3.one * (time < TransferTime ? scale : 1);
        stamp.anchoredPosition = time < TransferTime
            ? new Vector2(0, -2 + (time >= ImpactTime ? Mathf.Sin(after * 66) * 8 * Mathf.Clamp01(1 - after / .22f) : 0))
            : TransferPosition(travel);
        stamp.sizeDelta = Vector2.Lerp(new Vector2(720, 240), new Vector2(310, 94), travel);
        stampValue.rectTransform.sizeDelta = stamp.sizeDelta;
        stampValue.fontSize = Mathf.Lerp(206, 80, travel);
        string suffix = Mathf.Lerp(64, 34, travel).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        stampValue.text = rank + "<size=" + suffix + "> 位</size>";
        stateLabel.text = time < StopTime ? "RANKING" : rank == 1 ? "通算 第1位！" : "TOP 3  ランクイン！";
        float burst = time >= ImpactTime ? Mathf.Clamp01(1 - after / .6f) : 0;
        impactGlow.color = new Color(accent.r, accent.g, accent.b, burst * .6f);
        ringImage.color = new Color(accent.r, accent.g, accent.b, burst * .85f);
        ring.localScale = Vector3.one * Mathf.Lerp(.8f, 2.05f, Mathf.Clamp01(after / .6f));
        if (time < lastTickTime) lastTickTime = -1;   // スキップや手動の巻き戻し
        if (visible && time < StopTime && cellIndex != lastCell && time - lastTickTime >= TickInterval)
        {
            // 作った音は鳴らす前に中身を確かめる(音の出力先が変わると消えるため。ProceduralSfx.Revive)。
            audioSource.PlayOneShot(ProceduralSfx.Revive(ref tickSound), .16f);
            lastTickTime = time;
        }
        lastCell = cellIndex;
        if (!impacted && time >= ImpactTime)
        {
            impacted = true;
            if (visible && time < ImpactTime + .3f) audioSource.PlayOneShot(ProceduralSfx.Revive(ref impactSound), .65f);
        }
        // スキップ、LOW切替、終了では回転音や衝撃音を残さない。
        if (!visible) audioSource.Stop();
    }

    // 1920×1080 の左上原点の二次ベジェを、中央原点のUI座標へ変換する。
    public static Vector2 TransferPosition(float progress)
    {
        float t = Mathf.Clamp01(progress), u = 1 - t;
        Vector2 point = u * u * new Vector2(960, 542)
            + 2 * u * t * new Vector2(820, 809) + t * t * new Vector2(535, 809);
        return new Vector2(point.x - 960, 540 - point.y);
    }

    void OnDestroy()
    {
        Showing = false;
        if (tickSound != null) Destroy(tickSound);
        if (impactSound != null) Destroy(impactSound);
    }

    // 44100Hz の波形を、隣り合う2つの平均で 22050Hz にする。
    static float[] Halve(float[] samples)
    {
        var half = new float[Mathf.Max(1, samples.Length / 2)];
        for (int i = 0; i < half.Length; i++) half[i] = (samples[2 * i] + samples[Mathf.Min(samples.Length - 1, 2 * i + 1)]) * .5f;
        return half;
    }

    static AudioClip Sound(bool impact)
    {
        const int rate = 22050;
        int length = (int)(rate * (impact ? .65f : .025f));
        var data = new float[length];
        float phase = 0;
        for (int i = 0; i < length; i++)
        {
            float t = i / (float)rate, p = i / (float)length;
            phase += 2 * Mathf.PI * (impact ? Mathf.Lerp(150, 62, Mathf.Clamp01(t / .16f)) : 1450) / rate;
            float envelope = Mathf.Min(1, t / .002f) * Mathf.Pow(1 - p, impact ? 2.4f : 3);
            data[i] = envelope * (impact ? .52f * Mathf.Sin(phase) + .12f * Mathf.Sin(2 * Mathf.PI * 440 * t) + .06f * Mathf.Sin(2 * Mathf.PI * 660 * t) : .38f * Mathf.Sin(phase));
        }
        return ProceduralSfx.Clip(impact ? "DailyRankDEN" : "DailyRankWheelTick", data, rate);
    }

    static RectTransform Rect(string name, Transform parent, Vector2 size, Vector2 pos)
    {
        var root = new GameObject(name, typeof(RectTransform)); root.transform.SetParent(parent, false);
        var rect = root.GetComponent<RectTransform>(); rect.sizeDelta = size; rect.anchoredPosition = pos; return rect;
    }
    static Image Image(string name, Transform parent, Vector2 pos, Vector2 size, Color color)
    {
        var rect = Rect(name, parent, size, pos); var image = rect.gameObject.AddComponent<Image>();
        image.color = color; image.raycastTarget = false; return image;
    }
    static TextMeshProUGUI Label(string name, Transform parent, string text, Vector2 pos, Vector2 size, float fontSize, Color color)
    {
        var label = UISkinKit.MakeTMP(parent, name, text, fontSize, color, TextAlignmentOptions.Center,
            pos, size, FontStyles.Normal, 0, UISkinKit.FontAsset("Oxanium-Bold"));
        label.enableAutoSizing = true; label.fontSizeMin = fontSize * .55f; label.fontSizeMax = fontSize; return label;
    }
}
