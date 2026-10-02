using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Object = UnityEngine.Object;

// 音を聴く → 試す → 保存。中央のプレイ領域と下端のセーバー操作帯を分ける。
public sealed class CalibrationOverlay : MonoBehaviour
{
    static readonly Color Ink = new Color(.91f,.97f,1), Muted = new Color(.65f,.76f,.82f),
        Cyan = new Color(.43f,.93f,.87f), Blue = new Color(.4f,.72f,1), Coral = new Color(1,.59f,.48f),
        PanelColor = new Color(.025f,.043f,.065f,.97f);
    CalibrationController ctl;
    TextMeshProUGUI feedback, feedbackDetail, value, state, instruction, volume, connections, timer;
    TextMeshProUGUI trendTitle, trendCounts, savedValue, device, readyDetail;
    TextMeshProUGUI resultTitle, resultValue, resultDetail, resultMessage, measurementStatus;
    readonly SongSelectCountdown countdown = new SongSelectCountdown();
    readonly List<Button> adjustments = new List<Button>();
    readonly int[] deltas = { -10, -1, 1, 10 };
    double lastTick;
    Button play, save, back, measure, restore, resultPrimary, resultRetry;
    GameObject settings, exitDialog, readyCard, resultCard;
    CalibrationTimingGraphic timing;
    Button[] profiles, speeds;
    CalibrationRunMode shownMode = (CalibrationRunMode)(-1);
    int shownOffset = int.MinValue, shownSpeed = -1;
    bool shownDirty;
    public double RemainingSeconds => countdown.Remaining;
    public int SpeedButtonCount => speeds != null ? speeds.Length : 0;
    public string SpeedCaption(int step) => speeds != null && step >= 0 && step < speeds.Length ? speeds[step].GetComponentInChildren<TextMeshProUGUI>(true).text : "";
    public bool IsExitDialogOpen => exitDialog != null && exitDialog.activeSelf;
    public bool IsSettingsOpen => settings != null && settings.activeSelf;
    public bool IsResultOpen => resultCard != null && resultCard.activeSelf;
    public string FeedbackText => feedback != null ? feedback.text : "";

    public static CalibrationOverlay Ensure()
    {
        var existing = Object.FindFirstObjectByType<CalibrationOverlay>(); if (existing != null) return existing;
        var go = new GameObject("CalibrationOverlay",typeof(Canvas),typeof(CanvasScaler),typeof(GraphicRaycaster));
        var canvas = go.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 100;
        var scaler = go.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920,1080); scaler.matchWidthOrHeight = .5f;
        var overlay = go.AddComponent<CalibrationOverlay>(); overlay.Build(); return overlay;
    }
    public void Bind(CalibrationController controller) { ctl = controller; Refresh(); }
    RectTransform Rect(Transform p,string name,float x,float y,float w,float h) =>
        SongSelectVisuals.Rect(p,name,new Vector2(x,y),new Vector2(w,h));
    Transform Panel(Transform p,string name,float x,float y,float w,float h) =>
        SongSelectVisuals.Panel(p,name,new Vector2(x,y),new Vector2(w,h),PanelColor,new Color(.22f,.39f,.45f),12).transform;
    TextMeshProUGUI Label(Transform p,string name,string text,float size,float x,float y,float w,float h,Color color,bool center = true)
    {
        var t = SongSelectVisuals.Label(p,name,text,size,new Vector2(x,y),new Vector2(w,h),color,
            center ? TextAlignmentOptions.Center : TextAlignmentOptions.MidlineLeft,false);
        t.textWrappingMode = TextWrappingModes.Normal; t.overflowMode = TextOverflowModes.Ellipsis; t.raycastTarget = false; return t;
    }
    Button Action(Transform p,string name,string text,float x,float y,float w,float h,UnityEngine.Events.UnityAction click,bool main = false)
    {
        var rt = Rect(p,name,x,y,w,h); var b = rt.gameObject.AddComponent<Button>();
        // ボタン全体が滞留対象。選曲用の立体ノーツと余白を持ち込まない。
        var face = SongSelectVisuals.Panel(rt,"ActionSurface",Vector2.zero,Vector2.zero,PanelColor,Cyan,8);
        SongSelectVisuals.Stretch(face.rectTransform); face.raycastTarget = true;
        b.targetGraphic = face; b.transition = Selectable.Transition.None;
        b.navigation = new Navigation { mode = Navigation.Mode.None };
        var label = Label(rt,"ActionLabel",text,30,0,0,w-24,h-8,Ink);
        label.enableAutoSizing = true; label.fontSizeMin = 24; label.fontSizeMax = 30; label.textWrappingMode = TextWrappingModes.NoWrap;
        rt.gameObject.AddComponent<SongSelectActionStyle>().Initialize(b,face,label,main);
        b.onClick.AddListener(click);
        var dwell = rt.gameObject.AddComponent<SaberDwellTarget>(); dwell.dwellSeconds = 1; dwell.progressColor = Cyan;
        return b;
    }
    static void Enable(Button b,bool enabled)
    {
        if (b.interactable == enabled) return;
        b.interactable = enabled; b.GetComponent<SongSelectActionStyle>().Refresh();
    }
    static void Caption(Button b,string text) => b.GetComponentInChildren<TextMeshProUGUI>(true).text = text;
    void Build()
    {
        if (Object.FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>() == null)
            new GameObject("EventSystem",typeof(UnityEngine.EventSystems.EventSystem),typeof(UnityEngine.InputSystem.UI.InputSystemUIInputModule));
        var header = Panel(transform,"Header",0,480,1920,120);
        back = Action(header,"Back","戻る",-815,0,190,84,OnBackClicked);
        Label(header,"Title","判定調整",38,-542,12,320,58,Ink,false);
        device = Label(header,"Profile","",24,-452,-28,500,38,Muted,false);
        var live = Panel(transform,"LiveFeedback",-594,326,650,160);
        Label(live,"Eyebrow","判定",20,0,53,600,34,Muted);
        feedback = Label(live,"Feedback","—",42,0,3,604,70,Cyan);
        feedback.enableAutoSizing = true; feedback.fontSizeMin = 30; feedback.fontSizeMax = 48;
        feedbackDetail = Label(live,"FeedbackDetail","",23,0,-48,600,36,Muted);
        var trend = Panel(transform,"RecentTiming",339,326,1160,160);
        trendTitle = Label(trend,"Title","タイミング",23,-208,53,688,36,Ink,false);
        trendCounts = Label(trend,"Counts","",20,362,53,380,36,Muted);
        timing = Rect(trend,"ErrorMeter",0,6,1080,54).gameObject.AddComponent<CalibrationTimingGraphic>();
        timing.isDistribution = true; timing.raycastTarget = false;
        Label(trend,"Early","早い  −250 ms",18,-387,-48,305,32,Blue,false);
        Label(trend,"Center","0",18,0,-48,60,32,Cyan);
        Label(trend,"Late","+250 ms  遅い",18,389,-48,305,32,Coral);
        instruction = Label(transform,"Instruction","",26,0,200,1780,48,Ink);
        readyCard = Panel(transform,"ReadyGuide",0,-57,1060,146).gameObject;
        readyDetail = Label(readyCard.transform,"Detail","",28,0,21,992,70,Ink);
        Label(readyCard.transform,"LeftHand","青：左手",24,-130,-42,240,40,Blue);
        Label(readyCard.transform,"RightHand","赤：右手",24,130,-42,240,40,Coral);
        measurementStatus = Label(transform,"MeasurementStatus","",26,0,-265,1740,42,Muted);
        // ボタンを下端の操作帯に収め、大きくしても試し切り領域に重ねない。
        var controls = Panel(transform,"Controls",0,-414,1840,236);
        Label(controls,"EarlierHint","早い → −",24,-389,98,264,34,Blue);
        Label(controls,"LaterHint","＋ ← 遅い",24,221,98,264,34,Coral);
        play = Action(controls,"PlayPause","試し切り",-720,26,350,80,TogglePractice,true);
        float[] xs = { -458,-320,152,290 };
        for (int i=0;i<deltas.Length;i++)
        { int d=deltas[i]; adjustments.Add(Action(controls,"Adjust"+d,d.ToString("+0;-0"),xs[i],26,126,80,()=>ctl?.ChangeOffset(d))); }
        value = Label(controls,"OffsetValue","",45,-83,34,322,62,Ink);
        savedValue = Label(controls,"SavedValue","",22,-83,-10,325,32,Muted);
        save = Action(controls,"Save","保存して戻る",684,26,420,80,()=>ctl?.SaveAndExit(),true);
        measure = Action(controls,"Measure","自動測定",-720,-62,350,72,StartMeasurement);
        speeds = new Button[NoteSpeedPreset.Count]; float[] sx = { -410,-140,130 };
        for (int i=0;i<speeds.Length;i++)
        { int s=i; speeds[i]=Action(controls,"Speed"+(i+1),NoteSpeedPreset.Caption(i,false),sx[i],-62,254,72,()=>ctl?.SetSpeedStep(s)); }
        restore = Action(controls,"Restore","保存値に戻す",413,-62,280,72,()=>ctl?.RestoreSaved());
        state = Label(controls,"SaveState","",24,733,-62,326,58,Muted);
        BuildResults(); BuildSettings(); BuildExitDialog();
        var clock = Panel(transform,"TimeTab",730,480,380,112);
        foreach (var graphic in clock.GetComponentsInChildren<Graphic>()) graphic.raycastTarget = false;
        Label(clock,"TimeLabel","残り時間",20,-102,25,140,32,Muted);
        Label(clock,"TimeoutHint","0秒で保存せず戻る",18,-75,-20,218,32,Muted);
        timer = Label(clock,"TimeRemaining","",70,95,0,146,100,Ink);
        timer.font = UISkinKit.LogoFontAsset(); timer.textWrappingMode = TextWrappingModes.NoWrap;
        countdown.Reset(GameSession.CalibrationSelectionSeconds ?? 100);
        GameSession.CalibrationSelectionSeconds = countdown.Remaining;
        lastTick = Time.realtimeSinceStartupAsDouble; RefreshCountdown();
    }
    void BuildResults()
    {
        resultCard = Panel(transform,"MeasurementSummary",0,-84,1200,482).gameObject;
        resultCard.GetComponent<SongSelectPanelGraphic>().color = new Color(.025f,.043f,.065f,1);
        resultTitle = Label(resultCard.transform,"Title","",32,0,192,1120,52,Ink);
        resultValue = Label(resultCard.transform,"Value","",39,0,124,1120,66,Cyan);
        resultDetail = Label(resultCard.transform,"Detail","",23,0,60,1120,52,Muted);
        resultMessage = Label(resultCard.transform,"Message","",25,0,-22,1100,106,Ink);
        resultPrimary = Action(resultCard.transform,"TryProposal","提案値で試す",-210,-125,650,76,AcceptResult,true);
        resultRetry = Action(resultCard.transform,"MeasureAgain","もう一度測る",367,-125,336,76,StartMeasurement);
        Action(resultCard.transform,"Close","手動調整に戻る",0,-207,1070,64,()=>{ctl.Stop();Refresh();});
        resultCard.SetActive(false);
    }
    void BuildSettings()
    {
        settings = new GameObject("AudioSettings",typeof(RectTransform)); settings.transform.SetParent(transform,false);
        SongSelectVisuals.Stretch(settings.GetComponent<RectTransform>());
        var dim = Rect(settings.transform,"Dim",0,0,1920,1080).gameObject.AddComponent<Image>(); dim.color = new Color(0,0,0,.82f);
        var p = Panel(settings.transform,"SettingsCard",0,0,1010,744);
        p.GetComponent<SongSelectPanelGraphic>().color = new Color(.025f,.043f,.065f,1);
        Label(p,"Title","音・接続設定",35,0,310,940,60,Ink);
        Label(p,"OutputLabel","調整値の保存先",24,0,234,940,44,Muted);
        profiles = new[] {Action(p,"Speaker","PCスピーカー",-229,170,438,80,()=>ctl?.SelectProfile(0)),
            Action(p,"Headphones","有線イヤホン",229,170,438,80,()=>ctl?.SelectProfile(1))};
        volume = Label(p,"ClickVolume","",25,-115,77,660,54,Ink);
        Action(p,"VolumeDown","−",282,77,96,80,()=>ctl?.ChangeVolume(-.1f));
        Action(p,"VolumeUp","＋",394,77,96,80,()=>ctl?.ChangeVolume(.1f));
        connections = Label(p,"Connections","",24,0,-4,928,62,Muted);
        Label(p,"Hint","音の出力先はWindowsで切り替えます。",23,0,-64,916,44,Muted);
        Label(p,"Shortcuts","Space 開始・停止   ← → ±1 ms   Shift ±10 ms\n1 / 2 / 3 速度   Esc 戻る   セーバーを1秒合わせて選択",21,0,-121,916,70,Muted);
        Action(p,"Restore","保存値に戻す",0,-198,898,80,()=>ctl?.RestoreSaved());
        Action(p,"Close","閉じる",0,-294,898,80,()=>{settings.SetActive(false);Refresh();},true);
        settings.SetActive(false);
    }
    void BuildExitDialog()
    {
        exitDialog = new GameObject("UnsavedChanges",typeof(RectTransform)); exitDialog.transform.SetParent(transform,false);
        SongSelectVisuals.Stretch(exitDialog.GetComponent<RectTransform>());
        var dim = Rect(exitDialog.transform,"Dim",0,0,1920,1080).gameObject.AddComponent<Image>(); dim.color = new Color(0,0,0,.82f);
        var p = Panel(exitDialog.transform,"Dialog",0,0,1040,340);
        p.GetComponent<SongSelectPanelGraphic>().color = new Color(.025f,.043f,.065f,1);
        Label(p,"Title","変更を保存せずに戻りますか？",32,0,56,960,68,Ink);
        Action(p,"Continue","調整を続ける",-240,-93,450,88,()=>{exitDialog.SetActive(false);Refresh();},true);
        Action(p,"Discard","保存せずに戻る",240,-93,450,88,()=>ctl?.DiscardAndExit());
        exitDialog.SetActive(false);
    }
    public void TogglePractice()
    {
        if (ctl==null||IsExitDialogOpen||IsSettingsOpen||IsResultOpen) return;
        if (ctl.IsRunning) ctl.Stop(); else ctl.BeginLivePractice(); Refresh();
    }
    public void StartMeasurement()
    {
        if (ctl==null||IsSettingsOpen||IsExitDialogOpen||!ctl.CanMeasure||RemainingSeconds<25) return;
        ctl.Begin(CalibrationRunMode.Measure); Refresh();
    }
    void AcceptResult()
    {
        if (ctl?.Result==null) return;
        if (!ctl.LastRunWasMeasurement) ctl.SaveAndExit();
        else if (ctl.Result.CanRecommend&&RemainingSeconds>=25) ctl.TryRecommendation();
        Refresh();
    }
    public void OpenSettings()
    { if (ctl==null) return; if (ctl.IsRunning) ctl.Stop(); settings.SetActive(true); Refresh(); }
    public void OnBackClicked()
    {
        if (ctl==null) {GamePlayManager.ExitCalibration();return;}
        if (IsSettingsOpen) {settings.SetActive(false);Refresh();return;}
        if (IsResultOpen) {ctl.Stop();Refresh();return;}
        if (ctl.IsRunning) ctl.Stop();
        if (ctl.Draft.IsDirty) {exitDialog.SetActive(true);Refresh();} else ctl.DiscardAndExit();
    }
    public void Tick()
    {
        double now=Time.realtimeSinceStartupAsDouble; TickCountdown(now-lastTick); lastTick=now;
        if (ScreenTransition.IsBusy||ctl==null) return;
        var keyboard=Keyboard.current;
        if (keyboard!=null)
        {
            if (keyboard.escapeKey.wasPressedThisFrame)
            {
                if (IsExitDialogOpen) {exitDialog.SetActive(false);Refresh();}
                else if (ctl.IsRunning) {ctl.Stop();Refresh();} else OnBackClicked();
            }
            else if (!IsSettingsOpen&&!IsExitDialogOpen&&!IsResultOpen)
            {
                if (keyboard.spaceKey.wasPressedThisFrame) TogglePractice();
                int step=keyboard.leftShiftKey.isPressed||keyboard.rightShiftKey.isPressed?10:1;
                if (keyboard.leftArrowKey.wasPressedThisFrame) ctl.ChangeOffset(-step);
                if (keyboard.rightArrowKey.wasPressedThisFrame) ctl.ChangeOffset(step);
                if (keyboard.digit1Key.wasPressedThisFrame||keyboard.numpad1Key.wasPressedThisFrame) ctl.SetSpeedStep(0);
                if (keyboard.digit2Key.wasPressedThisFrame||keyboard.numpad2Key.wasPressedThisFrame) ctl.SetSpeedStep(1);
                if (keyboard.digit3Key.wasPressedThisFrame||keyboard.numpad3Key.wasPressedThisFrame) ctl.SetSpeedStep(2);
            }
        }
        if (shownMode!=ctl.Mode||shownOffset!=ctl.Draft.OffsetMs||shownDirty!=ctl.Draft.IsDirty||shownSpeed!=ctl.SpeedStep) Refresh();
        RefreshFeedback(); RefreshAvailability(); if (IsSettingsOpen) connections.text=ConnectionText;
    }
    string ConnectionText => $"左：{(ctl.LeftReady?"接続中":"入力待ち")}    右：{(ctl.RightReady?"接続中":"入力待ち")}";
    public void TickCountdown(double deltaSeconds)
    {
        if (ctl==null||ScreenTransition.IsBusy) return;
        bool expired=countdown.Tick(deltaSeconds,true);
        GameSession.CalibrationSelectionSeconds=countdown.Remaining; RefreshCountdown();
        if (!expired) return;
        GameSession.CalibrationSelectionSeconds=null; ctl.DiscardAndExit();
    }
    void RefreshCountdown()
    { timer.text=Math.Ceiling(countdown.Remaining).ToString(); timer.color=countdown.Remaining<=10?Coral:Ink; }
    void RefreshFeedback()
    {
        if (IsResultOpen)
        {
            feedback.text=ctl.Result.Captured>0?"完了":"入力なし";
            feedback.color=ctl.Result.Captured>0?Cyan:Coral;
            feedbackDetail.text="";
        }
        else if (ctl.HasLastError&&ctl.IsRunning)
        {
            double error=ctl.LastErrorMs; feedback.text=CalibrationProtocol.LiveFeedback(error);
            feedback.color=error < -8?Blue:error > 8?Coral:Cyan;
            feedbackDetail.text=CalibrationDraft.FormatMs(error);
        }
        else if (ctl.LastInputWasMiss&&ctl.IsRunning)
        {feedback.text="見逃し";feedback.color=Muted;feedbackDetail.text="";}
        else
        {
            feedback.color=Cyan;
            feedback.text=ctl.IsRunning&&ctl.RunTime<CalibrationProtocol.FirstNoteSeconds?"準備中":"—";
            feedbackDetail.text="";
        }
        var h=ctl.History;var summary=IsResultOpen?ctl.Result:h.Summary;
        trendTitle.text=summary==null||summary.Captured==0?"タイミング":$"中央値 {CalibrationDraft.FormatMs(summary.MedianMs)}";
        trendCounts.text=summary==null||summary.Captured==0?"":$"{(IsResultOpen?"測定":"直近")} {summary.Captured} 回";
        timing.SetResult(summary,ctl.HasLastError,ctl.LastErrorMs);
        if (ctl.IsRunning&&!ctl.IsLive)
            instruction.text=ctl.RunTime<CalibrationProtocol.NoteTime(CalibrationProtocol.WarmupNotes)?"準備中":
                $"{(ctl.Mode==CalibrationRunMode.Measure?"測定":"試し切り")}  {ctl.ProgressCount} / 24";
    }
    void RefreshAvailability()
    {
        bool modal=IsSettingsOpen||IsExitDialogOpen||IsResultOpen;
        bool canAdjust=(!ctl.IsRunning||ctl.IsLive)&&!modal;
        for (int i=0;i<adjustments.Count;i++) Enable(adjustments[i],canAdjust&&(deltas[i]<0?ctl.Draft.OffsetMs>GameSession.JudgmentOffsetMinMs:ctl.Draft.OffsetMs<GameSession.JudgmentOffsetMaxMs));
        foreach (var b in speeds) Enable(b,canAdjust);
        Enable(restore,canAdjust&&(ctl.Draft.OffsetMs!=ctl.Draft.SavedOffsetMs||!Mathf.Approximately(ctl.Draft.ApproachTime,GameSession.NoteApproachTime)));
        Enable(save,canAdjust);Enable(play,!modal);Enable(back,!modal);
        bool canMeasure=ctl.CanMeasure&&RemainingSeconds>=25;
        Enable(measure,canAdjust&&canMeasure);Enable(resultRetry,canMeasure&&!IsSettingsOpen&&!IsExitDialogOpen);
        if (IsResultOpen)
        {
            Enable(resultPrimary,!IsSettingsOpen&&!IsExitDialogOpen&&(!ctl.LastRunWasMeasurement||(ctl.Result.CanRecommend&&RemainingSeconds>=25)));
            if (ctl.LastRunWasMeasurement&&ctl.Result.CanRecommend&&RemainingSeconds<25)
            {
                Caption(resultPrimary,"残り時間不足");
                resultMessage.text="試し切りには25秒以上必要です（提案値は未保存）。";
            }
        }
        if (ctl.IsRunning) measurementStatus.text="";
        else if (RemainingSeconds<25) measurementStatus.text="自動測定には残り25秒以上必要です";
        else measurementStatus.text=ctl.CanMeasure?"":"自動測定：両手の接続待ち";
    }
    public void Refresh()
    {
        if (ctl?.Draft==null) return;
        shownMode=ctl.Mode;shownOffset=ctl.Draft.OffsetMs;shownDirty=ctl.Draft.IsDirty;shownSpeed=ctl.SpeedStep;
        value.text=CalibrationDraft.FormatMs(ctl.Draft.OffsetMs);
        savedValue.text=ctl.Draft.OffsetMs!=ctl.Draft.SavedOffsetMs?$"保存値 {CalibrationDraft.FormatMs(ctl.Draft.SavedOffsetMs)}":"";
        state.text=ctl.Draft.IsDirty?"未保存":"";state.color=Coral;
        device.text=ctl.Draft.ProfileName;Caption(play,ctl.IsRunning?"停止":"試し切り");
        for (int i=0;i<speeds.Length;i++) Caption(speeds[i],NoteSpeedPreset.Caption(i,ctl.SpeedStep==i));
        instruction.text="";
        readyCard.SetActive(!ctl.IsRunning&&ctl.Mode!=CalibrationRunMode.Result);
        readyDetail.text=ctl.Notice.Contains("中断")||ctl.Notice.Contains("音声機器")?ctl.Notice:
            "音に合わせてカット";
        resultCard.SetActive(ctl.Mode==CalibrationRunMode.Result&&ctl.Result!=null);
        instruction.gameObject.SetActive(!IsResultOpen);
        measurementStatus.gameObject.SetActive(!IsResultOpen);
        if (IsResultOpen) RefreshResult();
        volume.text=$"音量   {ctl.ReferenceVolume*100:0}%";connections.text=ConnectionText;
        for (int i=0;i<profiles.Length;i++) Caption(profiles[i],(ctl.Draft.Profile==i?"●  ":"")+(i==0?"PCスピーカー":"有線イヤホン"));
        RefreshFeedback();RefreshAvailability();
    }
    void RefreshResult()
    {
        var r=ctl.Result;
        resultTitle.text=ctl.LastRunWasMeasurement?"測定結果":"試し切り結果";
        resultValue.text=ctl.LastRunWasMeasurement&&r.CanRecommend?$"現在 {CalibrationDraft.FormatMs(ctl.Draft.OffsetMs)}  →  提案 {CalibrationDraft.FormatMs(r.ProposedOffsetMs)}":
            r.Captured>0?$"中央値  {CalibrationDraft.FormatMs(r.MedianMs)}":"入力なし";
        resultDetail.text=r.Captured>0?$"有効 {r.Accepted} / 24    ばらつき {r.SpreadMs:0} ms":"";
        resultMessage.text=ctl.LastRunWasMeasurement?(r.CanRecommend?"未保存":r.Message):
            r.Accepted>=22?"合っていれば保存":"入力が少なめです。もう一度試してください。";
        Caption(resultPrimary,ctl.LastRunWasMeasurement?(r.CanRecommend?"この値で試す":"提案なし"):"保存して戻る");
    }
}
