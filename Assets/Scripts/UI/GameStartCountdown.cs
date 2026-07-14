using TMPro;
using UnityEngine;
using UnityEngine.UI;

// 楽曲開始前の 3 拍カウントイン。
// 表示・クリック音・楽曲開始をすべて AudioSettings.dspTime に揃え、フレーム落ちによる同期ずれを防ぐ。
public class GameStartCountdown : MonoBehaviour
{
    private const float DefaultBpm = 120f;
    private const double ScheduleLeadSeconds = 0.12;
    private const double StartHoldSeconds = 0.08;
    private const double FadeSeconds = 0.28;

    private static readonly string[] Tokens = { "3", "2", "1", "START!" };
    private static AudioClip[] cueClips;

    private CanvasGroup canvasGroup;
    private RectTransform pulseRoot;
    private RectTransform diamondFrame;
    private Image diamondImage;
    private Image pulseGlow;
    private TextMeshProUGUI countText;
    private TextMeshProUGUI echoLeft;
    private TextMeshProUGUI echoRight;
    private TextMeshProUGUI bpmText;
    private TextMeshProUGUI statusText;
    private readonly Image[] beatMarkers = new Image[3];

    private Color accent;
    private double firstBeatDspTime;
    private double songStartDspTime;
    private double beatSeconds;
    private int shownStep = -1;
    private bool running;
    private bool built;

    public bool IsRunning => running;
    public double SongStartDspTime => songStartDspTime;

    public static GameStartCountdown Ensure()
    {
        var existing = Object.FindFirstObjectByType<GameStartCountdown>(FindObjectsInactive.Include);
        if (existing != null) return existing;

        var go = new GameObject("GameStartCountdown", typeof(RectTransform));
        var countdown = go.AddComponent<GameStartCountdown>();
        countdown.Build();
        return countdown;
    }

    // カウントを開始し、START! と一致する楽曲開始用 DSP 時刻を返す。
    public double Begin(float bpm, string difficulty, float volume = 0.55f)
    {
        Build();
        beatSeconds = BeatSeconds(bpm);
        accent = AccentForDifficulty(difficulty);
        firstBeatDspTime = AudioSettings.dspTime + ScheduleLeadSeconds;
        songStartDspTime = firstBeatDspTime + beatSeconds * 3.0;
        shownStep = -1;
        running = true;
        canvasGroup.alpha = 1f;
        canvasGroup.blocksRaycasts = false;

        bpmText.text = $"{SanitizeBpm(bpm):0} BPM  //  3 BEAT COUNT-IN";
        ApplyStep(0);
        ScheduleCues(Mathf.Clamp01(volume));
        return songStartDspTime;
    }

    public static float SanitizeBpm(float bpm)
    {
        return float.IsNaN(bpm) || float.IsInfinity(bpm) || bpm <= 0f ? DefaultBpm : bpm;
    }

    public static double BeatSeconds(float bpm)
    {
        return 60.0 / SanitizeBpm(bpm);
    }

    public static int StepAt(double dspTime, double firstBeatTime, double secondsPerBeat)
    {
        if (secondsPerBeat <= 0.0) return 0;
        int step = Mathf.FloorToInt((float)((dspTime - firstBeatTime) / secondsPerBeat));
        return Mathf.Clamp(step, 0, 3);
    }

    public static string TokenForStep(int step)
    {
        return Tokens[Mathf.Clamp(step, 0, Tokens.Length - 1)];
    }

    public static Color AccentForDifficulty(string difficulty)
    {
        string d = string.IsNullOrEmpty(difficulty) ? "" : difficulty.ToLowerInvariant();
        if (d == "easy") return UISkinPalette.LogoGreen;
        if (d == "hard") return UISkinPalette.LogoRed;
        return UISkinPalette.LogoBlue;
    }

    private void Build()
    {
        if (built) return;
        built = true;

        var canvas = gameObject.GetComponent<Canvas>();
        if (canvas == null) canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 900;

        var scaler = gameObject.GetComponent<CanvasScaler>();
        if (scaler == null) scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        canvasGroup = gameObject.GetComponent<CanvasGroup>();
        if (canvasGroup == null) canvasGroup = gameObject.AddComponent<CanvasGroup>();
        canvasGroup.interactable = false;
        canvasGroup.blocksRaycasts = false;

        // ステージを見失わない程度に暗くし、既存 HUD の情報量だけを抑える。
        var veil = MakeImage(transform, "StageVeil", null, new Color(0.008f, 0.012f, 0.035f, 0.66f));
        Stretch(veil.rectTransform);

        var centerShade = MakeImage(transform, "CenterShade", UISkinKit.SoftGlow(),
            new Color(0.01f, 0.025f, 0.07f, 0.82f));
        AnchorCenter(centerShade.rectTransform, Vector2.zero, new Vector2(1040f, 760f));

        // 左右の色を分けた細い同期ライン。タイトルから続く赤・青の識別色を控えめに使う。
        MakeRail(transform, "RedSyncRail", new Vector2(-480f, 0f), new Vector2(760f, 3f),
            new Color(UISkinPalette.LogoRed.r, UISkinPalette.LogoRed.g, UISkinPalette.LogoRed.b, 0.34f));
        MakeRail(transform, "BlueSyncRail", new Vector2(480f, 0f), new Vector2(760f, 3f),
            new Color(UISkinPalette.LogoBlue.r, UISkinPalette.LogoBlue.g, UISkinPalette.LogoBlue.b, 0.34f));

        var rootGo = new GameObject("PulseRoot", typeof(RectTransform));
        rootGo.transform.SetParent(transform, false);
        pulseRoot = rootGo.GetComponent<RectTransform>();
        AnchorCenter(pulseRoot, Vector2.zero, new Vector2(760f, 760f));

        pulseGlow = MakeImage(pulseRoot, "PulseGlow", UISkinKit.SoftGlow(), Color.clear);
        AnchorCenter(pulseGlow.rectTransform, Vector2.zero, new Vector2(540f, 540f));

        var diamondGo = new GameObject("DiamondFrame", typeof(RectTransform), typeof(Image));
        diamondGo.transform.SetParent(pulseRoot, false);
        diamondFrame = diamondGo.GetComponent<RectTransform>();
        AnchorCenter(diamondFrame, new Vector2(0f, 4f), new Vector2(270f, 270f));
        diamondFrame.localRotation = Quaternion.Euler(0f, 0f, 45f);
        diamondImage = diamondGo.GetComponent<Image>();
        diamondImage.sprite = UISkinKit.RoundedFrame();
        diamondImage.type = Image.Type.Sliced;
        diamondImage.raycastTarget = false;

        // ダイヤの四隅から伸びる短い照準線。動くノーツより細くして視線を奪いすぎない。
        MakeRail(pulseRoot, "TopTick", new Vector2(0f, 210f), new Vector2(3f, 70f), Color.white);
        MakeRail(pulseRoot, "BottomTick", new Vector2(0f, -202f), new Vector2(3f, 70f), Color.white);
        MakeRail(pulseRoot, "LeftTick", new Vector2(-206f, 4f), new Vector2(70f, 3f), Color.white);
        MakeRail(pulseRoot, "RightTick", new Vector2(206f, 4f), new Vector2(70f, 3f), Color.white);

        var logoFont = UISkinKit.LogoFontAsset();
        var numberFont = UISkinKit.FontAsset("Oxanium-ExtraBold");
        if (numberFont == null) numberFont = UISkinKit.FontAsset("Rajdhani-Bold");
        if (numberFont == null) numberFont = logoFont;

        statusText = UISkinKit.MakeTMP(pulseRoot, "Status", "RHYTHM LOCK", 24f,
            UISkinPalette.OffWhite, TextAlignmentOptions.Center,
            new Vector2(0f, 334f), new Vector2(520f, 38f), FontStyles.Normal, 8f, logoFont);

        bpmText = UISkinKit.MakeTMP(pulseRoot, "Bpm", "120 BPM  //  3 BEAT COUNT-IN", 17f,
            UISkinPalette.SubtleGray, TextAlignmentOptions.Center,
            new Vector2(0f, 294f), new Vector2(600f, 30f), FontStyles.Normal, 4f, logoFont);

        echoLeft = MakeCountText(pulseRoot, "EchoRed", numberFont, new Vector2(-8f, 8f));
        echoRight = MakeCountText(pulseRoot, "EchoBlue", numberFont, new Vector2(8f, 0f));
        countText = MakeCountText(pulseRoot, "Count", numberFont, new Vector2(0f, 4f));

        var follow = UISkinKit.MakeTMP(pulseRoot, "Follow", "FOLLOW THE PULSE", 18f,
            UISkinPalette.SubtleGray, TextAlignmentOptions.Center,
            new Vector2(0f, -236f), new Vector2(420f, 30f), FontStyles.Normal, 7f, logoFont);
        follow.color = new Color(follow.color.r, follow.color.g, follow.color.b, 0.85f);

        for (int i = 0; i < beatMarkers.Length; i++)
        {
            beatMarkers[i] = MakeImage(pulseRoot, $"Beat{i + 1}", UISkinKit.RoundedRect(), Color.white);
            AnchorCenter(beatMarkers[i].rectTransform, new Vector2((i - 1) * 60f, -286f), new Vector2(42f, 6f));
        }
    }

    private TextMeshProUGUI MakeCountText(Transform parent, string name, TMP_FontAsset font, Vector2 position)
    {
        return UISkinKit.MakeTMP(parent, name, "3", 188f, Color.white,
            TextAlignmentOptions.Center, position, new Vector2(720f, 230f),
            FontStyles.Normal, 0f, font);
    }

    private void Update()
    {
        if (!running) return;

        double now = AudioSettings.dspTime;
        int step = StepAt(now, firstBeatDspTime, beatSeconds);
        if (step != shownStep) ApplyStep(step);

        double stepStart = firstBeatDspTime + step * beatSeconds;
        float phase = step < 3
            ? Mathf.Clamp01((float)((now - stepStart) / beatSeconds))
            : Mathf.Clamp01((float)((now - songStartDspTime) / 0.22));
        AnimatePulse(step, phase);

        if (now < songStartDspTime + StartHoldSeconds) return;
        float fade = Mathf.Clamp01((float)((now - songStartDspTime - StartHoldSeconds) / FadeSeconds));
        canvasGroup.alpha = 1f - fade * fade * (3f - 2f * fade);
        if (fade >= 1f)
        {
            running = false;
            Destroy(gameObject);
        }
    }

    private void ApplyStep(int step)
    {
        shownStep = Mathf.Clamp(step, 0, 3);
        string token = TokenForStep(shownStep);
        countText.text = token;
        echoLeft.text = token;
        echoRight.text = token;

        bool start = shownStep == 3;
        float size = start ? 92f : 188f;
        countText.fontSize = size;
        echoLeft.fontSize = size;
        echoRight.fontSize = size;
        statusText.text = start ? "TRACE ONLINE" : "RHYTHM LOCK";

        countText.color = start ? Color.white : Color.Lerp(accent, Color.white, 0.18f);
        echoLeft.color = new Color(UISkinPalette.LogoRed.r, UISkinPalette.LogoRed.g,
            UISkinPalette.LogoRed.b, start ? 0.30f : 0.18f);
        echoRight.color = new Color(UISkinPalette.LogoBlue.r, UISkinPalette.LogoBlue.g,
            UISkinPalette.LogoBlue.b, start ? 0.30f : 0.18f);
        diamondImage.color = new Color(accent.r, accent.g, accent.b, start ? 0.95f : 0.72f);

        for (int i = 0; i < beatMarkers.Length; i++)
        {
            bool active = start || i == shownStep;
            bool passed = i < shownStep;
            float alpha = active ? 0.95f : passed ? 0.32f : 0.13f;
            Color c = active ? accent : UISkinPalette.OffWhite;
            beatMarkers[i].color = new Color(c.r, c.g, c.b, alpha);
            beatMarkers[i].rectTransform.sizeDelta = active ? new Vector2(42f, 6f) : new Vector2(28f, 4f);
        }
    }

    private void AnimatePulse(int step, float phase)
    {
        float attack = 1f - Mathf.Pow(1f - Mathf.Clamp01(phase / 0.30f), 3f);
        float scale = step == 3
            ? Mathf.Lerp(0.82f, 1f, attack)
            : Mathf.Lerp(1.28f, 1f, attack);
        pulseRoot.localScale = Vector3.one * scale;

        float glowStrength = step == 3
            ? Mathf.Lerp(0.58f, 0.25f, phase)
            : Mathf.Lerp(0.46f, 0.10f, phase);
        pulseGlow.color = new Color(accent.r, accent.g, accent.b, glowStrength);

        float diamondPulse = 1f + (1f - attack) * (step == 3 ? 0.13f : 0.08f);
        diamondFrame.localScale = Vector3.one * diamondPulse;
        diamondFrame.localRotation = Quaternion.Euler(0f, 0f, 45f + phase * 2f);
    }

    private void ScheduleCues(float volume)
    {
        EnsureCueClips();
        for (int i = 0; i < cueClips.Length; i++)
        {
            var src = gameObject.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.loop = false;
            src.spatialBlend = 0f;
            src.volume = volume * (i == 3 ? 0.9f : 0.68f);
            src.clip = cueClips[i];
            src.PlayScheduled(firstBeatDspTime + beatSeconds * i);
        }
    }

    private static void EnsureCueClips()
    {
        if (cueClips != null) return;
        cueClips = new[]
        {
            BuildCueClip("CountIn3", 620f, false),
            BuildCueClip("CountIn2", 720f, false),
            BuildCueClip("CountIn1", 880f, false),
            BuildCueClip("CountInStart", 1040f, true)
        };
    }

    private static AudioClip BuildCueClip(string name, float frequency, bool startCue)
    {
        const int sampleRate = 48000;
        float duration = startCue ? 0.22f : 0.10f;
        int sampleCount = Mathf.CeilToInt(sampleRate * duration);
        var samples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            float t = i / (float)sampleRate;
            float envelope = Mathf.Exp(-(startCue ? 13f : 32f) * t);
            float tone = Mathf.Sin(2f * Mathf.PI * frequency * t);
            tone += 0.36f * Mathf.Sin(2f * Mathf.PI * frequency * 1.5f * t);
            if (startCue) tone += 0.24f * Mathf.Sin(2f * Mathf.PI * frequency * 2f * t);
            samples[i] = Mathf.Clamp(tone * envelope * (startCue ? 0.42f : 0.34f), -1f, 1f);
        }

        var clip = AudioClip.Create(name, sampleCount, 1, sampleRate, false);
        clip.SetData(samples, 0);
        clip.hideFlags = HideFlags.HideAndDontSave;
        return clip;
    }

    private static Image MakeImage(Transform parent, string name, Sprite sprite, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var image = go.GetComponent<Image>();
        image.sprite = sprite;
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    private static void MakeRail(Transform parent, string name, Vector2 pos, Vector2 size, Color color)
    {
        var rail = MakeImage(parent, name, UISkinKit.RoundedRect(), color);
        AnchorCenter(rail.rectTransform, pos, size);
    }

    private static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    private static void AnchorCenter(RectTransform rt, Vector2 position, Vector2 size)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = position;
        rt.sizeDelta = size;
    }
}
