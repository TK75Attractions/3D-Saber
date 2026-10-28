using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;

// 結果画面で、プレイ中に付けた印(ChartMarks)ごとに一言を打つ欄(2026-10-27、ユーザーの答え「曲のあとに打つ」)。
// 運営が F5(かスマホ)で印を付けたプレイだけ開く(来場者のふだんのプレイでは出ない)。結果画面で F5 を押すと何度でも開き直せる。
// 日本語入力は Input System の文字入力(onTextInput)と変換中の文字(onIMECompositionChange)で受ける。
// Enter で次の印、最後の Enter か F5 で閉じて保存(変換中は F5 で閉じない)。Delete でその印を取り消す / 戻す。
// 打った一言は1秒あけて下書きを保存し、閉じるときは必ず保存する(変換中の文字も含める。保存できなければ赤字で知らせて閉じない)。
// 開いている間は「戻る」ボタン(Enter・照準・クリック)と Esc 2回、運営の F キー(F4・F7〜F10)を止める
// (日本語入力の取り消しの Esc や、F6〜F10 の変換で運営の操作が動かないように)。
// 何も打たずに30秒たつと閉じる(来場者を待たせない)。打ったあと・変換中は時間切れで閉じない。
// スマホで打った一言は、この欄で打っていない行にそのまま出る(2026-10-28)。
public sealed class ChartMarkCommentPanel : MonoBehaviour
{
    public const float IdleCloseSeconds = 30f;
    public const float DraftSaveDelaySeconds = 1f;
    public const int VisibleRows = 6;
    public const string KeysGuide = "Enter: 次の印 / ↑↓: 移動 / Delete: この印を取り消す / F5: 保存して閉じる(結果画面で F5 でもう一度開けます)";
    public const string ConfirmFirstNotice = "変換を確定してから F5";
    public const string SavedNotice = "印の一言を保存しました。直すときは AI に「印を直して」と伝えてください";
    const float RowHeight = 78f, CardWidth = 1240f, FooterLine = 30f;
    const float BackspaceRepeatDelay = .45f, BackspaceRepeatInterval = .05f;
    const float NoticeSeconds = 3f, RecountSeconds = 2f;
    const string Muted = "#5E6B78";

    static ChartMarkCommentPanel current;
    public static bool IsOpen => current != null;
    public static ChartMarkCommentPanel Current => current;
    // 閉じたコマ(結果画面の F5 で開き直すとき、閉じた F5 と同じコマで開かない)。
    public static int LastClosedFrame { get; private set; } = -10;

    ChartMarkFile file;
    readonly List<string> texts = new List<string>();
    // この欄で打った行(以後はこの欄の文字を正とし、スマホの一言で上書きしない)と、まだ保存していない行。
    readonly List<bool> edited = new List<bool>();
    readonly List<bool> dirty = new List<bool>();
    readonly List<TextMeshProUGUI> labels = new List<TextMeshProUGUI>();
    readonly List<TextMeshProUGUI> fields = new List<TextMeshProUGUI>();
    readonly List<Image> bars = new List<Image>();
    TextMeshProUGUI footer, counts, place, phone;
    int row, firstVisible;
    string composition = "";
    int compositionEndFrame = -10, openedFrame;
    // clock は最後に見た時刻(Update は実時間、テストは Tick に渡した時刻)。
    float clock, lastInputAt, nextRepeatAt, draftDueAt = -1f, noticeUntil, nextCountAt;
    string notice = "", lastError = "", folderTail = "";
    bool typed;
    int openOnPc, openThisPlay;
    GameObject previousSelection;
    EventSystem pausedEvents;
    bool previousNavigation = true;
    Keyboard keyboard;
    bool closed, cleaned, saveFailed, draftFailed, closeAnyway;

    // 閉じるときに保存できなかった(もう一度閉じる操作で、保存せずに閉じる)。
    public bool SaveFailed => saveFailed;
    // 下書きの保存に失敗している(閉じるのは止めない)。
    public bool DraftFailed => draftFailed;
    // この欄で何か打ったか(打ったあとは時間切れで閉じない)。
    public bool Typed => typed;
    public int Row => row;
    public int Count => texts.Count;
    public string Composition => composition;
    public string TextAt(int index) => index >= 0 && index < texts.Count ? texts[index] : "";
    public bool IsCanceled(int index)
        => file != null && index >= 0 && index < file.marks.Count && file.marks[index].status == ChartMarks.StatusCanceled;
    public string FooterText => footer != null ? footer.text : "";
    public string CountsText => counts != null ? counts.text : "";
    public string PlaceText => place != null ? place.text : "";
    public string PhoneText => phone != null ? phone.text : "";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { current = null; LastClosedFrame = -10; PhoneSaberOperatorKeys.TextEntryActive = false; }

    public static ChartMarkCommentPanel Open(ChartMarkFile file)
    {
        if (file == null || file.marks == null || file.marks.Count == 0) return null;
        if (current != null) return current;
        var go = new GameObject("ChartMarkCommentPanel", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler),
            typeof(GraphicRaycaster));
        var panel = go.AddComponent<ChartMarkCommentPanel>();
        panel.file = file;
        foreach (var mark in file.marks)
        {
            panel.texts.Add(mark.comment ?? "");
            panel.edited.Add(false);
            panel.dirty.Add(false);
        }
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
        // 下の案内は4行(キー・直していない印の数・保存先・スマホのアドレス)。
        float height = 210f + RowHeight * rows + FooterLine * 3f;
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
            var label = SongSelectVisuals.Label(transform, "Mark" + i, "", 26, new Vector2(-438f, y), new Vector2(300f, 60f),
                ink, TextAlignmentOptions.MidlineLeft, true);
            label.richText = true;
            labels.Add(label);
            var field = SongSelectVisuals.Label(transform, "Comment" + i, "", 28, new Vector2(170f, y), new Vector2(830f, 60f),
                ink, TextAlignmentOptions.MidlineLeft);
            field.overflowMode = TextOverflowModes.Ellipsis;
            field.richText = true;
            fields.Add(field);
        }
        float bottom = -top + 34f;
        footer = FooterLabel("Keys", 21, bottom + FooterLine * 3f, muted);
        counts = FooterLabel("Counts", 20, bottom + FooterLine * 2f, muted);
        place = FooterLabel("Folder", 20, bottom + FooterLine, muted);
        phone = FooterLabel("Phone", 20, bottom, muted);
        foreach (var text in GetComponentsInChildren<TextMeshProUGUI>(true)) text.raycastTarget = false;
        // 右にスマホで印の QR(F5 で出していた単独のカードは閉じる)。
        ChartMarkPhoneCard.HideStandalone();
        ChartMarkPhoneCard.AttachTo(transform, CardWidth * .5f);
    }

    TextMeshProUGUI FooterLabel(string name, float size, float y, Color color)
    {
        var label = SongSelectVisuals.Label(transform, name, "", size, new Vector2(0, y), new Vector2(CardWidth - 60, 32),
            color, TextAlignmentOptions.Center);
        label.richText = true;
        label.overflowMode = TextOverflowModes.Ellipsis;
        return label;
    }

    void Begin()
    {
        current = this;
        openedFrame = Time.frameCount;
        clock = Time.unscaledTime;
        PauseEvents();
        InputManager.EscapeSuppressed = true;
        PhoneSaberOperatorKeys.TextEntryActive = true;
        keyboard = Keyboard.current;
        if (keyboard != null)
        {
            keyboard.onTextInput += OnText;
            keyboard.onIMECompositionChange += OnComposition;
            keyboard.SetIMEEnabled(true);
        }
        lastInputAt = clock;
        folderTail = ChartMarks.FolderTail();
        CountOpen();
        nextCountAt = clock + RecountSeconds;
        Refresh(clock);
    }

    // ↑↓ で行を動かしても UI の選択が「戻る」へ移らないよう、ナビゲーション(Enter の決定を含む)も止める。
    // 暗転中に開いたとき(EventSystem が止まっていて current が無い)は、戻ったコマの Update で止める。
    void PauseEvents()
    {
        var events = EventSystem.current;
        if (events == null || pausedEvents != null) return;
        previousSelection = events.currentSelectedGameObject;
        previousNavigation = events.sendNavigationEvents;
        events.SetSelectedGameObject(null);
        events.sendNavigationEvents = false;
        pausedEvents = events;
    }

    void Update()
    {
        if (closed) return;
        if (pausedEvents == null) PauseEvents();
        float now = Time.unscaledTime;
        clock = now;
        // 開いたコマの F5・Enter は、開いた操作のもの。
        if (keyboard != null && Time.frameCount != openedFrame) HandleKeys(now);
        if (closed) return;
        Tick(now);
    }

    void HandleKeys(float now)
    {
        if (keyboard.anyKey.wasPressedThisFrame) lastInputAt = now;
        // 変換を決める Enter・Backspace・Delete・F5 は日本語入力のもの。変換中と、変換が終わったコマは数えない。
        bool composing = composition.Length > 0 || Time.frameCount - compositionEndFrame <= 1;
        if (keyboard[ChartMarkRecorder.MarkKey].wasPressedThisFrame)
        {
            if (composing) ShowNotice(ConfirmFirstNotice);
            else { Close(); return; }
        }
        if (composing) return;
        if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame) { Submit(); if (closed) return; }
        else if (keyboard.upArrowKey.wasPressedThisFrame) Move(-1);
        else if (keyboard.downArrowKey.wasPressedThisFrame || keyboard.tabKey.wasPressedThisFrame) Move(1);
        else if (keyboard.deleteKey.wasPressedThisFrame) ToggleCanceled();
        Backspace(now);
    }

    // 下書きの保存・時間切れ・表示(Update から。テストは時刻を進めて呼ぶ)。
    public void Tick(float now)
    {
        if (closed) return;
        clock = now;
        if (draftDueAt >= 0f && now >= draftDueAt)
        {
            draftDueAt = -1f;
            SaveAll();
        }
        // 何も打たずに放っておいたときだけ閉じる(打ったあと・変換中は閉じない)。閉じるときも保存する。
        if (!typed && composition.Length == 0 && now - lastInputAt > IdleCloseSeconds)
        {
            Close();
            if (closed) return;
        }
        if (now >= nextCountAt) { CountOpen(); nextCountAt = now + RecountSeconds; }
        Pull();
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

    void OnComposition(IMECompositionString value) => Compose(value.ToString());

    // 変換中の文字(日本語入力。テストからも使う)。変換中は打ったとみなし、時間切れで閉じない。
    public void Compose(string text)
    {
        if (closed) return;
        text = text ?? "";
        if (composition.Length > 0 && text.Length == 0) compositionEndFrame = Time.frameCount;
        composition = text;
        if (text.Length > 0) typed = true;
        lastInputAt = clock;
    }

    // 決まった文字を今の行の末尾に足す(テストからも使う)。
    public void Type(string text)
    {
        if (closed || string.IsNullOrEmpty(text) || texts.Count == 0) return;
        Append(text);
        lastInputAt = clock;
    }

    void Append(string text)
    {
        string next = texts[row] + text;
        texts[row] = next.Length > ChartMarks.CommentLimit ? next.Substring(0, ChartMarks.CommentLimit) : next;
        Edited();
    }

    // 打ったら1秒あけて下書きを保存する(途中でアプリが落ちても一言が残る)。
    void Edited()
    {
        edited[row] = dirty[row] = true;
        typed = true;
        draftDueAt = clock + DraftSaveDelaySeconds;
    }

    public void DeleteLast()
    {
        if (closed || texts.Count == 0 || texts[row].Length == 0) return;
        string text = texts[row];
        int cut = text.Length >= 2 && char.IsLowSurrogate(text[text.Length - 1]) ? 2 : 1;
        texts[row] = text.Substring(0, text.Length - cut);
        Edited();
    }

    public void Move(int step)
    {
        if (closed || texts.Count == 0) return;
        if (dirty[row]) SaveAll();
        int left = row;
        row = Mathf.Clamp(row + step, 0, texts.Count - 1);
        if (left != row) Release(left);
    }

    // 今の行を保存して次へ。最後の行なら閉じて保存。
    public void Submit()
    {
        if (closed || texts.Count == 0) return;
        SaveAll();
        if (row + 1 < texts.Count) { Release(row); row++; return; }
        Close();
    }

    // 離れた行は、保存できていればスマホの一言に追従させる(古い表示のまま、あとでこの欄から上書きしない)。
    void Release(int index)
    {
        if (index >= 0 && index < edited.Count && !dirty[index]) edited[index] = false;
    }

    // アプリを終えるとき(ChartMarkRecorder から): 変換中の文字も含めて書く。
    public void Flush()
    {
        if (closed) return;
        if (composition.Length > 0 && texts.Count > 0) { Append(composition); composition = ""; }
        SaveAll();
    }

    // Delete: 今の行の印を取り消す / 戻す(取り消した印は灰色・取り消し線。保存して、marks.py の一覧には出さない)。
    public void ToggleCanceled()
    {
        if (closed || texts.Count == 0) return;
        var mark = file.marks[row];
        bool cancel = mark.status != ChartMarks.StatusCanceled;
        bool same = ReferenceEquals(file, ChartMarks.Current);
        // 打った一言も同じ書き込みで残す。
        if (same) PushComments();
        if (ChartMarks.SetCanceled(mark.id, cancel, out string error))
        {
            if (same) ClearDirty();
            else mark.status = cancel ? ChartMarks.StatusCanceled : ChartMarks.StatusOpen;
        }
        else ShowNotice("取り消しを保存できませんでした(" + error + ")");
        CountOpen();
    }

    // 閉じて保存。変換中の文字も一言に含める。保存できなかったときは閉じずに赤字で知らせ、もう一度閉じる操作
    // (F5・最後の Enter・何も打たないままの時間切れ)で、保存できないまま閉じる(来場者を待たせ続けない)。
    public void Close()
    {
        if (closed) return;
        if (composition.Length > 0)
        {
            Append(composition);
            composition = "";
        }
        bool saved = SaveAll();
        if (!saved && !closeAnyway)
        {
            closeAnyway = true;
            saveFailed = true;
            lastInputAt = clock;
            return;
        }
        // 保存できないまま閉じても、この回の一言はメモリに残り(ChartMarks.Unsaved)、あとで書き直す。前の回の欄の分はログに残す。
        if (!saved) LogUnsavedTexts();
        closed = true;
        LastClosedFrame = Time.frameCount;
        var recorder = ChartMarkRecorder.Instance != null ? ChartMarkRecorder.Instance : FindFirstObjectByType<ChartMarkRecorder>();
        if (recorder != null)
            recorder.ShowToast(saved ? SavedNotice : "印の一言をまだ保存できていません(" + lastError + ")。あとで保存し直します", saved ? 4f : 6f);
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

    // 打った行をメモリへ(この回の印)。
    void PushComments()
    {
        for (int i = 0; i < texts.Count && i < file.marks.Count; i++)
            if (dirty[i]) file.marks[i].comment = ChartMarks.Clean(texts[i]);
    }

    // 全部の行を書けたとき。いま打っている行のほかは、スマホの一言に追従させる(保存に失敗したまま離れた行も)。
    void ClearDirty()
    {
        for (int i = 0; i < dirty.Count; i++) dirty[i] = false;
        for (int i = 0; i < edited.Count; i++) if (i != row) edited[i] = false;
        saveFailed = draftFailed = closeAnyway = false;
        lastError = "";
    }

    void LogUnsavedTexts()
    {
        if (ReferenceEquals(file, ChartMarks.Current)) return;
        for (int i = 0; i < texts.Count && i < file.marks.Count; i++)
            if (dirty[i]) Debug.LogWarning("[ChartMarks] 書けなかった一言 " + file.marks[i].id + ": " + texts[i]);
    }

    bool AnyDirty()
    {
        foreach (bool unsaved in dirty) if (unsaved) return true;
        return false;
    }

    bool SaveAll()
    {
        if (file == null) return false;
        bool ok;
        if (ReferenceEquals(file, ChartMarks.Current))
        {
            // 打った行が無く、前の保存も済んでいれば書かない(最後の Enter は Submit と Close が続けて呼ぶ)。
            if (!AnyDirty() && !ChartMarks.CurrentUnsaved)
            {
                ClearDirty();
                CountOpen();
                return true;
            }
            PushComments();
            ok = ChartMarks.Save();
            if (!ok) lastError = ChartMarks.LastSaveError;
        }
        else
        {
            // 場面が変わって次の回が始まったあと(ふつうは起きない)。前の回のファイルへ ID で書く。
            ok = true;
            for (int i = 0; i < texts.Count && i < file.marks.Count; i++)
            {
                if (!dirty[i]) continue;
                if (ChartMarks.SetComment(file.marks[i].id, texts[i], out string error)) file.marks[i].comment = ChartMarks.Clean(texts[i]);
                else { ok = false; lastError = error; }
            }
        }
        if (ok) ClearDirty();
        else draftFailed = true;
        CountOpen();
        return ok;
    }

    // 直していない印の数(この PC と、この回)。開いたとき・保存・取り消しのたびと、2秒ごと(スマホの取り消し)。
    void CountOpen()
    {
        openOnPc = ChartMarks.OpenCountOnThisPc();
        openThisPlay = 0;
        if (file?.marks != null)
            foreach (var mark in file.marks)
                if (mark != null && (string.IsNullOrEmpty(mark.status) || mark.status == ChartMarks.StatusOpen)) openThisPlay++;
    }

    // スマホで打った一言(この欄で打っていない行だけ)を出す。
    void Pull()
    {
        for (int i = 0; i < texts.Count && i < file.marks.Count; i++)
            if (!edited[i]) texts[i] = file.marks[i].comment ?? "";
    }

    void ShowNotice(string text)
    {
        notice = text ?? "";
        noticeUntil = clock + NoticeSeconds;
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
        PhoneSaberOperatorKeys.TextEntryActive = false;
        if (pausedEvents != null)
        {
            pausedEvents.sendNavigationEvents = previousNavigation;
            // 開く前の選択へ戻す。無ければ結果画面の「戻る」(閉じたあとはいつもの Enter で進める)。
            var target = previousSelection != null && previousSelection.activeInHierarchy ? previousSelection : ResultBackButton();
            if (target != null) pausedEvents.SetSelectedGameObject(target);
        }
        if (current == this) current = null;
    }

    static GameObject ResultBackButton()
    {
        var skin = FindFirstObjectByType<ResultSkin>();
        if (skin == null || skin.BackButton == null) return null;
        var button = skin.BackButton.gameObject;
        return button.activeInHierarchy ? button : null;
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
            bool canceled = mark.status == ChartMarks.StatusCanceled;
            string label = "印" + mark.number + "  " + ChartMarks.Clock(mark.songTime) + "  " + mark.bar + "小節目";
            labels[i].text = canceled ? "<color=" + Muted + "><s>" + label + "</s></color>" : label;
            string typedText = "<noparse>" + texts[index] + "</noparse>";
            if (active)
            {
                if (composition.Length > 0) typedText += "<u><noparse>" + composition + "</noparse></u>";
                typedText += caretOn ? "<color=#FFC85A>|</color>" : "<color=#00000000>|</color>";
            }
            else if (texts[index].Length == 0) typedText = "<color=" + Muted + ">(なし)</color>";
            if (canceled) typedText = "<color=" + Muted + "><s>" + typedText + "</s>  (取り消し)</color>";
            fields[i].text = typedText;
            bars[i].color = canceled
                ? new Color(1f, 1f, 1f, active ? .09f : .02f)
                : active ? new Color(1f, .74f, .32f, .16f) : new Color(1f, 1f, 1f, .04f);
        }
        if (saveFailed)
            footer.text = "<color=#FF8A7A>保存できませんでした(<noparse>" + lastError + "</noparse>)。もう一度 F5 で閉じます(一言は残して、あとで保存し直します)</color>";
        else if (draftFailed)
            footer.text = "<color=#FF8A7A>下書きを保存できませんでした(<noparse>" + lastError + "</noparse>)。打った分はこの欄に残っています</color>";
        else if (notice.Length > 0 && now < noticeUntil)
            footer.text = "<color=#FFC85A><noparse>" + notice + "</noparse></color>";
        else if (composition.Length > 0)
            footer.text = "<color=#FFC85A>" + ConfirmFirstNotice + "</color>";   // 変換中の Enter・↑↓・Delete・F5 は日本語入力のもの
        else
            footer.text = KeysGuide;
        string count = "この PC に直していない印が " + openOnPc + "件(この回 " + openThisPlay + "件)";
        if (!typed && composition.Length == 0)
            count += "   何も打たずに " + Mathf.Max(0, Mathf.CeilToInt(IdleCloseSeconds - (now - lastInputAt))) + " 秒で閉じます";
        counts.text = count;
        place.text = "保存先: <noparse>" + folderTail + "</noparse>";
        string url = ChartMarkPhoneLink.Url;
        phone.text = string.IsNullOrEmpty(url) ? "" : "スマホで印: <noparse>" + url + "</noparse>";
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
        if (!closed)
        {
            // 場面が変わって閉じる前に消えたときも、打った分(変換中の文字も)を残す。
            if (composition.Length > 0 && texts.Count > 0) { Append(composition); composition = ""; }
            SaveAll();
        }
        closed = true;
        Cleanup();
    }
}
