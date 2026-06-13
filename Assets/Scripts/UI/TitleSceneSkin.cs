using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using TMPro;

// タイトル画面の見た目をランタイムで強化する(シーンは編集しない)。
// 構成:
//   - 「BEAT / TRACE / SLASH」3段ロゴ(Chakra Petch Bold Italic、赤/青/緑グラデ+グロー)
//   - 中央下に本物の CuttableNote が浮遊し、セーバー(マウス/実機)で切ると
//     MeshSlicer で砕け散ってフラッシュ→曲選択へ遷移(Enter/Space でも疑似カット)
//   - START/QUIT ボタンは廃止し、右下に小さな QUIT のみ残す
// 3D ノーツを背景 UI より手前に見せるため、Canvas を ScreenSpaceCamera(planeDistance=20)に切り替える。
public class TitleSceneSkin : MonoBehaviour
{
    // 既定で「跳ね」OFF。Inspector で値を入れたらロゴが脈動する。
    public float titlePulseHz = 0f;
    public float titlePulseAmplitude = 0f;
    public string tagline = "// CUT . THE . RHYTHM";
    public string versionLabel = "PROTOTYPE BUILD";
    public string promptText = "CUT THE NOTE TO START";
    public Vector3 startNoteWorldPos = new Vector3(0f, -2.3f, 0f);

    private TitleMenuController titleCtl;
    private RectTransform titleContainer;
    private TitleStartNote startNote;
    private TextMeshProUGUI prompt;
    private Image flashImage;
    private bool transitioning;
    private float age;

    void Start()
    {
        titleCtl = Object.FindFirstObjectByType<TitleMenuController>();
        if (titleCtl == null) return;

        var canvas = titleCtl.GetComponent<Canvas>();
        if (canvas == null) canvas = titleCtl.GetComponentInParent<Canvas>();
        if (canvas == null) return;

        // 3D ノーツを UI 背景より手前に描くため、カメラ前方の平面に Canvas を移す
        bool has3D = false;
        var cam = Camera.main;
        if (cam != null)
        {
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = cam;
            canvas.planeDistance = 20f;
            has3D = true;
        }

        CyberBackdrop.Ensure(canvas);
        HideLegacyTitle(canvas);
        BuildLogo(canvas);
        BuildDividerAndTagline(canvas);
        ReplaceButtons(canvas, has3D);
        AddVersionTag(canvas);

        if (has3D)
        {
            BuildTitleSaber();
            startNote = TitleStartNote.Build(startNoteWorldPos, UISkinPalette.LogoRed);
            startNote.OnSlashed += HandleSlashed;
            BuildPrompt(canvas);
        }

        BuildFlashOverlay(canvas);
    }

    void Update()
    {
        age += Time.unscaledDeltaTime;

        // 「切ってスタート」プロンプトの明滅
        if (prompt != null && !transitioning)
        {
            Color c = prompt.color;
            c.a = 0.55f + 0.35f * (0.5f + 0.5f * Mathf.Sin(age * 2.4f));
            prompt.color = c;
        }

        // キーボードでも開始できる(疑似スラッシュで同じ砕け散り演出を通す)
        if (!transitioning && startNote != null && Keyboard.current != null)
        {
            if (Keyboard.current.enterKey.wasPressedThisFrame ||
                Keyboard.current.spaceKey.wasPressedThisFrame)
            {
                startNote.SlashProgrammatically();
            }
        }

        if (titleContainer == null) return;
        if (titlePulseAmplitude <= 0f || titlePulseHz <= 0f)
        {
            if (titleContainer.localScale != Vector3.one)
            {
                titleContainer.localScale = Vector3.one;
            }
            return;
        }
        float pulse = 1f + titlePulseAmplitude * Mathf.Sin(age * titlePulseHz * 2f * Mathf.PI);
        titleContainer.localScale = new Vector3(pulse, pulse, 1f);
    }

    // ---- ロゴ ----

    void HideLegacyTitle(Canvas canvas)
    {
        Text original = FindTextByContent(canvas, "3D SABER");
        if (original != null) original.gameObject.SetActive(false);
    }

    void BuildLogo(Canvas canvas)
    {
        var container = new GameObject("TitleLogo", typeof(RectTransform), typeof(CanvasGroup));
        container.transform.SetParent(canvas.transform, false);
        titleContainer = container.GetComponent<RectTransform>();
        titleContainer.sizeDelta = new Vector2(1300f, 480f);
        titleContainer.anchoredPosition = new Vector2(0f, 195f);

        BuildLogoWord(container.transform, "BEAT", UISkinPalette.LogoRed, new Vector2(0f, 140f), 640f);
        BuildLogoWord(container.transform, "TRACE", UISkinPalette.LogoBlue, new Vector2(0f, 0f), 760f);
        BuildLogoWord(container.transform, "SLASH", UISkinPalette.LogoGreen, new Vector2(0f, -140f), 760f);

        var entrance = container.AddComponent<UIFadeSlideIn>();
        entrance.delay = 0.05f;
        entrance.duration = 0.6f;
        entrance.fromOffset = new Vector2(0f, 46f);
    }

    TextMeshProUGUI BuildLogoWord(Transform parent, string word, Color color, Vector2 pos, float glowWidth)
    {
        MakeGlowBlob(parent, pos, new Vector2(glowWidth, 230f), color, 0.17f);

        var font = UISkinKit.LogoFontAsset();
        // Chakra Petch Bold Italic は元から太字+斜体なので素のまま使う。
        // フォントが無い環境では既定フォントに擬似 Bold+Italic で形だけ寄せる。
        FontStyles style = font != null ? FontStyles.Normal : (FontStyles.Bold | FontStyles.Italic);
        var t = UISkinKit.MakeTMP(parent, "Logo_" + word, word, 128f, color,
            TextAlignmentOptions.Center, pos, new Vector2(1200f, 150f), style, 4f, font);
        ApplyLogoGradient(t, color);
        return t;
    }

    // 参照画像のネオン文字: 上が白寄り、下が深い原色、暗い縁取りで締める。
    public static void ApplyLogoGradient(TextMeshProUGUI t, Color accent)
    {
        t.enableVertexGradient = true;
        Color top = Color.Lerp(accent, Color.white, 0.58f);
        Color bottom = Color.Lerp(accent, Color.black, 0.10f);
        t.colorGradient = new VertexGradient(top, top, bottom, bottom);
        t.outlineWidth = 0.13f;
        t.outlineColor = new Color32(8, 10, 24, 220);
    }

    static void MakeGlowBlob(Transform parent, Vector2 pos, Vector2 size, Color color, float alpha)
    {
        var go = new GameObject("GlowBlob", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = size;
        rt.anchoredPosition = pos;
        var img = go.GetComponent<Image>();
        img.sprite = UISkinKit.SoftGlow();
        img.color = new Color(color.r, color.g, color.b, alpha);
        img.raycastTarget = false;
    }

    void BuildDividerAndTagline(Canvas canvas)
    {
        // ロゴ下の細いディバイダー(参照画像準拠)
        var line = new GameObject("Divider", typeof(RectTransform), typeof(Image));
        line.transform.SetParent(canvas.transform, false);
        var lrt = line.GetComponent<RectTransform>();
        lrt.sizeDelta = new Vector2(560f, 2f);
        lrt.anchoredPosition = new Vector2(0f, -62f);
        var limg = line.GetComponent<Image>();
        limg.color = new Color(1f, 1f, 1f, 0.40f);
        limg.raycastTarget = false;

        var t = UISkinKit.MakeTMP(canvas.transform, "Tagline", tagline, 17f,
            UISkinPalette.SubtleGray, TextAlignmentOptions.Center,
            new Vector2(0f, -94f), new Vector2(900f, 34f), FontStyles.Normal, 8f);
        var fade = t.gameObject.AddComponent<UIFadeSlideIn>();
        fade.delay = 0.45f;
        fade.duration = 0.5f;
        fade.fromOffset = new Vector2(0f, -12f);
    }

    // ---- ボタン(廃止して QUIT ミニのみ) ----

    void ReplaceButtons(Canvas canvas, bool slashStartAvailable)
    {
        foreach (var btn in canvas.GetComponentsInChildren<Button>())
        {
            string label = GetButtonLabelText(btn);
            if (label == "QUIT")
            {
                btn.gameObject.SetActive(false);
            }
            else if (label == "START")
            {
                if (slashStartAvailable)
                {
                    btn.gameObject.SetActive(false);
                }
                else
                {
                    // カメラが無い等で 3D ノーツが出せない場合のフォールバック
                    UISkinKit.RestyleButton(btn, UISkinPalette.Cyan, 34f);
                }
            }
        }

        // 右下に小さな QUIT
        var parts = UISkinKit.MakeNeonButton(canvas.transform, "QuitMini", "QUIT",
            Vector2.zero, new Vector2(150f, 54f), UISkinPalette.Magenta,
            () => { if (titleCtl != null) titleCtl.OnQuitButton(); }, 18f);
        var rt = parts.button.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(1f, 0f);
        rt.anchorMax = new Vector2(1f, 0f);
        rt.pivot = new Vector2(1f, 0f);
        rt.anchoredPosition = new Vector2(-28f, 24f);
    }

    static string GetButtonLabelText(Button btn)
    {
        var t = btn.GetComponentInChildren<Text>();
        return t != null ? t.text : "";
    }

    void AddVersionTag(Canvas canvas)
    {
        var t = UISkinKit.MakeTMP(canvas.transform, "VersionTag", versionLabel, 15f,
            new Color(0.45f, 0.50f, 0.65f, 0.8f), TextAlignmentOptions.Left,
            new Vector2(28f, 22f), new Vector2(400f, 30f), FontStyles.Normal, 3f);
        var rt = t.rectTransform;
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(0f, 0f);
        rt.pivot = new Vector2(0f, 0f);
    }

    // ---- 切ってスタート ----

    void BuildTitleSaber()
    {
        var saber = new GameObject("TitleSaber");
        var tracker = saber.AddComponent<SaberTracker>();
        var bridge = saber.AddComponent<SaberInputBridge>();
        bridge.useInputPoint = true;
        bridge.fallbackToMouse = true;
        bridge.fixedZ = 0f;
        var judge = saber.AddComponent<SaberCutJudge>();
        judge.saber = tracker;
        // タイトルは気軽に切れるよう本編より緩め
        judge.bladeRadius = 0.32f;
        judge.noteHitRadiusXY = 0.60f;
        judge.minCutSpeed = 2.5f;
    }

    void BuildPrompt(Canvas canvas)
    {
        prompt = UISkinKit.MakeTMP(canvas.transform, "StartPrompt", promptText, 22f,
            UISkinPalette.OffWhite, TextAlignmentOptions.Center,
            new Vector2(0f, -352f), new Vector2(900f, 40f), FontStyles.Bold, 7f);
        var fade = prompt.gameObject.AddComponent<UIFadeSlideIn>();
        fade.delay = 0.6f;
        fade.duration = 0.5f;
        fade.fromOffset = new Vector2(0f, -14f);
    }

    void BuildFlashOverlay(Canvas canvas)
    {
        var go = new GameObject("SlashFlash", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(canvas.transform, false);
        go.transform.SetAsLastSibling();
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        flashImage = go.GetComponent<Image>();
        flashImage.color = new Color(1f, 1f, 1f, 0f);
        flashImage.raycastTarget = false;
    }

    void HandleSlashed()
    {
        if (transitioning) return;
        transitioning = true;
        PlaySlashChime();
        StartCoroutine(TransitionAfterSlash());
    }

    IEnumerator TransitionAfterSlash()
    {
        // 破片が飛び散るのを見せつつ、白フラッシュ→フェードで曲選択へ
        float t = 0f;
        const float total = 1.05f;
        const float spike = 0.10f;
        while (t < total)
        {
            t += Time.unscaledDeltaTime;
            if (flashImage != null)
            {
                float a = t < spike
                    ? Mathf.Lerp(0f, 0.65f, t / spike)
                    : Mathf.Lerp(0.65f, 0f, (t - spike) / (total - spike));
                Color c = flashImage.color;
                c.a = a;
                flashImage.color = c;
            }
            yield return null;
        }
        if (titleCtl != null) titleCtl.OnStartButton();
    }

    void PlaySlashChime()
    {
        var go = new GameObject("TitleSlashSfx", typeof(AudioSource));
        var src = go.GetComponent<AudioSource>();
        src.playOnAwake = false;
        src.PlayOneShot(JudgmentSfx.Beep(880f, 0.18f), 0.50f);
        src.PlayOneShot(JudgmentSfx.Beep(1318.5f, 0.35f), 0.35f);
        Destroy(go, 1.5f);
    }

    public static Text FindTextByContent(Canvas canvas, string content)
    {
        foreach (var t in canvas.GetComponentsInChildren<Text>(true))
        {
            if (t.text == content) return t;
        }
        return null;
    }
}
