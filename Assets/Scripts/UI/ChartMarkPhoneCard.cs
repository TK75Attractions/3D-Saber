using TMPro;
using UnityEngine;
using UnityEngine.UI;

// スマホで印のアドレス(ChartMarkPhoneLink.Url)を QR コードで出す小さなカード(2026-10-28、スマホで打つのが大変なので)。
// 結果画面の一言の欄の右に子として付ける(AttachTo)か、印が無いときの F5 で自前のキャンバスに出す(ToggleStandalone、30 秒で消える)。
// 来場者のふだんの画面には出さない。アドレスが変わったら作り直す。どれも raycastTarget=false(結果画面の操作を止めない)。
public sealed class ChartMarkPhoneCard : MonoBehaviour
{
    public const float Width = 300f, Height = 452f;
    public const float StandaloneSeconds = 30f;
    public const int QuietModules = 4;
    public const string Title = "スマホで印";
    public const string Hint = "同じ Wi-Fi のスマホのカメラで読む";
    public const string NoAddress = "LAN の番地が見つかりません。Wi-Fi かホットスポットを入れてください";
    public const string TooLong = "アドレスが長すぎて QR にできません";
    const float Box = 264f, CodeY = 30f, StandaloneMargin = 20f;
    // 一言の欄に付けたとき、画面が細い(16:10 など)なら欄と重ならないところまで縮める(これより小さくはしない)。
    public const float MinFitScale = .6f;

    static ChartMarkPhoneCard standalone;
    public static bool StandaloneVisible => standalone != null;
    public static ChartMarkPhoneCard Standalone => standalone;

    RawImage image;
    SongSelectPanelGraphic frame;
    TextMeshProUGUI message, hint, address;
    Texture2D texture;
    GameObject root;
    string shownUrl;
    bool shown;
    int moduleCount;
    float laidOutScale = -1f, hideAt = -1f, keepClear;
    readonly Vector3[] corners = new Vector3[4];

    public string ShownUrl => shownUrl;
    public bool ShowsCode => texture != null && image != null && image.enabled;
    public Texture2D CodeTexture => texture;
    public string MessageText => message != null && message.enabled ? message.text : "";
    public string AddressText => address != null ? address.text : "";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { standalone = null; }

    // parent(キャンバス全面)の右端から 20 に置く。16:9 なら中央から x=790(幅 1240 の一言の欄の右)。
    // keepClearHalfWidth: 中央からこの幅(一言の欄の半分)には重ならないよう、画面が細いときは縮める。
    public static ChartMarkPhoneCard AttachTo(Transform parent, float keepClearHalfWidth = 0f)
    {
        if (parent == null) return null;
        var card = Create(parent, new Vector2(1f, .5f), new Vector2(-(StandaloneMargin + Width * .5f), 0f));
        card.keepClear = keepClearHalfWidth;
        card.Refresh();
        return card;
    }

    // 右端と欄の間に収まる大きさ(余白は両側に 20)。キャンバスの大きさが決まる前(幅が小さすぎる)は何もしない。
    void Fit()
    {
        if (keepClear <= 0f || !(transform.parent is RectTransform parent) || parent.rect.width < keepClear * 2f) return;
        float room = parent.rect.width * .5f - keepClear - StandaloneMargin * 2f;
        float scale = Mathf.Clamp(room / Width, MinFitScale, 1f);
        if (Mathf.Approximately(transform.localScale.x, scale)) return;
        transform.localScale = new Vector3(scale, scale, 1f);
        ((RectTransform)transform).anchoredPosition = new Vector2(-(StandaloneMargin + Width * scale * .5f), 0f);
        laidOutScale = -1f;   // QR を画素にそろえ直す
    }

    // 結果画面で印が無いときの F5: 右端に出す / 消す。出したら true。30 秒で消え、場面が変わっても消える。
    public static bool ToggleStandalone()
    {
        if (StandaloneVisible) { HideStandalone(); return false; }
        var go = new GameObject("ChartMarkPhoneCardCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
        var canvas = go.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        // 照準(900)より上、運営の知らせ(950)より下。レイキャスターは付けない。
        canvas.sortingOrder = 920;
        var scaler = go.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = .5f;
        // 右端から 20(16:9 なら中央から x=790、一言の欄のときと同じ所)。
        standalone = Create(go.transform, new Vector2(1f, .5f), new Vector2(-(StandaloneMargin + Width * .5f), 0f));
        standalone.root = go;
        standalone.hideAt = Time.unscaledTime + StandaloneSeconds;
        return true;
    }

    public static void HideStandalone()
    {
        var card = standalone;
        standalone = null;
        if (card != null) UISkinKit.SafeDestroy(card.root != null ? card.root : card.gameObject);
    }

    static ChartMarkPhoneCard Create(Transform parent, Vector2 anchor, Vector2 anchoredPosition)
    {
        var go = new GameObject("ChartMarkPhoneCard", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = anchor;
        rt.sizeDelta = new Vector2(Width, Height);
        rt.anchoredPosition = anchoredPosition;
        var card = go.AddComponent<ChartMarkPhoneCard>();
        card.Build();
        card.Refresh();
        return card;
    }

    void Build()
    {
        var ink = new Color(.93f, .96f, 1f);
        var muted = new Color(.62f, .7f, .78f);
        // 地は不透明(結果画面の数字が透けて見えない。リニアの色空間では 0.97 でも後ろが2割ほど見える)。
        SongSelectVisuals.Panel(transform, "Back", Vector2.zero, new Vector2(Width, Height),
            new Color(.035f, .05f, .09f, 1f), new Color(1f, .74f, .32f, .9f), 12f);
        SongSelectVisuals.Label(transform, "Title", Title, 26, new Vector2(0f, 190f), new Vector2(Box, 36f), ink,
            TextAlignmentOptions.Center, true);
        // QR が無いときの枠と知らせ。
        frame = SongSelectVisuals.Panel(transform, "Frame", new Vector2(0f, CodeY), new Vector2(Box, Box),
            new Color(1f, 1f, 1f, .04f), new Color(.62f, .7f, .78f, .5f), 6f);
        message = SongSelectVisuals.Label(transform, "Message", "", 22, new Vector2(0f, CodeY), new Vector2(Box - 32f, Box - 32f),
            new Color(1f, .82f, .45f), TextAlignmentOptions.Center);
        Wrap(message, 16f, 22f);
        var code = new GameObject("Code", typeof(RectTransform), typeof(RawImage));
        code.transform.SetParent(transform, false);
        image = code.GetComponent<RawImage>();
        image.rectTransform.anchoredPosition = new Vector2(0f, CodeY);
        image.color = Color.white;
        hint = SongSelectVisuals.Label(transform, "Hint", Hint, 18, new Vector2(0f, -124f), new Vector2(Box, 28f), muted,
            TextAlignmentOptions.Center);
        hint.enableAutoSizing = true;
        hint.fontSizeMin = 13f;
        hint.fontSizeMax = 18f;
        address = SongSelectVisuals.Label(transform, "Address", "", 15, new Vector2(0f, -178f), new Vector2(Box, 56f), muted,
            TextAlignmentOptions.Center);
        Wrap(address, 12f, 16f);
        foreach (var graphic in GetComponentsInChildren<Graphic>(true)) graphic.raycastTarget = false;
    }

    static void Wrap(TextMeshProUGUI label, float min, float max)
    {
        label.textWrappingMode = TextWrappingModes.Normal;
        label.enableAutoSizing = true;
        label.fontSizeMin = min;
        label.fontSizeMax = max;
        label.richText = true;
    }

    void Update()
    {
        if (hideAt >= 0f && Time.unscaledTime >= hideAt) { HideStandalone(); return; }
        Refresh();
    }

    // アドレスが変わったら作り直す(毎コマ比べる)。キャンバスの倍率が変わったら大きさを合わせ直す。
    public void Refresh()
    {
        if (image == null) return; // AttachTo・ToggleStandalone を通さずに付けたとき
        Fit();
        string url = ChartMarkPhoneLink.Url;
        if (!shown || url != shownUrl) Show(url);
        if (texture == null) return;
        // 画素にそろえるのはオーバーレイのキャンバスだけ(ワールド座標が画面の画素)。ほかは参照解像度の整数でそろえる。
        var canvas = image.canvas;
        bool overlay = canvas != null && canvas.rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay;
        float scale = overlay ? Mathf.Abs(image.rectTransform.lossyScale.x) : 1f;
        bool off = false;
        if (overlay)
        {
            image.rectTransform.GetWorldCorners(corners);
            Vector3 corner = corners[0];
            off = Mathf.Abs(corner.x - Mathf.Round(corner.x)) > .01f || Mathf.Abs(corner.y - Mathf.Round(corner.y)) > .01f;
        }
        if (off || !Mathf.Approximately(scale, laidOutScale)) Layout(scale, overlay);
    }

    void Show(string url)
    {
        shown = true;
        shownUrl = url;
        ReleaseTexture();
        bool[,] modules = string.IsNullOrEmpty(url) ? null : QrCode.Encode(url);
        if (modules != null)
        {
            texture = MakeTexture(modules);
            moduleCount = texture.width;
            image.texture = texture;
            laidOutScale = -1f;
        }
        bool hasCode = modules != null;
        image.enabled = hasCode;
        hint.enabled = hasCode;
        frame.enabled = !hasCode;
        message.enabled = !hasCode;
        message.text = hasCode ? "" : string.IsNullOrEmpty(url) ? NoAddress : TooLong;
        address.text = string.IsNullOrEmpty(url) ? "" : "<noparse>" + url + "</noparse>";
    }

    // 1 モジュールが画面の整数の画素になる大きさ(枠 264 に入る中で一番大きい)にし、左下の角を画素の境目にそろえる
    // (点の補間で太さがばらつかない)。
    void Layout(float scale, bool alignToPixels)
    {
        laidOutScale = scale;
        if (scale <= 0f || moduleCount <= 0) return;
        int pixels = Mathf.Max(1, Mathf.FloorToInt(Box * scale / moduleCount + 1e-4f));
        float side = pixels * moduleCount / scale;
        var rt = image.rectTransform;
        rt.sizeDelta = new Vector2(side, side);
        rt.anchoredPosition = new Vector2(0f, CodeY);
        if (!alignToPixels) return;
        rt.GetWorldCorners(corners);
        Vector3 corner = corners[0];
        rt.anchoredPosition += new Vector2(Mathf.Round(corner.x) - corner.x, Mathf.Round(corner.y) - corner.y) / scale;
    }

    // 余白 QuietModules を含めて 1 モジュール = 1 テクセル。白地に黒。
    static Texture2D MakeTexture(bool[,] modules)
    {
        int size = modules.GetLength(0), count = size + QuietModules * 2;
        var result = new Texture2D(count, count, TextureFormat.RGBA32, false)
        {
            name = "ChartMarkPhoneQr",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave,
        };
        var pixels = new Color32[count * count];
        var white = new Color32(255, 255, 255, 255);
        var black = new Color32(0, 0, 0, 255);
        for (int row = 0; row < count; row++)
            for (int column = 0; column < count; column++)
            {
                // テクスチャは下の行から、QR は上の行から。
                int x = column - QuietModules, y = count - 1 - row - QuietModules;
                bool dark = x >= 0 && y >= 0 && x < size && y < size && modules[x, y];
                pixels[row * count + column] = dark ? black : white;
            }
        result.SetPixels32(pixels);
        result.Apply(false);
        return result;
    }

    void ReleaseTexture()
    {
        if (image != null) image.texture = null;
        UISkinKit.SafeDestroy(texture);
        texture = null;
        moduleCount = 0;
    }

    void OnDestroy()
    {
        ReleaseTexture();
        if (ReferenceEquals(standalone, this)) standalone = null;
    }
}
