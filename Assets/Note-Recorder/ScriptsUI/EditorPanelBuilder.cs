using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;
using TMPro;

// 譜面エディタの操作UIを、ランタイムで「日本語ラベル・カテゴリ色・グループ分け」の見やすいパネルへ組み直す。
//  ・シーン(.unity)は書き換えない。Play中のインスタンスだけ再構成するので、停止すれば元に戻る（完全に非破壊）。
//  ・既存ボタン/入力欄は再利用して配線を保持。足りないボタン（本編へ出力 / 前後移動 / ノーツ種類）は
//    新規作成して各コントローラの public メソッドへ配線する。
//  ・ChartManager のあるシーンでのみ自動実行。
public class EditorPanelBuilder : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoSpawn()
    {
        if (Object.FindFirstObjectByType<ChartManager>() == null) return;
        if (Object.FindFirstObjectByType<EditorPanelBuilder>() != null) return;
        new GameObject("EditorPanelBuilder").AddComponent<EditorPanelBuilder>();
    }

    private EditorControlUI ec;
    private SaveLoadUI slu;
    private ChartManager cm;
    private Font jpFont;
    private bool fontResolved;

    // カテゴリ色
    private static readonly Color ColFile  = new Color(0.20f, 0.45f, 0.85f, 0.95f);
    private static readonly Color ColEdit  = new Color(0.55f, 0.40f, 0.80f, 0.95f);
    private static readonly Color ColPlay  = new Color(0.18f, 0.62f, 0.45f, 0.95f);
    private static readonly Color ColNote  = new Color(0.85f, 0.55f, 0.20f, 0.95f);
    private static readonly Color ColMisc  = new Color(0.40f, 0.42f, 0.48f, 0.95f);
    private static readonly Color TextCol  = new Color(0.96f, 0.97f, 1f);

    void Start()
    {
        ec = Object.FindFirstObjectByType<EditorControlUI>();
        slu = Object.FindFirstObjectByType<SaveLoadUI>();
        cm = Object.FindFirstObjectByType<ChartManager>();
        Build();
    }

    // ---------------------------------------------------------------
    private void Build()
    {
        RectTransform topPanel = FindTopPanel();
        Canvas canvas = topPanel != null
            ? topPanel.GetComponentInParent<Canvas>()
            : Object.FindFirstObjectByType<Canvas>();
        if (canvas == null) return;

        Transform panel = CreatePanel(canvas.transform);

        AddTitle(panel, "譜面エディタ");

        // ノーツ種類（既存UIが無いので新規作成。現状の type 固定問題も解消）
        if (ec != null && FindButtonByMethod("OnNoteTypeChanged") == null)
        {
            var row = AddSection(panel, "ノーツの種類", ColNote);
            MakeButton(row, "タップ",   ColNote, () => ec.OnNoteTypeChanged(0));
            MakeButton(row, "フリック", ColNote, () => ec.OnNoteTypeChanged(1));
            MakeButton(row, "ロング",   ColNote, () => ec.OnNoteTypeChanged(2));
        }

        // 再生・移動
        {
            var row = AddSection(panel, "再生・移動", ColPlay);
            Place(row, FindButtonByMethod("OnPPrevPressed"), "◀◀ 前小節", ColPlay, () => ec?.OnPPrevPressed());
            Place(row, FindButtonByMethod("OnPrevPressed"),  "◀ 前",     ColPlay, () => ec?.OnPrevPressed());
            Place(row, FindButtonByMethod("OnStartStopPressed"), "再生 / 停止", ColPlay, () => ec?.OnStartStopPressed());
            Place(row, FindButtonByMethod("OnNextPressed"),  "次 ▶",     ColPlay, () => ec?.OnNextPressed());
            Place(row, FindButtonByMethod("OnNNextPressed"), "次小節 ▶▶", ColPlay, () => ec?.OnNNextPressed());
        }

        // 編集
        {
            var row = AddSection(panel, "編集", ColEdit);
            Place(row, FindButtonByMethod("OnUndoPressed"), "元に戻す",  ColEdit, () => ec?.OnUndoPressed());
            Place(row, FindButtonByMethod("OnRedoPressed"), "やり直し",  ColEdit, () => ec?.OnRedoPressed());
        }

        // 曲の設定（入力欄を再利用してラベル付け）
        {
            var box = AddSectionColumn(panel, "曲の設定", ColMisc);
            AddField(box, "BPM",       ec != null ? ec.bpmInputField : null);
            AddField(box, "OFFSET(秒)", ec != null ? ec.offsetInputField : null);
            AddField(box, "拍子",       ec != null ? ec.measureInputField : null);
            AddField(box, "現在の拍",   BeatField());
        }

        // ファイル
        {
            var row = AddSection(panel, "ファイル", ColFile);
            Place(row, FindButtonByMethod("OnSaveButtonClicked"), "保存",       ColFile, () => slu?.OnSaveButtonClicked());
            Place(row, FindButtonByMethod("OnLoadButtonClicked"), "読込",       ColFile, () => slu?.OnLoadButtonClicked());
            Place(row, FindButtonByMethod("OnExportButtonClicked"), "本編へ出力", ColFile, () => slu?.OnExportButtonClicked());
        }

        // 旧パネルに残った操作系を取りこぼさず移動 → 旧パネルを隠す
        RescueLeftovers(topPanel, panel);
        if (topPanel != null) topPanel.gameObject.SetActive(false);
    }

    private TMP_InputField BeatField()
    {
        if (cm != null && cm.beatInputField != null) return cm.beatInputField;
        if (ec != null && ec.beatInputField != null) return ec.beatInputField;
        return null;
    }

    // ---------------------------------------------------------------
    // 既存ボタン探索（onClick の配線先メソッド名で照合）
    // ---------------------------------------------------------------
    private Button FindButtonByMethod(string method)
    {
        foreach (var b in Object.FindObjectsByType<Button>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            int n = b.onClick.GetPersistentEventCount();
            for (int i = 0; i < n; i++)
                if (b.onClick.GetPersistentMethodName(i) == method) return b;
        }
        return null;
    }

    private RectTransform FindTopPanel()
    {
        if (ec != null && ec.bpmInputField != null) return ec.bpmInputField.transform.parent as RectTransform;
        var b = FindButtonByMethod("OnSaveButtonClicked");
        return b != null ? b.transform.parent as RectTransform : null;
    }

    // 既存があれば再利用（配線維持）、無ければ新規作成して action へ配線
    private void Place(Transform parent, Button existing, string label, Color color, UnityAction createAction)
    {
        if (existing != null)
        {
            StyleButton(existing, label, color);
            existing.transform.SetParent(parent, false);
            SetLE(existing.gameObject, 0, 46, 1);
        }
        else
        {
            MakeButton(parent, label, color, createAction);
        }
    }

    private void RescueLeftovers(RectTransform topPanel, Transform panel)
    {
        if (topPanel == null) return;
        var leftovers = new List<Transform>();
        foreach (Transform c in topPanel)
        {
            if (c.GetComponent<Button>() || c.GetComponent<TMP_InputField>() || c.GetComponent<InputField>()
                || c.GetComponent<TMP_Dropdown>() || c.GetComponent<Dropdown>())
                leftovers.Add(c);
        }
        if (leftovers.Count == 0) return;
        var row = AddSection(panel, "その他", ColMisc);
        foreach (var c in leftovers)
        {
            c.SetParent(row, false);
            SetLE(c.gameObject, 0, 44, 1);
        }
    }

    // ---------------------------------------------------------------
    // 生成ヘルパー
    // ---------------------------------------------------------------
    private Transform CreatePanel(Transform canvasT)
    {
        var go = new GameObject("EditorControlPanel", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(canvasT, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(1, 1);
        rt.pivot = new Vector2(1, 1);
        rt.anchoredPosition = new Vector2(-20, -50); // 上部ヒントバー(高さ42)の下に出す
        rt.sizeDelta = new Vector2(560, 100); // 高さは ContentSizeFitter が決める
        go.GetComponent<Image>().color = new Color(0.09f, 0.11f, 0.15f, 0.96f);

        var vlg = go.AddComponent<VerticalLayoutGroup>();
        vlg.padding = new RectOffset(16, 16, 14, 16);
        vlg.spacing = 12;
        vlg.childControlWidth = vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;

        var fit = go.AddComponent<ContentSizeFitter>();
        fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        return go.transform;
    }

    private void AddTitle(Transform parent, string text)
    {
        var t = MakeLabel(parent, text, 28, TextAnchor.MiddleLeft, Color.white, true);
        SetLE(t.gameObject, 0, 40);
    }

    // ヘッダ + 横並び行 を作り、行 Transform を返す
    private Transform AddSection(Transform parent, string header, Color accent)
    {
        var box = NewBox(parent, false, 8);
        AddHeader(box, header, accent);
        var row = NewBox(box, true, 8);
        return row;
    }

    // ヘッダ + 縦並び列 を作り、列 Transform を返す（入力欄セクション用）
    private Transform AddSectionColumn(Transform parent, string header, Color accent)
    {
        var box = NewBox(parent, false, 8);
        AddHeader(box, header, accent);
        var col = NewBox(box, false, 6);
        return col;
    }

    private void AddHeader(Transform parent, string text, Color accent)
    {
        var t = MakeLabel(parent, "● " + text, 17, TextAnchor.MiddleLeft, accent, true);
        SetLE(t.gameObject, 0, 24);
    }

    // 入力欄1行：ラベル + 既存 InputField を再利用
    private void AddField(Transform parent, string label, TMP_InputField field)
    {
        var row = NewBox(parent, true, 8);
        SetLE((row as Transform).gameObject, 0, 38);
        var lab = MakeLabel(row, label, 17, TextAnchor.MiddleLeft, TextCol, false);
        SetLE(lab.gameObject, 150, 34);
        if (field != null)
        {
            field.transform.SetParent(row, false);
            var le = field.GetComponent<LayoutElement>() ?? field.gameObject.AddComponent<LayoutElement>();
            le.minHeight = 34; le.preferredHeight = 34; le.flexibleWidth = 1;
        }
        else
        {
            var na = MakeLabel(row, "(未設定)", 15, TextAnchor.MiddleLeft, new Color(1, 0.6f, 0.6f), false);
            SetLE(na.gameObject, 0, 34, 1);
        }
    }

    private Transform NewBox(Transform parent, bool horizontal, float spacing)
    {
        var go = new GameObject(horizontal ? "Row" : "Box", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        HorizontalOrVerticalLayoutGroup lg = horizontal
            ? (HorizontalOrVerticalLayoutGroup)go.AddComponent<HorizontalLayoutGroup>()
            : go.AddComponent<VerticalLayoutGroup>();
        lg.spacing = spacing;
        lg.childControlWidth = lg.childControlHeight = true;
        lg.childForceExpandWidth = true;
        lg.childForceExpandHeight = false;
        return go.transform;
    }

    private void MakeButton(Transform parent, string label, Color color, UnityAction action)
    {
        var go = new GameObject("Btn_" + label, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var img = go.GetComponent<Image>();
        img.color = color;
        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img; // 押下時の色フィードバック用
        if (action != null) btn.onClick.AddListener(action);
        var t = MakeLabel(go.transform, label, 17, TextAnchor.MiddleCenter, Color.white, false);
        Stretch(t.rectTransform);
        SetLE(go, 0, 46, 1);
    }

    private void StyleButton(Button btn, string label, Color color)
    {
        if (btn.image != null) btn.image.color = color;
        var tmp = btn.GetComponentInChildren<TMP_Text>(true);
        if (tmp != null) { tmp.text = label; tmp.alignment = TextAlignmentOptions.Center; return; }
        var legacy = btn.GetComponentInChildren<Text>(true);
        if (legacy != null) { legacy.text = label; legacy.alignment = TextAnchor.MiddleCenter; return; }
        var t = MakeLabel(btn.transform, label, 17, TextAnchor.MiddleCenter, Color.white, false);
        Stretch(t.rectTransform);
    }

    private Text MakeLabel(Transform parent, string text, int size, TextAnchor anchor, Color color, bool bold)
    {
        var go = new GameObject("Label", typeof(RectTransform), typeof(Text));
        go.transform.SetParent(parent, false);
        var t = go.GetComponent<Text>();
        t.font = JpFont();
        t.text = text;
        t.fontSize = size;
        t.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
        t.alignment = anchor;
        t.color = color;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.raycastTarget = false;
        return t;
    }

    private static void SetLE(GameObject go, float prefW, float prefH, float flexW = 0)
    {
        var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
        if (prefW > 0) le.preferredWidth = prefW;
        if (prefH > 0) { le.preferredHeight = prefH; le.minHeight = prefH; }
        le.flexibleWidth = flexW;
    }

    private static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    private Font JpFont()
    {
        if (fontResolved) return jpFont;
        fontResolved = true;
        jpFont = Font.CreateDynamicFontFromOSFont(
            new[] { "Yu Gothic UI", "Yu Gothic", "Meiryo", "MS Gothic",
                    "Hiragino Sans", "Hiragino Kaku Gothic ProN", "Noto Sans CJK JP", "Arial" }, 18);
        if (jpFont == null) jpFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        return jpFont;
    }
}
