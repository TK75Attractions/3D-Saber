using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// 楽曲開始前の 3 拍カウントイン。
// 表示・クリック音・楽曲開始をすべて AudioSettings.dspTime に揃え、フレーム落ちによる同期ずれを防ぐ。
// 見た目は 2026-10-05 の Web 試作(カウントイン試作)の案A「斬る数字」に、案B の「拍ごとに点くゲートの辺」を足したもの。
//   3: 大きな「3」(画面の高さの約37%)。ゲートの左の辺と床の左半分が青く点く。
//   2: 青い斬撃が「3」を斬って2つに割る。「2」が出て、右の辺と床の右半分が赤く点く。
//   1: 赤い斬撃が「2」を斬る。「1」が出て、上下の辺と四隅が白く点く。
//   START: 青と赤の X で「1」が4つに割れ、両端からの斬撃が中央で出会って「START」。ゲート全体が難易度の色に光り、
//          ゲートからリング、奥から光の帯。最初のノーツがゲートに着く約0.3秒前までに START も横に斬られて消える。
// 全画面の暗幕・ひし形の枠・英語の小さな文字は使わない。1カウントは曲の拍(0.45秒より短い曲は2拍)。
public class GameStartCountdown : MonoBehaviour
{
    private const float DefaultBpm = 120f;
    private const double ScheduleLeadSeconds = 0.12;
    // 1カウントの最短。光る回数を1秒2.2回までに抑える(点滅の目安は1秒3回)。
    public const double MinCountSeconds = 0.45;
    // 1920×1080 基準の大きさ
    private const float NumberSize = 400f;
    private const float StartSize = 320f;
    private const float CutSeconds = .36f;
    private const float SlashSweep = .05f;
    private const float SlashFade = .14f;

    private static readonly string[] Tokens = { "3", "2", "1", "START" };
    private static AudioClip tickClip, swishClip, startClip;

    private Canvas canvas;
    private RectTransform canvasRect;
    private CanvasGroup canvasGroup;
    private RectTransform center;
    private readonly CountInCutText[] numbers = new CountInCutText[3];
    private CountInCutText startWord;
    private readonly List<Material> materials = new List<Material>();
    private Material accentMaterial;
    private readonly List<SlashLine> slashes = new List<SlashLine>();
    private readonly List<Image> telegraphs = new List<Image>();
    private readonly List<Image> sparks = new List<Image>();
    private readonly List<Image> streakImages = new List<Image>();
    private readonly Image[] ringA = new Image[4];
    private readonly Image[] ringB = new Image[4];
    private CountInGateCue gateCue;
    private Transform gateTransform;
    private float spawnZ = 20f;
    private Vector2 ringHalf = new Vector2(330f, 165f);
    private Vector2 vanishing = new Vector2(0f, 190f);

    private Color accent;
    private float power = .8f;
    private bool reduced;
    private double firstBeatDspTime;
    private double songStartDspTime;
    private double countSeconds;
    private double startHold;
    private double endDspTime;
    private bool running;
    private bool built;

    public bool IsRunning => running;
    public double SongStartDspTime => songStartDspTime;
    public double FirstBeatDspTime => firstBeatDspTime;
    public double CountLength => countSeconds;
    public double StartHold => startHold;
    public double EndDspTime => endDspTime;
    public CountInGateCue GateCue => gateCue;
    public CountInCutText NumberAt(int index) => numbers[Mathf.Clamp(index, 0, numbers.Length - 1)];
    public CountInCutText StartWord => startWord;
    public int ActiveSparkCount { get { int n = 0; foreach (var s in sparks) if (s.gameObject.activeSelf) n++; return n; } }

    public static GameStartCountdown Ensure()
    {
        var existing = Object.FindFirstObjectByType<GameStartCountdown>(FindObjectsInactive.Include);
        if (existing != null) return existing;

        var go = new GameObject("GameStartCountdown", typeof(RectTransform));
        var countdown = go.AddComponent<GameStartCountdown>();
        countdown.Build();
        return countdown;
    }

    // カウントを開始し、START と一致する楽曲開始用 DSP 時刻を返す。
    // firstNoteSeconds は最初のノーツが判定ゲートに着く曲時刻(START の文字をそれより前に消すため)。
    public double Begin(float bpm, string difficulty, float volume = 0.55f, double minimumLeadSeconds = 0.0,
        double firstNoteSeconds = double.NaN)
    {
        Build();
        countSeconds = CountSeconds(bpm);
        accent = AccentForDifficulty(difficulty);
        string d = string.IsNullOrEmpty(difficulty) ? "" : difficulty.ToLowerInvariant();
        power = d == "easy" ? .55f : d == "hard" ? 1f : .8f;
        reduced = DisplaySettings.ReducedEffects;
        startHold = StartHoldSeconds(firstNoteSeconds);
        // 低速ノーツの先読みが3カウントより長ければ、カウントの前に待ち時間を足す。
        // 3→2→1→STARTの間隔と、STARTと楽曲の同期は維持する。
        firstBeatDspTime = AudioSettings.dspTime + ScheduleLeadSeconds
            + System.Math.Max(0.0, minimumLeadSeconds - countSeconds * 3.0);
        songStartDspTime = firstBeatDspTime + countSeconds * 3.0;
        endDspTime = songStartDspTime + System.Math.Max(startHold + .4, CountInGateCue.RestoreFrom + CountInGateCue.RestoreSeconds);
        running = true;
        canvasGroup.alpha = 0f;
        canvasGroup.blocksRaycasts = false;
        ApplyAccent();
        BindStage();
        HideAll();
        if (Application.isPlaying) ScheduleCues(Mathf.Clamp01(volume));
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

    // 1カウントの長さ。曲の拍を使い、0.45秒より短い速い曲は2拍で1カウントにする(osu! のカウントダウンの倍速と同じ作法)。
    public static double CountSeconds(float bpm)
    {
        double count = BeatSeconds(bpm);
        while (count < MinCountSeconds) count *= 2.0;
        return count;
    }

    // START の文字を残す秒数。最初のノーツがゲートに着く0.3秒前までに斬って消す(0.22〜0.42秒)。
    public static double StartHoldSeconds(double firstNoteSeconds)
    {
        if (double.IsNaN(firstNoteSeconds) || double.IsInfinity(firstNoteSeconds)) return .42;
        return System.Math.Min(.42, System.Math.Max(.22, firstNoteSeconds - .3));
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

    // 数字を斬る線(数字の中心を通る)。3 は青い斬撃が左下から、2 は赤い斬撃が右下から、1 は青と赤の X。
    public static CountInCutText.Cut[] CutsFor(int number)
    {
        if (number == 0) return new[] { new CountInCutText.Cut(new Vector2(-490f, -190f), new Vector2(490f, 190f)) };
        if (number == 1) return new[] { new CountInCutText.Cut(new Vector2(490f, -190f), new Vector2(-490f, 190f)) };
        return new[]
        {
            new CountInCutText.Cut(new Vector2(-490f, -210f), new Vector2(490f, 210f)),
            new CountInCutText.Cut(new Vector2(490f, -210f), new Vector2(-490f, 210f))
        };
    }

    static Color CutColor(int number, int line) => number == 0 || (number == 2 && line == 0) ? UISkinPalette.LogoBlue : UISkinPalette.LogoRed;

    private void Build()
    {
        if (built) return;
        built = true;

        canvas = gameObject.GetComponent<Canvas>();
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
        canvasRect = GetComponent<RectTransform>();

        center = new GameObject("GateCenter", typeof(RectTransform)).GetComponent<RectTransform>();
        center.SetParent(transform, false);
        center.sizeDelta = Vector2.zero;
        center.anchoredPosition = new Vector2(0f, 12f);

        // 光の帯とリングは数字の後ろ
        for (int i = 0; i < 24; i++) streakImages.Add(MakeImage(center, "Streak" + i, UISkinKit.RoundedRect(), Color.clear, new Vector2(160f, 5f)));
        for (int i = 0; i < 4; i++)
        {
            ringA[i] = MakeImage(center, "RingA" + i, null, Color.clear, Vector2.one);
            ringB[i] = MakeImage(center, "RingB" + i, null, Color.clear, Vector2.one);
        }
        for (int i = 0; i < 2; i++)
        {
            var line = MakeImage(center, "Telegraph" + i, null, Color.clear, new Vector2(10f, 3f));
            line.rectTransform.pivot = new Vector2(0f, .5f);
            telegraphs.Add(line);
        }

        var font = UISkinKit.FontAsset("Oxanium-ExtraBold");
        if (font == null) font = UISkinKit.FontAsset("Oxanium-Bold");
        // 材質を複製する前に、使う文字を書体の画像へ入れておく
        if (font != null) font.TryAddCharacters("123START");
        Material blue = MakeNumberMaterial(font, UISkinPalette.LogoBlue, .55f);
        Material red = MakeNumberMaterial(font, UISkinPalette.LogoRed, .55f);
        Material white = MakeNumberMaterial(font, new Color(.75f, .88f, 1f), .45f);
        accentMaterial = MakeNumberMaterial(font, UISkinPalette.LogoBlue, .6f);
        var numberMaterials = new[] { blue, red, white };
        for (int k = 0; k < numbers.Length; k++)
        {
            numbers[k] = new CountInCutText(center, "Count" + Tokens[k], Tokens[k], NumberSize, font, numberMaterials[k], CutsFor(k), FontStyles.Normal);
        }
        numbers[0].SetGradient(Color.white, Color.Lerp(UISkinPalette.LogoBlue, Color.white, .42f));
        numbers[1].SetGradient(Color.white, Color.Lerp(UISkinPalette.LogoRed, Color.white, .42f));
        numbers[2].SetGradient(Color.white, new Color(.86f, .9f, 1f));
        startWord = new CountInCutText(center, "Start", Tokens[3], StartSize, font, accentMaterial,
            new[] { new CountInCutText.Cut(new Vector2(-1400f, 0f), new Vector2(1400f, 0f)) }, FontStyles.Italic);

        for (int i = 0; i < 6; i++) slashes.Add(new SlashLine(center, "Slash" + i));
        for (int i = 0; i < 40; i++) sparks.Add(MakeImage(center, "Spark" + i, null, Color.clear, new Vector2(9f, 9f)));
        HideAll();
    }

    // 芯は白に近く、色は外側の淡い光(下敷きの影を広げてぼかしたもの)で付ける。縁は暗くして投影でも読めるようにする。
    private Material MakeNumberMaterial(TMP_FontAsset font, Color glow, float glowAlpha)
    {
        if (font == null || font.material == null) return null;
        ShaderUtilities.GetShaderPropertyIDs();
        var m = new Material(font.material) { name = "CountIn/Number", hideFlags = HideFlags.HideAndDontSave };
        if (m.HasProperty(ShaderUtilities.ID_OutlineWidth)) m.SetFloat(ShaderUtilities.ID_OutlineWidth, .16f);
        if (m.HasProperty(ShaderUtilities.ID_OutlineColor)) m.SetColor(ShaderUtilities.ID_OutlineColor, new Color(.012f, .02f, .045f, .94f));
        if (m.HasProperty(ShaderUtilities.ID_UnderlayColor))
        {
            m.EnableKeyword(ShaderUtilities.Keyword_Underlay);
            m.SetColor(ShaderUtilities.ID_UnderlayColor, new Color(glow.r, glow.g, glow.b, glowAlpha));
            m.SetFloat(ShaderUtilities.ID_UnderlayOffsetX, 0f);
            m.SetFloat(ShaderUtilities.ID_UnderlayOffsetY, 0f);
            m.SetFloat(ShaderUtilities.ID_UnderlayDilate, .45f);
            m.SetFloat(ShaderUtilities.ID_UnderlaySoftness, .55f);
        }
        materials.Add(m);
        return m;
    }

    private void ApplyAccent()
    {
        if (accentMaterial != null && accentMaterial.HasProperty(ShaderUtilities.ID_UnderlayColor))
            accentMaterial.SetColor(ShaderUtilities.ID_UnderlayColor, new Color(accent.r, accent.g, accent.b, .6f));
        if (startWord != null) startWord.SetGradient(Color.white, Color.Lerp(accent, Color.white, .42f));
    }

    private void BindStage()
    {
        var frame = Object.FindFirstObjectByType<JudgeGateFrame>();
        gateTransform = frame != null ? frame.transform : null;
        if (frame != null && frame.HalfWidth > 0f) ringHalf = new Vector2(frame.HalfWidth, frame.HalfHeight);
        var spawner = Object.FindFirstObjectByType<NoteSpawner>();
        if (spawner != null) spawnZ = spawner.spawnZ;
        if (gateCue != null) gateCue.Restore();
        gateCue = new CountInGateCue(gateTransform, spawner != null ? spawner.FloorGuide : null);
    }

    private void Update()
    {
        if (!running) return;
        Apply(AudioSettings.dspTime);
    }

    // 指定の DSP 時刻の見た目にする。テストからは時刻を直接渡して確かめる。
    public void Apply(double now)
    {
        if (!running) return;
        // 追加した助走中に「3」だけ先に表示せず、最初のクリック音と一緒に出す。
        if (now < firstBeatDspTime) { canvasGroup.alpha = 0f; return; }
        canvasGroup.alpha = 1f;
        PlaceAtGate();
        double since = now - firstBeatDspTime, startAge = now - songStartDspTime;
        int step = StepAt(now, firstBeatDspTime, countSeconds);
        HideFx();
        UpdateNumbers(since);
        UpdateStart(startAge);
        if (gateCue != null) gateCue.Apply(step, since, countSeconds, startAge, accent);
        if (now >= endDspTime) Finish();
    }

    // 数字はゲートの中央に重ね、リングはゲートの見た目の大きさに合わせる。カメラやゲートが無ければ画面の中央。
    private void PlaceAtGate()
    {
        var cam = Camera.main;
        if (gateTransform == null || cam == null || canvasRect == null) return;
        // 画面に重ねるキャンバスでは null、カメラに描くキャンバス(撮影など)ではそのカメラで画面座標を変換する
        Camera uiCamera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        Vector3 screen = cam.WorldToScreenPoint(gateTransform.position);
        if (screen.z <= 0f) return;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRect, screen, uiCamera, out var local)) return;
        center.anchoredPosition = local + new Vector2(0f, 12f);
        Vector2 min = new Vector2(float.MaxValue, float.MaxValue), max = new Vector2(float.MinValue, float.MinValue);
        for (int i = 0; i < 4; i++)
        {
            var corner = gateTransform.TransformPoint(new Vector3((i & 1) == 0 ? -ringHalf.x : ringHalf.x, (i & 2) == 0 ? -ringHalf.y : ringHalf.y, 0f));
            Vector3 p = cam.WorldToScreenPoint(corner);
            if (p.z <= 0f || !RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRect, p, uiCamera, out var lp)) return;
            min = Vector2.Min(min, lp); max = Vector2.Max(max, lp);
        }
        gateScreenHalf = (max - min) * .5f;
        gateScreenCenter = (max + min) * .5f - center.anchoredPosition;
        Vector3 far = cam.WorldToScreenPoint(gateTransform.position + Vector3.forward * spawnZ);
        if (far.z > 0f && RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRect, far, uiCamera, out var fp)) vanishing = fp - center.anchoredPosition;
    }

    private Vector2 gateScreenHalf = new Vector2(330f, 165f);
    private Vector2 gateScreenCenter = new Vector2(0f, -12f);

    private void UpdateNumbers(double since)
    {
        int slashIndex = 0, sparkIndex = 0;
        int sparkCount = reduced ? 0 : Mathf.RoundToInt(6f + 12f * power);
        for (int k = 0; k < numbers.Length; k++)
        {
            double bk = k * countSeconds, bn = bk + countSeconds;
            if (since < bk) { numbers[k].Hide(); continue; }
            if (since < bn)
            {
                double e = since - bk, u = e / countSeconds;
                float scale = e < .16 ? Mathf.LerpUnclamped(1.3f, 1f, EaseOutBack((float)(e / .16))) : Mathf.Lerp(1f, .965f, Mathf.Clamp01((float)((u - .45) / .55)));
                numbers[k].ShowWhole(Mathf.Clamp01((float)(e / .05)), scale);
                // 拍の終わりに、斬る線をうっすら予告する(予備動作)
                if (u > .8)
                {
                    var cuts = CutsFor(k);
                    for (int c = 0; c < cuts.Length && c < telegraphs.Count; c++)
                        ShowLine(telegraphs[c], cuts[c].from, cuts[c].to, 3f, WithAlpha(CutColor(k, c), .35f * Mathf.Clamp01((float)((u - .8) / .2))));
                }
                continue;
            }
            double ec = since - bn;
            if (ec < CutSeconds) numbers[k].ShowPieces((float)(ec / CutSeconds), 108f, 44f);
            else numbers[k].Hide();
            // 斬撃は拍ちょうどに走り、すぐ消える。火花は線の中点から散る。
            var lines = CutsFor(k);
            for (int c = 0; c < lines.Length; c++)
            {
                Color color = CutColor(k, c);
                if (ec < SlashSweep + .06 + SlashFade && slashIndex < slashes.Count)
                {
                    float sweep = Mathf.Clamp01((float)(ec / SlashSweep)), fade = 1f - Mathf.Clamp01((float)((ec - SlashSweep - .01) / SlashFade));
                    slashes[slashIndex++].Show(lines[c].from, Vector2.Lerp(lines[c].from, lines[c].to, sweep), color, fade, 12f, 40f);
                }
                sparkIndex = Burst(sparkIndex, (lines[c].from + lines[c].to) * .5f, (float)ec, sparkCount, color, k * 7 + c);
            }
        }
        slashCursor = slashIndex;
    }

    private int slashCursor;

    private void UpdateStart(double startAge)
    {
        if (startAge < 0) { startWord.Hide(); return; }
        float age = (float)startAge;
        float halfWidth = canvasRect != null && canvasRect.rect.width > 0 ? canvasRect.rect.width * .5f : 960f;
        float left = -halfWidth - center.anchoredPosition.x, right = halfWidth - center.anchoredPosition.x;
        // 画面の両端から青と赤の斬撃が中央で出会う
        if (age < .32f && slashCursor + 1 < slashes.Count)
        {
            float sweep = Mathf.Clamp01(age / .06f), fade = 1f - Mathf.Clamp01((age - .1f) / .22f);
            slashes[slashCursor++].Show(new Vector2(left, 0f), new Vector2(Mathf.Lerp(left, 0f, sweep), 0f), UISkinPalette.LogoBlue, fade, 14f, 46f);
            slashes[slashCursor++].Show(new Vector2(right, 0f), new Vector2(Mathf.Lerp(right, 0f, sweep), 0f), UISkinPalette.LogoRed, fade, 14f, 46f);
        }
        float hold = (float)startHold;
        if (age < hold) startWord.ShowWhole(1f, age < .14f ? Mathf.LerpUnclamped(1.45f, 1f, EaseOutBack(age / .14f)) : 1f);
        else if (age < hold + .4f)
        {
            startWord.ShowPieces((age - hold) / .34f, 68f, 0f);
            if (slashCursor < slashes.Count)
            {
                float sweep = Mathf.Clamp01((age - hold) / .07f), fade = 1f - Mathf.Clamp01((age - hold - .08f) / .16f);
                slashes[slashCursor++].Show(new Vector2(left, 0f), new Vector2(Mathf.Lerp(left, right, sweep), 0f), accent, fade, 8f, 28f);
            }
        }
        else startWord.Hide();
        // ゲートからリング(HARD は白いリングをもう1本)
        if (age < .55f)
        {
            float r = age / .55f;
            Ring(ringA, Mathf.Lerp(1f, 1.55f, EaseOut(r)), WithAlpha(accent, (1f - r) * (1f - r) * .95f), 10f);
        }
        if (!reduced && power >= 1f && age > .08f && age < .63f)
        {
            float r = (age - .08f) / .55f;
            Ring(ringB, Mathf.Lerp(1f, 1.8f, EaseOut(r)), new Color(1f, 1f, 1f, (1f - r) * (1f - r) * .6f), 6f);
        }
        // 奥から光の帯
        if (!reduced && age < .6f)
        {
            int n = Mathf.Min(streakImages.Count, Mathf.RoundToInt(8f + 14f * power));
            float k = 1f - age / .6f;
            for (int q = 0; q < n; q++)
            {
                float a = q / (float)n * Mathf.PI * 2f + .3f, r0 = (age * 1800f + q * 274f) % 1440f;
                var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a) * .6f).normalized;
                var img = streakImages[q];
                img.gameObject.SetActive(true);
                img.rectTransform.anchoredPosition = vanishing + dir * (r0 + 80f);
                img.rectTransform.localRotation = Quaternion.Euler(0, 0, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg);
                img.color = WithAlpha(q % 2 == 0 ? UISkinPalette.LogoRed : UISkinPalette.LogoBlue, .6f * k * Mathf.Clamp01(r0 / 240f));
            }
        }
    }

    private int Burst(int index, Vector2 origin, float age, int count, Color color, int seed)
    {
        if (age < 0f || age > .42f || count <= 0) return index;
        float alpha = 1f - age / .42f;
        for (int i = 0; i < count && index < sparks.Count; i++, index++)
        {
            float angle = Hash(i, seed) * Mathf.PI * 2f, speed = 240f + 440f * Hash(seed, i + 3);
            var spark = sparks[index];
            spark.gameObject.SetActive(true);
            spark.rectTransform.anchoredPosition = origin + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * (speed * age) + Vector2.down * (320f * age * age);
            spark.color = WithAlpha(i % 3 == 0 ? Color.white : color, .95f * alpha);
        }
        return index;
    }

    private void Ring(Image[] ring, float scale, Color color, float thickness)
    {
        Vector2 half = gateScreenHalf * scale, c = gateScreenCenter;
        Place(ring[0], c + new Vector2(0f, half.y), new Vector2(half.x * 2f + thickness, thickness), color);
        Place(ring[1], c - new Vector2(0f, half.y), new Vector2(half.x * 2f + thickness, thickness), color);
        Place(ring[2], c - new Vector2(half.x, 0f), new Vector2(thickness, half.y * 2f), color);
        Place(ring[3], c + new Vector2(half.x, 0f), new Vector2(thickness, half.y * 2f), color);
    }

    private static void Place(Image image, Vector2 position, Vector2 size, Color color)
    {
        image.gameObject.SetActive(true);
        image.rectTransform.anchoredPosition = position;
        image.rectTransform.sizeDelta = size;
        image.color = color;
    }

    private static void ShowLine(Image image, Vector2 from, Vector2 to, float thickness, Color color)
    {
        var d = to - from;
        image.gameObject.SetActive(true);
        image.rectTransform.anchoredPosition = from;
        image.rectTransform.sizeDelta = new Vector2(d.magnitude, thickness);
        image.rectTransform.localRotation = Quaternion.Euler(0, 0, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
        image.color = color;
    }

    private void HideFx()
    {
        foreach (var s in slashes) s.Hide();
        foreach (var t in telegraphs) t.gameObject.SetActive(false);
        foreach (var s in sparks) s.gameObject.SetActive(false);
        foreach (var s in streakImages) s.gameObject.SetActive(false);
        for (int i = 0; i < 4; i++) { ringA[i].gameObject.SetActive(false); ringB[i].gameObject.SetActive(false); }
        slashCursor = 0;
    }

    private void HideAll()
    {
        if (!built) return;
        HideFx();
        foreach (var n in numbers) if (n != null) n.Hide();
        if (startWord != null) startWord.Hide();
    }

    private void Finish()
    {
        running = false;
        if (gateCue != null) gateCue.Restore();
        ReleaseMaterials();
        UISkinKit.SafeDestroy(gameObject);
    }

    private void ReleaseMaterials()
    {
        foreach (var m in materials) UISkinKit.SafeDestroy(m);
        materials.Clear();
    }

    private void OnDisable()
    {
        // 途中で止められても(シーン移動・テストでの無効化)、ゲートと床の色を必ず元に戻す
        if (gateCue != null) gateCue.Restore();
    }

    private void OnDestroy()
    {
        if (gateCue != null) gateCue.Restore();
        ReleaseMaterials();
    }

    private void ScheduleCues(float volume)
    {
        EnsureCueClips();
        for (int i = 0; i < 3; i++) Schedule(tickClip, firstBeatDspTime + countSeconds * i, volume * .68f);
        // 前の数字を斬る音(2 と 1 の拍)と、START の X・両端からの斬撃、START を斬って消す音
        for (int i = 1; i < 3; i++) Schedule(swishClip, firstBeatDspTime + countSeconds * i, volume * .5f);
        Schedule(swishClip, songStartDspTime, volume * .55f);
        Schedule(startClip, songStartDspTime, volume * .9f);
        Schedule(swishClip, songStartDspTime + startHold, volume * .35f);
    }

    private void Schedule(AudioClip clip, double dspTime, float volume)
    {
        if (clip == null) return;
        var src = gameObject.AddComponent<AudioSource>();
        src.playOnAwake = false;
        src.loop = false;
        src.spatialBlend = 0f;
        src.volume = volume;
        src.clip = clip;
        src.PlayScheduled(dspTime);
    }

    private static void EnsureCueClips()
    {
        if (tickClip == null) tickClip = Cached("CountInTick", ProceduralSfx.CountTick());
        if (swishClip == null) swishClip = Cached("CountInSwish", ProceduralSfx.CountSwish());
        if (startClip == null) startClip = Cached("CountInStart", ProceduralSfx.CountStart());
    }

    private static AudioClip Cached(string name, float[] samples)
    {
        var clip = ProceduralSfx.Clip(name, samples);
        clip.hideFlags = HideFlags.HideAndDontSave;
        return clip;
    }

    private static Image MakeImage(Transform parent, string name, Sprite sprite, Color color, Vector2 size)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var image = go.GetComponent<Image>();
        image.sprite = sprite;
        if (sprite != null) image.type = Image.Type.Sliced;
        image.color = color;
        image.raycastTarget = false;
        image.rectTransform.sizeDelta = size;
        go.SetActive(false);
        return image;
    }

    private static Color WithAlpha(Color c, float a) => new Color(c.r, c.g, c.b, Mathf.Clamp01(a));
    private static float EaseOut(float x) { x = Mathf.Clamp01(x); return 1f - (1f - x) * (1f - x) * (1f - x); }
    private static float EaseOutBack(float x) { x = Mathf.Clamp01(x); const float c1 = 1.9f, c3 = c1 + 1f; return 1f + c3 * Mathf.Pow(x - 1f, 3f) + c1 * Mathf.Pow(x - 1f, 2f); }
    private static float Hash(int i, int j) { float h = Mathf.Sin(i * 127.1f + j * 311.7f) * 43758.5453f; return h - Mathf.Floor(h); }

    // 斬撃1本: 色の付いた淡い光と、白い芯の2重。起点から終点へ伸びる。
    private sealed class SlashLine
    {
        readonly RectTransform root;
        readonly Image glow, core;

        public SlashLine(Transform parent, string name)
        {
            root = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            root.SetParent(parent, false);
            root.pivot = new Vector2(0f, .5f);
            glow = MakeChild("Glow");
            core = MakeChild("Core");
            Hide();
        }

        Image MakeChild(string name)
        {
            var image = new GameObject(name, typeof(RectTransform), typeof(Image)).GetComponent<Image>();
            image.transform.SetParent(root, false);
            image.sprite = UISkinKit.RoundedRect();
            image.type = Image.Type.Sliced;
            image.raycastTarget = false;
            var rt = image.rectTransform;
            rt.anchorMin = new Vector2(0f, .5f); rt.anchorMax = new Vector2(1f, .5f);
            rt.pivot = new Vector2(.5f, .5f);
            rt.anchoredPosition = Vector2.zero;
            return image;
        }

        public void Show(Vector2 from, Vector2 to, Color color, float alpha, float coreThickness, float glowThickness)
        {
            var d = to - from;
            if (alpha <= 0f || d.sqrMagnitude < 1f) { Hide(); return; }
            root.gameObject.SetActive(true);
            root.anchoredPosition = from;
            root.sizeDelta = new Vector2(d.magnitude, glowThickness);
            root.localRotation = Quaternion.Euler(0, 0, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
            glow.rectTransform.sizeDelta = new Vector2(0f, glowThickness);
            core.rectTransform.sizeDelta = new Vector2(0f, coreThickness);
            glow.color = WithAlpha(color, .38f * alpha);
            core.color = new Color(1f, 1f, 1f, .96f * alpha);
        }

        public void Hide() { root.gameObject.SetActive(false); }
    }
}
