using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

// 曲選択画面の「SONG WHEEL」(デザインハンドオフ 12a)。
// 選択行は常に画面中央に固定され、曲リスト側が回転して流れる。
// 各行はオフセット off = i - animPos から位置(off×104px)・縮尺・不透明度を計算する。
// アニメーションは animPos を easeOutCubic で 0.3 秒かけて目標へ動かすだけで全行が追従する。
public class SongWheelView : MonoBehaviour
{
    public const float RowHeight = 84f;
    public const float RowStride = 104f;
    public const float SlideDuration = 0.30f;

    static readonly Color RowLine = new Color(0.118f, 0.133f, 0.275f);        // #1E2246
    static readonly Color RowFill = new Color(20f / 255f, 24f / 255f, 56f / 255f, 0.72f);
    static readonly Color SelectedFill = new Color(69f / 255f, 1f, 247f / 255f, 0.10f);
    static readonly Color LockedText = new Color(0.227f, 0.251f, 0.40f);      // #3A4066

    class Row
    {
        public RectTransform root;
        public CanvasGroup group;
        public Image fill;
        public Image border;
        public Image glow;
        public Image thumb;
        public TextMeshProUGUI title;
        public TextMeshProUGUI level;
        public bool locked;
    }

    SongSelectController ctl;
    System.Func<int, Sprite> coverProvider;
    System.Func<int, Color> accentProvider; // 現在難易度の色(レベル数値用)
    readonly List<Row> rows = new List<Row>();
    float animFrom;
    float animTo;
    float animAge = 999f;

    // ---- 純関数(テストから直接叩く) ----

    // 行の縮尺: 中央 1.0、1行離れるごとに 0.07 縮み、下限 0.72
    public static float RowScale(float absOff)
    {
        return Mathf.Max(1f - absOff * 0.07f, 0.72f);
    }

    // 行の不透明度: 中央 1.0 → 隣接 0.57 → 以降 0.18 刻みで減衰(下限 0.25)、3行超は 0。
    // 中央と隣接の間はアニメ中に連続に繋ぐ。
    public static float RowAlpha(float absOff)
    {
        if (absOff > 3f) return 0f;
        float faded = Mathf.Max(0.75f - absOff * 0.18f, 0.25f);
        if (absOff >= 1f) return faded;
        return Mathf.Lerp(1f, 0.57f, absOff);
    }

    // レベル表示: 1〜99 はゼロ埋め2桁、0 以下(譜面なし)は "--"
    public static string FormatLevel(int level)
    {
        return level > 0 ? Mathf.Clamp(level, 0, 99).ToString("00") : "--";
    }

    // ---- 構築 ----

    public static SongWheelView Build(SongSelectController controller, Transform parent,
        System.Func<int, Sprite> coverProvider, System.Func<int, Color> accentProvider)
    {
        var go = new GameObject("SongWheel", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(680f, 760f);
        rt.anchoredPosition = new Vector2(-520f, -26f); // left:100 top:186 (1920×1080基準)
        var view = go.AddComponent<SongWheelView>();
        view.ctl = controller;
        view.coverProvider = coverProvider;
        view.accentProvider = accentProvider;
        view.BuildContent();
        return view;
    }

    void BuildContent()
    {
        // 選択フレーム(中央固定)。上下の細線 + うっすら塗り。
        var frame = new GameObject("SelectionFrame", typeof(RectTransform), typeof(Image));
        frame.transform.SetParent(transform, false);
        var frt = frame.GetComponent<RectTransform>();
        frt.sizeDelta = new Vector2(680f, RowStride);
        var fimg = frame.GetComponent<Image>();
        fimg.color = new Color(69f / 255f, 1f, 247f / 255f, 0.03f);
        fimg.raycastTarget = false;
        MakeLine(frame.transform, new Vector2(0f, RowStride * 0.5f));
        MakeLine(frame.transform, new Vector2(0f, -RowStride * 0.5f));

        // 左の六角マーカー(ブランドモチーフ)
        var mark = new GameObject("Marker", typeof(RectTransform), typeof(ResultHexBadgeGraphic));
        mark.transform.SetParent(transform, false);
        var mrt = mark.GetComponent<RectTransform>();
        mrt.sizeDelta = new Vector2(24f, 28f);
        mrt.anchoredPosition = new Vector2(-352f, 0f);
        var hex = mark.GetComponent<ResultHexBadgeGraphic>();
        hex.flatFill = true;
        hex.topColor = UISkinPalette.Cyan;
        hex.raycastTarget = false;

        int count = ctl != null ? ctl.SongCount : 0;
        for (int i = 0; i < count; i++)
        {
            rows.Add(BuildRow(i));
        }

        animFrom = animTo = ctl != null ? Mathf.Max(0, ctl.SelectedIndex) : 0;
        LayoutRows(animTo);
    }

    Row BuildRow(int index)
    {
        var row = new Row();
        var go = new GameObject($"WheelRow_{index}", typeof(RectTransform), typeof(CanvasGroup), typeof(Button));
        go.transform.SetParent(transform, false);
        row.root = go.GetComponent<RectTransform>();
        row.root.sizeDelta = new Vector2(680f, RowHeight);
        row.group = go.GetComponent<CanvasGroup>();
        row.locked = ctl.IsLocked(index);

        // 選択グロー(選択行のみ点灯)
        var glowGo = new GameObject("Glow", typeof(RectTransform), typeof(Image));
        glowGo.transform.SetParent(go.transform, false);
        var grt = glowGo.GetComponent<RectTransform>();
        grt.anchorMin = Vector2.zero;
        grt.anchorMax = Vector2.one;
        grt.sizeDelta = new Vector2(90f, 70f);
        row.glow = glowGo.GetComponent<Image>();
        row.glow.sprite = UISkinKit.SoftGlow();
        row.glow.color = Color.clear;
        row.glow.raycastTarget = false;

        var fillGo = new GameObject("Fill", typeof(RectTransform), typeof(Image));
        fillGo.transform.SetParent(go.transform, false);
        StretchFull(fillGo.GetComponent<RectTransform>());
        row.fill = fillGo.GetComponent<Image>();
        row.fill.sprite = UISkinKit.RoundedRect();
        row.fill.type = Image.Type.Sliced;
        row.fill.color = RowFill;
        row.fill.raycastTarget = true; // ボタンの当たり判定

        var borderGo = new GameObject("Border", typeof(RectTransform), typeof(Image));
        borderGo.transform.SetParent(go.transform, false);
        StretchFull(borderGo.GetComponent<RectTransform>());
        row.border = borderGo.GetComponent<Image>();
        row.border.sprite = UISkinKit.RoundedFrame();
        row.border.type = Image.Type.Sliced;
        row.border.color = RowLine;
        row.border.raycastTarget = false;

        // サムネイル(cover.png。無ければ曲名頭文字入りのプレースホルダー)
        var thumbGo = new GameObject("Thumb", typeof(RectTransform), typeof(Image));
        thumbGo.transform.SetParent(go.transform, false);
        var trt = thumbGo.GetComponent<RectTransform>();
        trt.sizeDelta = new Vector2(56f, 56f);
        trt.anchoredPosition = new Vector2(-290f, 0f);
        row.thumb = thumbGo.GetComponent<Image>();
        row.thumb.raycastTarget = false;
        Sprite cover = coverProvider != null ? coverProvider(index) : null;
        string songId = ctl.SongIdAt(index);
        if (cover != null)
        {
            row.thumb.sprite = cover;
            row.thumb.color = row.locked ? new Color(0.45f, 0.45f, 0.55f) : Color.white;
        }
        else
        {
            row.thumb.sprite = UISkinKit.RoundedRect();
            row.thumb.type = Image.Type.Sliced;
            row.thumb.color = row.locked
                ? new Color(0.227f, 0.251f, 0.40f)
                : PlaceholderColor(songId);
            var chakra = UISkinKit.LogoFontAsset();
            var init = UISkinKit.MakeTMP(thumbGo.transform, "Initial",
                string.IsNullOrEmpty(songId) ? "?" : songId.Substring(0, 1).ToUpperInvariant(),
                24f, new Color(0.91f, 0.93f, 1f, 0.85f), TextAlignmentOptions.Center,
                Vector2.zero, new Vector2(56f, 56f), FontStyles.Normal, 0f, chakra);
            init.raycastTarget = false;
        }

        // 曲名(英タイトル)。ロック曲はグレー。
        string title = ResultSkin.SongIdToDisplayTitle(songId);
        row.title = UISkinKit.MakeTMP(go.transform, "Title", title, 26f,
            row.locked ? UISkinPalette.SubtleGray : UISkinPalette.OffWhite,
            TextAlignmentOptions.MidlineLeft,
            new Vector2(-30f, 0f), new Vector2(420f, 60f), FontStyles.Normal, 1f,
            UISkinKit.FontAsset("Oxanium-Bold"));

        // LOCKED チップ
        if (row.locked)
        {
            var chip = UISkinKit.MakeTMP(go.transform, "LockedChip", "LOCKED", 15f,
                UISkinPalette.SubtleGray, TextAlignmentOptions.MidlineRight,
                new Vector2(180f, 0f), new Vector2(120f, 30f), FontStyles.Normal, 2f,
                UISkinKit.FontAsset("Oxanium-Bold"));
            chip.raycastTarget = false;
        }

        // 現在難易度のレベル(右端)
        row.level = UISkinKit.MakeTMP(go.transform, "Level", "--", 32f,
            LockedText, TextAlignmentOptions.MidlineRight,
            new Vector2(292f, 0f), new Vector2(90f, 60f), FontStyles.Normal, 0f,
            UISkinKit.LogoFontAsset());

        int captured = index;
        var btn = go.GetComponent<Button>();
        btn.targetGraphic = row.fill;
        btn.transition = Selectable.Transition.None;
        btn.onClick.AddListener(() => { if (ctl != null) ctl.Select(captured); });

        return row;
    }

    // ---- 状態更新 ----

    // 選択が変わったらホイールを回す(0.3秒 easeOutCubic)。
    public void SetSelected(int index)
    {
        animFrom = CurrentPos();
        animTo = index;
        animAge = 0f;
        ApplySelectionStyling(index);
    }

    // 難易度変更(または初期化)でレベル数値と色を更新する。
    public void RefreshLevels(int difficultyIndex)
    {
        if (ctl == null) return;
        Color accent = accentProvider != null ? accentProvider(difficultyIndex) : UISkinPalette.Cyan;
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i]?.level == null) continue;
            int lv = ctl.DisplayLevelFor(i, difficultyIndex);
            rows[i].level.text = FormatLevel(lv);
            rows[i].level.color = lv > 0 ? accent : LockedText;
        }
    }

    void ApplySelectionStyling(int selectedIndex)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row == null) continue;
            bool on = i == selectedIndex;
            row.border.color = on ? new Color(69f / 255f, 1f, 247f / 255f, 0.95f) : RowLine;
            row.fill.color = on ? SelectedFill : RowFill;
            row.glow.color = on
                ? new Color(69f / 255f, 1f, 247f / 255f, 0.25f)
                : Color.clear;
        }
    }

    float CurrentPos()
    {
        float p = Mathf.Clamp01(animAge / SlideDuration);
        float ease = 1f - (1f - p) * (1f - p) * (1f - p);
        return Mathf.Lerp(animFrom, animTo, ease);
    }

    void Update()
    {
        if (animAge > SlideDuration + 0.1f) return;
        animAge += Time.unscaledDeltaTime;
        LayoutRows(CurrentPos());
    }

    // animPos(連続値)に基づいて全行を配置する。テストから直接呼べる。
    public void LayoutRows(float animPos)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row?.root == null) continue;
            float off = i - animPos;
            float abs = Mathf.Abs(off);
            row.root.anchoredPosition = new Vector2(0f, -off * RowStride);
            float s = RowScale(abs);
            row.root.localScale = new Vector3(s, s, 1f);
            float alpha = RowAlpha(abs);
            if (row.locked) alpha *= 0.55f;
            row.group.alpha = alpha;
            row.group.blocksRaycasts = abs <= 3f;
        }
    }

    // 曲IDから安定した色相のプレースホルダー色を作る(カバー未設定の曲)
    static Color PlaceholderColor(string songId)
    {
        int hash = 0;
        if (!string.IsNullOrEmpty(songId))
        {
            foreach (char c in songId) hash = hash * 31 + c;
        }
        float hue = Mathf.Abs(hash % 360) / 360f;
        return Color.HSVToRGB(hue, 0.50f, 0.55f);
    }

    static void MakeLine(Transform parent, Vector2 pos)
    {
        var go = new GameObject("Line", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(680f, 1f);
        rt.anchoredPosition = pos;
        var img = go.GetComponent<Image>();
        img.color = new Color(69f / 255f, 1f, 247f / 255f, 0.35f);
        img.raycastTarget = false;
    }

    static void StretchFull(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }
}
