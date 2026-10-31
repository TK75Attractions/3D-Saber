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
    static readonly Color Ink = new Color(.94f,.97f,1), Muted = new Color(.78f,.83f,.86f),
        Cyan = new Color(.55f,.95f,.72f), Amber = new Color(1,.79f,.35f),
        PanelColor = new Color(.025f,.043f,.065f,.97f);
    CalibrationController ctl;
    TextMeshProUGUI feedback, instruction, volume, connections, timer, value, measurementStatus;
    TextMeshProUGUI resultTitle, resultValue, resultDetail, resultMessage, timerNotice;
    readonly SongSelectCountdown countdown = new SongSelectCountdown();
    readonly List<Button> adjustments = new List<Button>();
    readonly int[] deltas = { -10, 10 };
    double lastTick;
    // 1回の Tick で残り時間を進める上限(秒)。音の立て直しを待つ間は GamePlayManager がこの Tick を呼ばないので、戻ったときに
    // 待った時間をまとめて引いて時間切れにしない(2026-10-30 全体の点検)。
    public const double MaxTickSeconds=.5;
    public static double CountdownStep(double elapsed)=>elapsed>MaxTickSeconds?MaxTickSeconds:elapsed;
    Button play, save, back, measure, restore, recommend, resultPrimary, resultRetry;
    Button earlier, later;
    GameObject settings, exitDialog, resultCard, controls, timingGroup;
    CalibrationTimingGraphic timing;
    Button[] profiles, speeds;
    CalibrationRunMode shownMode = (CalibrationRunMode)(-1);
    int shownOffset = int.MinValue, shownSpeed = -1, displayedSecond = -1;
    bool shownDirty;
    public int HighlightedDirection { get; private set; }
    public bool IsRecommendationVisible => recommend != null && recommend.gameObject.activeSelf;
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
        scaler.referenceResolution = new Vector2(1920,1080); scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
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
        label.enableAutoSizing = true; label.fontSizeMin = 26; label.fontSizeMax = 30; label.textWrappingMode = TextWrappingModes.NoWrap;
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
        var heading = Label(transform,"Title","",34,-680,469,430,76,Ink,false);
        heading.gameObject.AddComponent<SongSelectRubyText>().Set("<ruby=はんてい>判定</ruby><ruby=ちょうせい>調整</ruby>");
        instruction = Label(transform,"Instruction","音に合わせて切ろう",44,0,365,800,76,Ink);
        // カットする位置のすぐ下へ集約。数値や個々の早い/遅いは表示しない。
        timingGroup = Panel(transform,"RecentTiming",0,-242,1120,136).gameObject;
        feedback = Label(timingGroup.transform,"Feedback","",36,0,39,1060,56,Ink);
        timing = Rect(timingGroup.transform,"ErrorMeter",0,-20,1010,56).gameObject.AddComponent<CalibrationTimingGraphic>();
        timing.isDistribution = true; timing.raycastTarget = false;
        controls = Panel(transform,"Controls",0,-430,1840,220).gameObject;
        var c = controls.transform;
        back = Action(c,"Back","曲をえらぶ",-762,-52,230,96,OnBackClicked);
        earlier = Action(c,"Earlier","← ノーツを早く",-332,-52,430,96,()=>ctl?.ChangeOffset(-10));
        save = Action(c,"Save","決定",772,-52,210,96,()=>ctl?.SaveAndExit(),true);
        later = Action(c,"Later","ノーツを遅く →",302,-52,430,96,()=>ctl?.ChangeOffset(10));
        adjustments.Add(earlier); adjustments.Add(later);
        // 退出操作は調整より長くかざす。再実行には一度照準を外す必要がある。
        back.GetComponent<SaberDwellTarget>().dwellSeconds = 1.5f;
        save.GetComponent<SaberDwellTarget>().dwellSeconds = 1.5f;
        var speedGroup = Panel(c,"SpeedGroup",-454,68,830,100);
        speeds = new Button[NoteSpeedPreset.Count];
        for (int i=0;i<speeds.Length;i++)
        { int step=i; speeds[i]=Action(speedGroup,"Speed"+(i+1),NoteSpeedPreset.Names[i],-272+i*272,0,240,96,()=>ctl?.SetSpeedStep(step)); }
        recommend = Action(c,"Recommendation","おすすめに合わせる",522,60,660,64,()=>ctl?.ApplyLiveRecommendation());
        recommend.gameObject.SetActive(false);
        // 通常は隠し、中断時にだけ再開操作を出す。
        play = Action(c,"PlayPause","再開",522,60,660,64,TogglePractice);
        play.gameObject.SetActive(false);
        BuildResults(); BuildSettings(); BuildExitDialog();
        timer = SongSelectSkin.BuildCountdownDisplay(transform);
        timerNotice = Label(timer.transform.parent,"TimerConsequence","保存せず曲選択へ戻るまで",26,0,-79,430,44,Ink);
        countdown.Reset(GameSession.CalibrationSelectionSeconds ?? SongSelectCountdown.LimitSeconds);
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
        Action(resultCard.transform,"Close","練習へ",0,-207,1070,64,()=>{ctl.BeginLivePractice();Refresh();});
        resultCard.SetActive(false);
    }
    void BuildSettings()
    {
        settings = new GameObject("AudioSettings",typeof(RectTransform)); settings.transform.SetParent(transform,false);
        SongSelectVisuals.Stretch(settings.GetComponent<RectTransform>());
        var dim = Rect(settings.transform,"Dim",0,0,1920,1080).gameObject.AddComponent<Image>(); dim.color = new Color(0,0,0,.88f);
        var p = Panel(settings.transform,"SettingsCard",0,0,1420,960);
        Label(p,"Title","スタッフ用の調整",36,0,425,1320,58,Ink);
        profiles = new[] {Action(p,"Speaker","PCスピーカー",-324,340,610,76,()=>ctl?.SelectProfile(0)),
            Action(p,"Headphones","有線イヤホン",324,340,610,76,()=>ctl?.SelectProfile(1))};
        Label(p,"Hint","音の出力先はOSで切り替えます",26,0,273,1320,44,Muted);
        value = Label(p,"OffsetValue","",36,0,205,440,64,Ink);
        Action(p,"FineEarlier","−1 ms",-445,205,330,72,()=>ctl?.ChangeOffset(-1));
        Action(p,"FineLater","＋1 ms",445,205,330,72,()=>ctl?.ChangeOffset(1));
        volume = Label(p,"ClickVolume","",28,-160,114,910,58,Ink);
        Action(p,"VolumeDown","音量 −",363,114,210,72,()=>ctl?.ChangeVolume(-.1f));
        Action(p,"VolumeUp","音量 ＋",595,114,210,72,()=>ctl?.ChangeVolume(.1f));
        connections = Label(p,"Connections","",28,0,29,1320,50,Muted);
        measure = Action(p,"Measure","自動測定",-324,-65,610,76,StartMeasurement);
        restore = Action(p,"Restore","元に戻す",324,-65,610,76,()=>ctl?.RestoreSaved());
        restore.GetComponent<SaberDwellTarget>().dwellSeconds=2;
        measurementStatus = Label(p,"MeasurementStatus","",26,0,-135,1320,52,Muted);
        // キーの説明は実際の動きに合わせる(2026-10-23 点検: Space はこの画面では効かず、Esc は2回で下書きを捨ててタイトルへ戻るのに「Esc 戻る」と出ていた)。
        Label(p,"Shortcuts","F2 この画面⇔練習   練習中: Space 再開・停止 / ← → 10 ms   1・2・3 速さ\nこの画面: ← → 1 ms(Shift 10 ms)   Esc を2秒以内に2回: 保存せずタイトルへ",26,0,-225,1310,104,Muted);
        Action(p,"Close","練習へ",0,-375,1260,88,CloseSettings,true);
        settings.SetActive(false);
    }
    public void CloseSettings()
    { settings.SetActive(false); ctl.BeginLivePractice(); Refresh(); }
    void BuildExitDialog()
    {
        exitDialog = new GameObject("UnsavedChanges",typeof(RectTransform)); exitDialog.transform.SetParent(transform,false);
        SongSelectVisuals.Stretch(exitDialog.GetComponent<RectTransform>());
        var dim = Rect(exitDialog.transform,"Dim",0,0,1920,1080).gameObject.AddComponent<Image>(); dim.color = new Color(0,0,0,.82f);
        var p = Panel(exitDialog.transform,"Dialog",0,0,1040,340);
        p.GetComponent<SongSelectPanelGraphic>().color = new Color(.025f,.043f,.065f,1);
        Label(p,"Title","変更をやめて選曲にもどる？",32,0,56,960,68,Ink);
        Action(p,"Continue","続ける",-240,-93,450,88,ContinuePractice,true);
        Action(p,"Discard","変更をやめる",240,-93,450,88,()=>ctl?.DiscardAndExit());
        exitDialog.SetActive(false);
    }
    void ContinuePractice()
    { exitDialog.SetActive(false); ctl.BeginLivePractice(); Refresh(); }
    public void ClearTimingPreview() => timing?.ClearPreview();
    public void PreviewAdjustment(CalibrationResult history, int delta) => timing?.PreviewAdjustment(history,delta);
    public void TogglePractice()
    {
        if (ctl==null||IsExitDialogOpen||IsSettingsOpen||IsResultOpen) return;
        if (ctl.IsRunning) ctl.Stop(); else ctl.BeginLivePractice(); Refresh();
    }
    public void StartMeasurement()
    {
        if (ctl==null||IsExitDialogOpen||!ctl.CanMeasure||RemainingSeconds<25) return;
        settings.SetActive(false); ctl.Begin(CalibrationRunMode.Measure); Refresh();
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
        if (IsSettingsOpen) {CloseSettings();return;}
        if (IsResultOpen) {ctl.BeginLivePractice();Refresh();return;}
        if (ctl.IsRunning) ctl.Stop();
        if (ctl.Draft.IsDirty) {exitDialog.SetActive(true);Refresh();} else ctl.DiscardAndExit();
    }
    public void Tick()
    {
        double now=Time.realtimeSinceStartupAsDouble; TickCountdown(CountdownStep(now-lastTick)); lastTick=now;
        if (ScreenTransition.IsBusy||ctl==null) return;
        var keyboard=Keyboard.current;
        if (keyboard!=null)
        {
            // Esc は GamePlayManager が受ける(2秒以内に2回でタイトルへ。仕様面の点検 2)。押したフレームはほかのキーも今までどおり見ない。
            if (InputManager.BackKeyPressedThisFrame(keyboard)) { }
            else if (keyboard.f2Key.wasPressedThisFrame&&!IsExitDialogOpen&&!IsResultOpen)
            { if (IsSettingsOpen) CloseSettings(); else OpenSettings(); }
            else if (!IsExitDialogOpen&&!IsResultOpen)
            {
                if (!IsSettingsOpen&&keyboard.spaceKey.wasPressedThisFrame) TogglePractice();
                int step=IsSettingsOpen&&!(keyboard.leftShiftKey.isPressed||keyboard.rightShiftKey.isPressed)?1:10;
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
        // 選曲の残り時間と同じ決まり: ウィンドウが裏にある間は進めない。ただし iPhone の座標が届いていれば、
        // 照準も練習も裏で動くので進める(2026-10-23 点検)。
        bool expired=countdown.Tick(deltaSeconds,SongSelectSkin.VisitorInputLive());
        GameSession.CalibrationSelectionSeconds=countdown.Remaining; RefreshCountdown();
        if (!expired) return;
        // 時間を足さずに(0秒で)選曲へ戻す(2026-10-31 ユーザー「選曲の45秒の時計が、止まったり増えたりしないようにする」)。
        // 前は10秒を足して戻していたので、判定調整との行き来で時間切れがいつまでも来なかった。
        // 戻った選曲はすぐ時間切れの「この曲で はじめる？」(8秒で始める)を開く(判定調整に入ったのは操作なので)。
        GameSession.CalibrationSelectionSeconds=0; ctl.DiscardAndExit();
    }
    void RefreshCountdown()
    {
        int previous = displayedSecond;
        SongSelectSkin.RefreshCountdownDisplay(timer, countdown.Remaining, ref displayedSecond);
        if (previous != displayedSecond && timerNotice != null)
        {
            timerNotice.text = displayedSecond <= 20 ? "あと " + displayedSecond + " 秒で保存せず曲選択へ" : "保存せず曲選択へ戻るまで";
            timerNotice.color = displayedSecond <= 20 ? Amber : Ink;
        }
    }
    void RefreshFeedback()
    {
        feedback.text=ctl.IsLive?ctl.History.Hint:"";
        feedback.color=ctl.History.SuggestedDirection==0?Cyan:Amber;
        timing.SetResult(ctl.History.Summary,ctl.HasLastError,ctl.LastErrorMs);
        if (ctl.IsRunning&&!ctl.IsLive)
            instruction.text=$"{(ctl.Mode==CalibrationRunMode.Measure?"測定":"確認")}  {ctl.ProgressCount} / 24";
        HighlightedDirection=ctl.IsLive?ctl.History.SuggestedDirection:0;
        Highlight(earlier,HighlightedDirection<0); Highlight(later,HighlightedDirection>0);
    }
    static void Highlight(Button button,bool highlighted)
    {
        button.GetComponent<SongSelectActionStyle>().Refresh();
        if (!highlighted||!button.interactable) return;
        float pulse=.55f+.12f*Mathf.Sin(Time.unscaledTime*3);
        button.GetComponentInChildren<SongSelectPanelGraphic>().SetColors(new Color(.22f,.17f,.055f),Color.Lerp(Amber,Color.white,pulse));
        button.GetComponentInChildren<TextMeshProUGUI>().color=Amber;
    }
    void RefreshAvailability()
    {
        bool modal=IsSettingsOpen||IsExitDialogOpen||IsResultOpen;
        bool canAdjust=(!ctl.IsRunning||ctl.IsLive)&&!modal;
        for (int i=0;i<adjustments.Count;i++) Enable(adjustments[i],canAdjust&&(deltas[i]<0?ctl.Draft.OffsetMs>GameSession.JudgmentOffsetMinMs:ctl.Draft.OffsetMs<GameSession.JudgmentOffsetMaxMs));
        for (int i=0;i<speeds.Length;i++)
        {
            Enable(speeds[i],canAdjust);
            speeds[i].GetComponent<SongSelectActionStyle>().Refresh();
            if (canAdjust && ctl.SpeedStep==i)
                speeds[i].GetComponentInChildren<SongSelectPanelGraphic>().SetColors(new Color(.08f,.24f,.18f),Cyan);
        }
        Enable(save,canAdjust); Enable(back,!modal); Enable(play,!modal);
        play.gameObject.SetActive(!modal&&!ctl.IsLive);
        Caption(play,ctl.IsRunning?"測定を止める":"再開");
        recommend.gameObject.SetActive(!modal&&ctl.CanApplyLiveRecommendation);
        bool canMeasure=ctl.CanMeasure&&RemainingSeconds>=25;
        Enable(measure,canMeasure);
        Enable(restore,ctl.Draft.OffsetMs!=ctl.Draft.SavedOffsetMs||!Mathf.Approximately(ctl.Draft.ApproachTime,GameSession.NoteApproachTime));
        Enable(resultRetry,canMeasure&&!IsSettingsOpen&&!IsExitDialogOpen);
        if (IsResultOpen)
            Enable(resultPrimary,!IsSettingsOpen&&!IsExitDialogOpen&&(!ctl.LastRunWasMeasurement||(ctl.Result.CanRecommend&&RemainingSeconds>=25)));
        measurementStatus.text=RemainingSeconds<25?"自動測定には残り25秒以上必要です":ctl.CanMeasure?"":"自動測定：両手の接続待ち";
    }
    public void Refresh()
    {
        if (ctl?.Draft==null) return;
        shownMode=ctl.Mode; shownOffset=ctl.Draft.OffsetMs; shownDirty=ctl.Draft.IsDirty; shownSpeed=ctl.SpeedStep;
        value.text=CalibrationDraft.FormatMs(ctl.Draft.OffsetMs);
        for (int i=0;i<speeds.Length;i++) Caption(speeds[i],(ctl.SpeedStep==i?"● ":"")+NoteSpeedPreset.Names[i]);
        resultCard.SetActive(ctl.Mode==CalibrationRunMode.Result&&ctl.Result!=null);
        bool modal=IsSettingsOpen||IsExitDialogOpen||IsResultOpen;
        instruction.gameObject.SetActive(!modal); timingGroup.SetActive(!modal); controls.SetActive(!modal);
        instruction.text=ctl.IsRunning?"音に合わせて切ろう":"再開して続けよう";
        if (IsResultOpen) RefreshResult();
        volume.text=$"基準音の音量   {ctl.ReferenceVolume*100:0}%"; connections.text=ConnectionText;
        for (int i=0;i<profiles.Length;i++) Caption(profiles[i],(ctl.Draft.Profile==i?"●  ":"")+(i==0?"PCスピーカー":"有線イヤホン"));
        RefreshAvailability(); RefreshFeedback();
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
        Caption(resultPrimary,ctl.LastRunWasMeasurement?(r.CanRecommend?"この値で試す":"提案なし"):"決定");
    }
}
