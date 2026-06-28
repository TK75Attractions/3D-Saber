using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using TMPro;

// 譜面エディタ(Note-Recorder)用の、コードだけで自動生成されるヘルプ画面。
// シーンへの配線は一切不要。ChartManager のあるシーンを Play すると自動で出る。
//   ・画面上部に常時「操作ヒントバー」
//   ・H / F1 / 右上の「?」ボタンで詳細ヘルプを開閉（初回は自動で開く）
// 本編シーンには ChartManager が無いので出ない。
public class EditorHelpOverlay : MonoBehaviour
{
    private GameObject panelRoot;
    private Font jpFont;
    private bool fontResolved;

    // ChartManager のあるシーン(=譜面エディタ)でだけ自動生成する。
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoSpawn()
    {
        if (Object.FindFirstObjectByType<ChartManager>() == null) return;
        Ensure();
    }

    public static EditorHelpOverlay Ensure()
    {
        var existing = Object.FindFirstObjectByType<EditorHelpOverlay>();
        if (existing != null) return existing;
        return new GameObject("EditorHelpOverlay").AddComponent<EditorHelpOverlay>();
    }

    void Start()
    {
        EnsureEventSystem();
        Build();
        SetOpen(true); // 初回は開いた状態で迎える
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (kb == null) return;
        if (IsTypingInInputField()) return; // BPM入力中などは無効化
        if (kb.hKey.wasPressedThisFrame || kb.f1Key.wasPressedThisFrame) Toggle();
        else if (kb.escapeKey.wasPressedThisFrame && IsOpen()) SetOpen(false);
    }

    public bool IsOpen() => panelRoot != null && panelRoot.activeSelf;
    public void Toggle() => SetOpen(!IsOpen());
    public void SetOpen(bool open) { if (panelRoot != null) panelRoot.SetActive(open); }

    // ---------------------------------------------------------------
    // 構築
    // ---------------------------------------------------------------
    private void Build()
    {
        var canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 32000; // 既存UIより必ず前面
        var scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;
        gameObject.AddComponent<GraphicRaycaster>();

        // 常時ヒントバー（上部）
        var strip = MakeImage("HintStrip", transform, new Color(0f, 0f, 0f, 0.5f));
        strip.raycastTarget = false;
        Stretch(strip.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -42), Vector2.zero);

        var hint = MakeText("HintBar", transform,
            "左クリック=ノーツを置く   /   右クリック=消す      ・      ← →=移動      ・      H または F1 でヘルプ",
            20, TextAnchor.MiddleCenter, new Color(1f, 1f, 1f, 0.92f));
        Stretch(hint.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(16, -42), new Vector2(-178, 0));

        // 右上「?」ボタン（常時）
        var help = MakeButton("HelpButton", transform, "?  ヘルプ (H)", Toggle, new Color(0.20f, 0.45f, 0.85f, 0.95f));
        help.anchorMin = help.anchorMax = new Vector2(1, 1);
        help.pivot = new Vector2(1, 1);
        help.anchoredPosition = new Vector2(-10, -6);
        help.sizeDelta = new Vector2(156, 32);

        BuildPanel();
    }

    private void BuildPanel()
    {
        panelRoot = new GameObject("HelpPanel", typeof(RectTransform));
        panelRoot.transform.SetParent(transform, false);
        Stretch(panelRoot.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

        // 背景（クリックで閉じる）
        var dim = MakeImage("Dim", panelRoot.transform, new Color(0f, 0f, 0f, 0.62f));
        Stretch(dim.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        var dimBtn = dim.gameObject.AddComponent<Button>();
        dimBtn.transition = Selectable.Transition.None;
        dimBtn.onClick.AddListener(() => SetOpen(false));

        // カード
        var card = MakeImage("Card", panelRoot.transform, new Color(0.10f, 0.12f, 0.16f, 0.98f));
        var crt = card.rectTransform;
        crt.anchorMin = crt.anchorMax = new Vector2(0.5f, 0.5f);
        crt.pivot = new Vector2(0.5f, 0.5f);
        crt.sizeDelta = new Vector2(1020, 690);

        var title = MakeText("Title", card.transform, "譜面エディタ ヘルプ", 32, TextAnchor.MiddleLeft, Color.white);
        Stretch(title.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(30, -62), new Vector2(-30, -14));

        string left =
            "<b><color=#7FE0FF>■ 盤面（グリッド）</color></b>\n" +
            "  左クリック : ノーツを置く / 選ぶ\n" +
            "  右クリック : ノーツを消す\n" +
            "  色 …  <color=#FFFFFF>白=Tap</color>  <color=#FFE14D>黄=Flick</color>  <color=#FF6B6B>赤=Long</color>\n" +
            "  明るいマス = 選択中のノーツ\n" +
            "\n" +
            "<b><color=#7FE0FF>■ ノーツの種類</color></b>（種類ボタンで切替）\n" +
            "  Tap    : タイミングよく斬る\n" +
            "  Flick  : 指定した向きに斬る\n" +
            "  Long   : 長押し（長さはホイールで調整）\n" +
            "\n" +
            "<b><color=#7FE0FF>■ 向き（選択中ノーツにテンキー）</color></b>\n" +
            "  <b>7 8 9        ↖ ↑ ↗</b>\n" +
            "  <b>4 5 6   =    ← ・ →</b>\n" +
            "  <b>1 2 3        ↙ ↓ ↘</b>\n" +
            "  5 = 向きなし";

        string right =
            "<b><color=#7FE0FF>■ 再生</color></b>\n" +
            "  Start/Stop  : 再生 / 一時停止\n" +
            "  タイムライン : クリック/ドラッグで移動\n" +
            "\n" +
            "<b><color=#7FE0FF>■ 移動</color></b>\n" +
            "  ← / →   : 1拍 戻る / 進む\n" +
            "  , / .    : 1小節 戻る / 進む\n" +
            "  拍入力欄 : 数字でその拍へジャンプ\n" +
            "\n" +
            "<b><color=#7FE0FF>■ 曲の設定</color></b>\n" +
            "  BPM / OFFSET / 拍子 を入力欄で調整\n" +
            "\n" +
            "<b><color=#7FE0FF>■ 編集</color></b>\n" +
            "  Ctrl+Z : 元に戻す   /   Ctrl+Y : やり直し\n" +
            "  ホイール : 選択中 Long の長さ調整\n" +
            "\n" +
            "<b><color=#7FE0FF>■ 保存 / 本編へ出力</color></b>\n" +
            "  Save / Load : 編集データの保存・読込\n" +
            "  Export(本編形式) : 本編で遊べる形へ書き出し\n" +
            "    ※ ChartManager の曲フォルダ/難易度を先に設定";

        var lc = MakeText("Left", card.transform, left, 19, TextAnchor.UpperLeft, new Color(0.92f, 0.95f, 1f));
        Stretch(lc.rectTransform, new Vector2(0, 0), new Vector2(0.5f, 1), new Vector2(30, 54), new Vector2(-14, -76));
        var rc = MakeText("Right", card.transform, right, 19, TextAnchor.UpperLeft, new Color(0.92f, 0.95f, 1f));
        Stretch(rc.rectTransform, new Vector2(0.5f, 0), new Vector2(1, 1), new Vector2(14, 54), new Vector2(-30, -76));

        var footer = MakeText("Footer", card.transform,
            "閉じる:  H  /  F1  /  Esc  /  カード外をクリック", 16, TextAnchor.LowerCenter,
            new Color(0.7f, 0.8f, 0.95f));
        Stretch(footer.rectTransform, new Vector2(0, 0), new Vector2(1, 0), new Vector2(30, 16), new Vector2(-30, 42));

        var close = MakeButton("Close", card.transform, "×", () => SetOpen(false), new Color(0.7f, 0.25f, 0.3f, 0.95f));
        close.anchorMin = close.anchorMax = new Vector2(1, 1);
        close.pivot = new Vector2(1, 1);
        close.anchoredPosition = new Vector2(-12, -12);
        close.sizeDelta = new Vector2(40, 40);
    }

    // ---------------------------------------------------------------
    // 小物ヘルパー
    // ---------------------------------------------------------------
    private Font JpFont()
    {
        if (fontResolved) return jpFont;
        fontResolved = true;
        // 日本語が出るOSフォントを優先順で探す（Win/Mac両対応）。無ければ既定にフォールバック。
        jpFont = Font.CreateDynamicFontFromOSFont(
            new[] { "Yu Gothic UI", "Yu Gothic", "Meiryo", "MS Gothic",
                    "Hiragino Sans", "Hiragino Kaku Gothic ProN", "Noto Sans CJK JP", "Arial" }, 18);
        if (jpFont == null) jpFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        return jpFont;
    }

    private Image MakeImage(string name, Transform parent, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var img = go.GetComponent<Image>();
        img.color = color;
        return img;
    }

    private Text MakeText(string name, Transform parent, string content, int size, TextAnchor anchor, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Text));
        go.transform.SetParent(parent, false);
        var t = go.GetComponent<Text>();
        t.font = JpFont();
        t.text = content;
        t.fontSize = size;
        t.alignment = anchor;
        t.color = color;
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.supportRichText = true;
        t.lineSpacing = 1.1f;
        t.raycastTarget = false;
        return t;
    }

    private RectTransform MakeButton(string name, Transform parent, string label, UnityAction onClick, Color bg)
    {
        var img = MakeImage(name, parent, bg);
        var btn = img.gameObject.AddComponent<Button>();
        btn.onClick.AddListener(onClick);
        var t = MakeText(name + "Label", img.transform, label, 18, TextAnchor.MiddleCenter, Color.white);
        Stretch(t.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        return img.rectTransform;
    }

    private static void Stretch(RectTransform rt, Vector2 aMin, Vector2 aMax, Vector2 offMin, Vector2 offMax)
    {
        rt.anchorMin = aMin;
        rt.anchorMax = aMax;
        rt.offsetMin = offMin;
        rt.offsetMax = offMax;
    }

    private bool IsTypingInInputField()
    {
        var es = EventSystem.current;
        var sel = es != null ? es.currentSelectedGameObject : null;
        if (sel == null) return false;
        return sel.GetComponent<TMP_InputField>() != null || sel.GetComponent<InputField>() != null;
    }

    private void EnsureEventSystem()
    {
        if (Object.FindFirstObjectByType<EventSystem>() != null) return;
        var go = new GameObject("EventSystem", typeof(EventSystem));
        go.AddComponent<InputSystemUIInputModule>(); // 新Input System用
    }
}
