using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;

// 結果画面で、プレイ中に付けた印(ChartMarks)ごとに一言を打つ欄(2026-10-27、ユーザーの答え「曲のあとに打つ」)。
// 運営が F5 で印を付けたプレイだけ開く(来場者のふだんのプレイでは出ない)。
// 日本語入力は Input System の文字入力(onTextInput)と変換中の文字(onIMECompositionChange)で受ける。
// Enter で次の印、最後の Enter か F5 で閉じて保存。開いている間は「戻る」ボタン(Enter・照準・クリック)と Esc 2回を止める
// (日本語入力の取り消しの Esc でタイトルへ戻らないように)。何も打たずに20秒たつと閉じる(来場者を待たせない)。
public sealed class ChartMarkCommentPanel : MonoBehaviour
{
    public const float IdleCloseSeconds = 20f;
    public const int VisibleRows = 6;
    const float RowHeight = 78f, CardWidth = 1240f;
    const float BackspaceRepeatDelay = .45f, BackspaceRepeatInterval = .05f;

    static ChartMarkCommentPanel current;
    public static bool IsOpen => current != null;
    public static ChartMarkCommentPanel Current => current;

    ChartMarkFile file;
    readonly List<string> texts = new List<string>();
    readonly List<TextMeshProUGUI> labels = new List<TextMeshProUGUI>();
    readonly List<TextMeshProUGUI> fields = new List<TextMeshProUGUI>();
    readonly List<Image> bars = new List<Image>();
    TextMeshProUGUI footer;
    int row, firstVisible;
    string composition = "";
    int compositionEndFrame = -10;
    float lastInputAt, nextRepeatAt;
    GameObject previousSelection;
    EventSystem pausedEvents;
    bool previousNavigation = true;
    Keyboard keyboard;
    bool closed, cleaned, saveFailed;

    public bool SaveFailed => saveFailed;

    public int Row => row;
    public int Count => texts.Count;
    public string TextAt(int index) => index >= 0 && index < texts.Count ? texts[index] : "";

    public static ChartMarkCommentPanel Open(ChartMarkFile file)
    {
        if (file == null || file.marks == null || file.marks.Count == 0) return null;
        if (current != null) return current;
        var go = new GameObject("ChartMarkCommentPanel", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler),
            typeof(GraphicRaycaster));
        var panel = go.AddComponent<ChartMarkCommentPanel>();
        panel.file = file;
        foreach (var mark in file.marks) panel.texts.Add(mark.comment ?? "");
        panel.Build();
        panel.Begin();
        return panel;
    }

    void Build()
    {
        var canvas = GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        // 照準(900)より上、運営の知らせ(950)と暗転の幕より下。
        canvas.sortingOrder = 920;
        var scaler = GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = .5f;

        // 全面の下敷きは、結果画面の「戻る」への照準・クリックも受け止める(SaberUIPointer の RespectRaycastBlockers)。
        var backdrop = new GameObject("Backdrop", typeof(RectTransform), typeof(Image));
        backdrop.transform.SetParent(transform, false);
        SongSelectVisuals.Stretch(backdrop.GetComponent<RectTransform>());
        var shade = backdrop.GetComponent<Image>();
        shade.color = new Color(0f, 0f, 0f, .62f);
        shade.raycastTarget = true;

        int rows = Mathf.Min(VisibleRows, texts.Count);
        float height = 210f + RowHeight * rows;
        SongSelectVisuals.Panel(transform, "Card", Vector2.zero, new Vector2(CardWidth, height),
            new Color(.035f, .05f, .09f, .97f), new Color(1f, .74f, .32f, .9f), 12f);
        var ink = new Color(.93f, .96f, 1f);
        var muted = new Color(.62f, .7f, .78f);
        float top = height * .5f;
        SongSelectVisuals.Label(transform, "Title", "印のところで気になったことを、短く打ってください("
            + texts.Count + "か所)", 34, new Vector2(0, top - 46), new Vector2(CardWidth - 80, 52), ink,
            TextAlignmentOptions.Center, true);
        SongSelectVisuals.Label(transform, "Example", "例: ここのノーツいらない / 半拍おそく / 暗すぎて見えない",
            22, new Vector2(0, top - 88), new Vector2(CardWidth - 80, 34), muted, TextAlignmentOptions.Center);
        for (int i = 0; i < rows; i++)
        {
            float y = top - 150f - RowHeight * i;
            var bar = new GameObject("Row" + i, typeof(RectTransform), typeof(Image));
            bar.transform.SetParent(transform, false);
            var rt = bar.GetComponent<RectTransform>();
            rt.anchoredPosition = new Vector2(170f, y);
            rt.sizeDelta = new Vector2(860f, 62f);
            var image = bar.GetComponent<Image>();
            image.raycastTarget = false;
            bars.Add(image);
            labels.Add(SongSelectVisuals.Label(transform, "Mark" + i, "", 26, new Vector2(-438f, y), new Vector2(300f, 60f),
                ink, TextAlignmentOptions.MidlineLeft, true));
            var field = SongSelectVisuals.Label(transform, "Comment" + i, "", 28, new Vector2(170f, y), new Vector2(830f, 60f),
                ink, TextAlignmentOptions.MidlineLeft);
            field.overflowMode = TextOverflowModes.Ellipsis;
            field.richText = true;
            fields.Add(field);
        }
        footer = SongSelectVisuals.Label(transform, "Keys", "", 21, new Vector2(0, -top + 34), new Vector2(CardWidth - 60, 34),
            muted, TextAlignmentOptions.Center);
        foreach (var text in GetComponentsInChildren<TextMeshProUGUI>(true)) text.raycastTarget = false;
        Refresh(0f);
    }

    void Begin()
    {
        current = this;
        var events = EventSystem.current;
        if (events != null)
        {
            // ↑↓ で行を動かしても UI の選択が「戻る」へ移らないよう、ナビゲーション(Enter の決定を含む)も止める。
            previousSelection = events.currentSelectedGameObject;
            previousNavigation = events.sendNavigationEvents;
            events.SetSelectedGameObject(null);
            events.sendNavigationEvents = false;
            pausedEvents = events;
        }
        InputManager.EscapeSuppressed = true;
        keyboard = Keyboard.current;
        if (keyboard != null)
        {
            keyboard.onTextInput += OnText;
            keyboard.onIMECompositionChange += OnComposition;
            keyboard.SetIMEEnabled(true);
        }
        lastInputAt = Time.unscaledTime;
    }

    void Update()
    {
        if (closed) return;
        float now = Time.unscaledTime;
        if (keyboard != null)
        {
            if (keyboard.anyKey.wasPressedThisFrame) lastInputAt = now;
            if (keyboard[ChartMarkRecorder.MarkKey].wasPressedThisFrame) { Close(); return; }
            // 変換を決める Enter・Backspace は日本語入力のもの。変換中と、変換が終わったコマは数えない。
            bool composing = composition.Length > 0 || Time.frameCount - compositionEndFrame <= 1;
            if (!composing)
            {
                if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame) { Submit(); if (closed) return; }
                else if (keyboard.upArrowKey.wasPressedThisFrame) Move(-1);
                else if (keyboard.downArrowKey.wasPressedThisFrame || keyboard.tabKey.wasPressedThisFrame) Move(1);
                Backspace(now);
            }
        }
        if (now - lastInputAt > IdleCloseSeconds) { Close(true); return; }
        Refresh(now);
    }

    void Backspace(float now)
    {
        var key = keyboard.backspaceKey;
        if (key.wasPressedThisFrame) { DeleteLast(); nextRepeatAt = now + BackspaceRepeatDelay; }
        else if (key.isPressed && now >= nextRepeatAt) { DeleteLast(); nextRepeatAt = now + BackspaceRepeatInterval; }
    }

    void OnText(char c)
    {
        if (closed || char.IsControl(c)) return;
        Type(c.ToString());
    }

    void OnComposition(IMECompositionString value)
    {
        string text = value.ToString();
        if (composition.Length > 0 && text.Length == 0) compositionEndFrame = Time.frameCount;
        composition = text;
        lastInputAt = Time.unscaledTime;
    }

    // 決まった文字を今の行の末尾に足す(テストからも使う)。
    public void Type(string text)
    {
        if (closed || string.IsNullOrEmpty(text) || texts.Count == 0) return;
        string next = texts[row] + text;
        texts[row] = next.Length > ChartMarks.CommentLimit ? next.Substring(0, ChartMarks.CommentLimit) : next;
        lastInputAt = Time.unscaledTime;
    }

    public void DeleteLast()
    {
        if (closed || texts.Count == 0 || texts[row].Length == 0) return;
        string text = texts[row];
        int cut = text.Length >= 2 && char.IsLowSurrogate(text[text.Length - 1]) ? 2 : 1;
        texts[row] = text.Substring(0, text.Length - cut);
    }

    public void Move(int step)
    {
        if (closed || texts.Count == 0) return;
        ChartMarks.SetComment(file.marks[row].number, texts[row]);
        row = Mathf.Clamp(row + step, 0, texts.Count - 1);
    }

    // 今の行を確かめて次へ。最後の行なら閉じて保存。
    public void Submit()
    {
        if (closed || texts.Count == 0) return;
        ChartMarks.SetComment(file.marks[row].number, texts[row]);
        ChartMarks.Save();
        if (row + 1 < texts.Count) { row++; return; }
        Close();
    }

    // 保存できなかったときは閉じずに理由を出す。もう一度閉じる操作(F5・最後の Enter)か、何も打たずに時間がたつと、
    // 保存できないまま閉じる(来場者を待たせ続けない)。
    public void Close() => Close(false);

    void Close(bool force)
    {
        if (closed) return;
        bool saved = SaveAll();
        if (!saved && !force && !saveFailed)
        {
            saveFailed = true;
            lastInputAt = Time.unscaledTime;
            return;
        }
        closed = true;
        var recorder = FindFirstObjectByType<ChartMarkRecorder>();
        if (recorder != null)
            recorder.ShowToast(saved ? "印を保存しました。AI に「印を直して」と伝えると直します"
                : "印の一言を保存できませんでした(" + ChartMarks.LastSaveError + ")", saved ? 4f : 6f);
        if (!Application.isPlaying) { Cleanup(); DestroyImmediate(gameObject); return; }
        // 閉じた Enter で、そのコマのうちに「戻る」まで押さないよう、選択とナビゲーションは次のコマで戻す。
        GetComponent<Canvas>().enabled = false;
        StartCoroutine(FinishNextFrame());
    }

    IEnumerator FinishNextFrame()
    {
        yield return null;
        Cleanup();
        Destroy(gameObject);
    }

    bool SaveAll()
    {
        if (file == null) return false;
        for (int i = 0; i < texts.Count && i < file.marks.Count; i++)
            ChartMarks.SetComment(file.marks[i].number, texts[i]);
        return ChartMarks.Save();
    }

    void Cleanup()
    {
        if (cleaned) return;
        cleaned = true;
        if (keyboard != null)
        {
            keyboard.onTextInput -= OnText;
            keyboard.onIMECompositionChange -= OnComposition;
            keyboard.SetIMEEnabled(false);
        }
        InputManager.EscapeSuppressed = false;
        if (pausedEvents != null)
        {
            pausedEvents.sendNavigationEvents = previousNavigation;
            if (previousSelection != null && previousSelection.activeInHierarchy)
                pausedEvents.SetSelectedGameObject(previousSelection);
        }
        if (current == this) current = null;
    }

    void Refresh(float now)
    {
        if (texts.Count == 0) return;
        if (row < firstVisible) firstVisible = row;
        if (row >= firstVisible + fields.Count) firstVisible = row - fields.Count + 1;
        bool caretOn = Mathf.Repeat(now, 1f) < .55f;
        for (int i = 0; i < fields.Count; i++)
        {
            int index = firstVisible + i;
            bool active = index == row;
            var mark = file.marks[index];
            labels[i].text = "印" + mark.number + "  " + ChartMarks.Clock(mark.songTime) + "  " + mark.bar + "小節目";
            string typed = "<noparse>" + texts[index] + "</noparse>";
            if (active)
            {
                if (composition.Length > 0) typed += "<u><noparse>" + composition + "</noparse></u>";
                typed += caretOn ? "<color=#FFC85A>|</color>" : "<color=#00000000>|</color>";
            }
            else if (texts[index].Length == 0) typed = "<color=#5E6B78>(なし)</color>";
            fields[i].text = typed;
            bars[i].color = active ? new Color(1f, .74f, .32f, .16f) : new Color(1f, 1f, 1f, .04f);
        }
        int left = Mathf.Max(0, Mathf.CeilToInt(IdleCloseSeconds - (now - lastInputAt)));
        footer.text = saveFailed
            ? "<color=#FF8A7A>保存できませんでした(<noparse>" + ChartMarks.LastSaveError + "</noparse>)。もう一度 F5 で、保存せずに閉じます</color>"
            : "Enter 次へ   ↑↓ 移動   F5 閉じる(打った分は保存)   何も打たずに " + left + " 秒で閉じます";
        footer.richText = true;
        if (keyboard != null && fields.Count > 0)
        {
            int visibleRow = row - firstVisible;
            var rt = fields[visibleRow].rectTransform;
            float width = Mathf.Min(rt.rect.width, fields[visibleRow].GetPreferredValues("<noparse>" + texts[row] + "</noparse>").x);
            Vector3 world = rt.TransformPoint(new Vector3(rt.rect.xMin + width, rt.rect.yMin, 0f));
            // 画面の座標は左下が原点、日本語入力の候補の位置は左上が原点(Keyboard.SetIMECursorPosition)。
            Vector2 screen = RectTransformUtility.WorldToScreenPoint(null, world);
            keyboard.SetIMECursorPosition(new Vector2(screen.x, Screen.height - screen.y));
        }
    }

    void OnDestroy()
    {
        if (!closed) SaveAll();
        closed = true;
        Cleanup();
    }
}
