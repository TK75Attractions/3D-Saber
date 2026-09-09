using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

// 曲選択画面の金属パネル型「SONG WHEEL」。
// 選択行は常に画面中央に固定され、曲リスト側が回転して流れる。
// 各行はオフセット off = i - animPos から位置(off×104px)・縮尺・不透明度を計算する。
// アニメーションは animPos を easeOutCubic で 0.3 秒かけて目標へ動かすだけで全行が追従する。
public class SongWheelView : MonoBehaviour
{
    public const float RowHeight = 84f;
    public const float RowStride = 104f;
    public const float SlideDuration = 0.30f;

    static readonly Color RowLine = SongSelectVisuals.Edge;
    static readonly Color RowFill = SongSelectVisuals.Surface;
    static readonly Color SelectedFill = Color.Lerp(SongSelectVisuals.Surface,SongSelectVisuals.Accent,.17f);
    static readonly Color LockedText = SongSelectVisuals.Disabled;

    class Row
    {
        public RectTransform root;
        public CanvasGroup group;
        public SongSelectPanelGraphic fill;
        public SongSelectPanelGraphic marker;
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
        rt.sizeDelta = new Vector2(724f, 700f);
        rt.anchoredPosition = new Vector2(-490f, -44f);
        var view = go.AddComponent<SongWheelView>();
        view.ctl = controller;
        view.coverProvider = coverProvider;
        view.accentProvider = accentProvider;
        view.BuildContent();
        return view;
    }

    void BuildContent()
    {
        // 選択行の面取り枠自体を強調し、重複する外枠や後光は置かない。

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
        row.root.sizeDelta = new Vector2(724f, RowHeight);
        row.group = go.GetComponent<CanvasGroup>();
        row.locked = ctl.IsLocked(index);

        row.fill = SongSelectVisuals.Panel(go.transform,"Fill",Vector2.zero,Vector2.zero,RowFill,RowLine,8f);
        StretchFull(row.fill.rectTransform);
        row.fill.raycastTarget = true; // ボタンの当たり判定
        row.marker = SongSelectVisuals.Panel(go.transform,"SelectedMarker",new Vector2(-354,0),new Vector2(3,54),Color.clear,Color.clear,0);

        // サムネイル(cover.png。無ければ曲名頭文字入りのプレースホルダー)
        var thumbGo = new GameObject("Thumb", typeof(RectTransform), typeof(Image));
        thumbGo.transform.SetParent(go.transform, false);
        var trt = thumbGo.GetComponent<RectTransform>();
        trt.sizeDelta = new Vector2(56f, 56f);
        trt.anchoredPosition = new Vector2(-309f, 0f);
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
            row.thumb.color = SongSelectVisuals.Raised;
            var chakra = UISkinKit.FontAsset("Oxanium-Bold");
            string displayTitle = ResultSkin.SongIdToDisplayTitle(songId);
            var init = UISkinKit.MakeTMP(thumbGo.transform, "Initial",
                string.IsNullOrEmpty(displayTitle) ? "?" : displayTitle.Substring(0, 1).ToUpperInvariant(),
                24f, SongSelectVisuals.Muted, TextAlignmentOptions.Center,
                Vector2.zero, new Vector2(56f, 56f), FontStyles.Normal, 0f, chakra);
            init.raycastTarget = false;
        }

        // 曲名(英タイトル)。ロック曲はグレー。
        string title = ResultSkin.SongIdToDisplayTitle(songId);
        row.title = UISkinKit.MakeTMP(go.transform, "Title", title, 26f,
            row.locked ? SongSelectVisuals.Muted : SongSelectVisuals.Text,
            TextAlignmentOptions.MidlineLeft,
            new Vector2(-34f, row.locked ? 11f : 0f), new Vector2(442f, 50f), FontStyles.Normal, 0f,
            UISkinKit.FontAsset("Oxanium-Bold"));
        row.title.enableAutoSizing=true; row.title.fontSizeMin=19; row.title.fontSizeMax=26;
        row.title.overflowMode=TextOverflowModes.Ellipsis;

        // LOCKED チップ
        if (row.locked)
        {
            var chip = UISkinKit.MakeTMP(go.transform, "LockedChip", "譜面準備中", 14f,
                SongSelectVisuals.Muted, TextAlignmentOptions.MidlineLeft,
                new Vector2(-34f, -19f), new Vector2(442f, 22f), FontStyles.Normal, 0f,
                UISkinKit.FontAsset("Oxanium-Bold"));
            chip.raycastTarget = false;
        }

        // 現在難易度のレベル(右端)
        row.level = UISkinKit.MakeTMP(go.transform, "Level", "--", 32f,
            LockedText, TextAlignmentOptions.MidlineRight,
            new Vector2(306f, -7f), new Vector2(68f, 45f), FontStyles.Normal, 0f,
            UISkinKit.FontAsset("Oxanium-ExtraBold"));
        SongSelectVisuals.Label(go.transform,"LevelCaption","LV",12,new Vector2(306,23),new Vector2(68,18),SongSelectVisuals.Muted,TextAlignmentOptions.MidlineRight);

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
            row.fill.SetColors(on ? SelectedFill : RowFill,on ? SongSelectVisuals.Accent : RowLine);
            row.marker.color = on ? SongSelectVisuals.Accent : Color.clear;
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

    static void StretchFull(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }
}
