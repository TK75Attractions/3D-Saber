using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// 試し切りと直近の判定を主役にする。環境設定は必要な時だけ開く。
public sealed class CalibrationOverlay : MonoBehaviour
{
    static readonly Color Ink=new Color(.91f,.97f,1), Muted=new Color(.64f,.75f,.81f),
        Cyan=new Color(.43f,.93f,.87f), Blue=new Color(.4f,.72f,1), Coral=new Color(1,.59f,.48f),
        PanelColor=new Color(.025f,.043f,.065f,.94f);
    CalibrationController ctl;
    TextMeshProUGUI feedback, feedbackDetail, value, state, instruction, volume, connections;
    Button play, save, settingsButton, back;
    GameObject settings, exitDialog;
    RectTransform feedbackCard;
    readonly List<Button> adjustments=new List<Button>();
    Button[] profiles;
    CalibrationRunMode shownMode=(CalibrationRunMode)(-1);
    int shownOffset=int.MinValue;
    bool shownDirty;
    public bool IsExitDialogOpen => exitDialog!=null&&exitDialog.activeSelf;
    public bool IsSettingsOpen => settings!=null&&settings.activeSelf;
    public string FeedbackText => feedback!=null?feedback.text:"";

    public static CalibrationOverlay Ensure()
    {
        var existing=Object.FindFirstObjectByType<CalibrationOverlay>();if(existing!=null)return existing;
        var go=new GameObject("CalibrationOverlay",typeof(Canvas),typeof(CanvasScaler),typeof(GraphicRaycaster));
        var canvas=go.GetComponent<Canvas>();canvas.renderMode=RenderMode.ScreenSpaceOverlay;canvas.sortingOrder=100;
        var scaler=go.GetComponent<CanvasScaler>();scaler.uiScaleMode=CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution=new Vector2(1920,1080);scaler.matchWidthOrHeight=.5f;
        var overlay=go.AddComponent<CalibrationOverlay>();overlay.Build();return overlay;
    }
    public void Bind(CalibrationController controller){ctl=controller;Refresh();}
    RectTransform Rect(Transform p,string name,float x,float y,float w,float h)=>
        SongSelectVisuals.Rect(p,name,new Vector2(x,y),new Vector2(w,h));
    Transform Panel(Transform p,string name,float x,float y,float w,float h)=>
        SongSelectVisuals.Panel(p,name,new Vector2(x,y),new Vector2(w,h),PanelColor,new Color(.22f,.39f,.45f),14).transform;
    TextMeshProUGUI Label(Transform p,string name,string text,float size,float x,float y,float w,float h,Color color,bool center=true)
    {
        var t=SongSelectVisuals.Label(p,name,text,size,new Vector2(x,y),new Vector2(w,h),color,
            center?TextAlignmentOptions.Center:TextAlignmentOptions.MidlineLeft,false);
        t.textWrappingMode=TextWrappingModes.Normal;t.overflowMode=TextOverflowModes.Ellipsis;return t;
    }
    Button Action(Transform p,string name,string text,float x,float y,float w,float h,UnityEngine.Events.UnityAction click,bool main=false)
    {
        var rt=Rect(p,name,x,y,w,h);var b=rt.gameObject.AddComponent<Button>();
        SongSelectVisuals.StyleAction(b,text,main);b.onClick.AddListener(click);
        b.GetComponentInChildren<TextMeshProUGUI>().fontSize=25;
        var dwell=rt.GetComponent<SaberDwellTarget>()??rt.gameObject.AddComponent<SaberDwellTarget>();dwell.dwellSeconds=1;dwell.progressColor=Cyan;
        return b;
    }
    static void Enable(Button b,bool enabled)
    {b.interactable=enabled;b.GetComponent<SongSelectActionStyle>().Refresh();}
    static void Caption(Button b,string text)=>b.GetComponentInChildren<TextMeshProUGUI>(true).text=text;
    void Build()
    {
        if(Object.FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>()==null)
            new GameObject("EventSystem",typeof(UnityEngine.EventSystems.EventSystem),typeof(UnityEngine.InputSystem.UI.InputSystemUIInputModule));
        var header=Panel(transform,"Header",0,480,1920,120);
        back=Action(header,"Back","戻る",-835,0,150,60,OnBackClicked);
        Label(header,"Title","判定調整",38,-510,0,440,72,Ink,false);
        settingsButton=Action(header,"Settings","音・設定",805,0,220,60,OpenSettings);
        instruction=Label(transform,"Instruction","音に合わせて、青・赤のノーツを切ってみよう",28,0,363,1400,50,Ink);
        feedbackCard=(RectTransform)Panel(transform,"LiveFeedback",0,253,580,146);
        feedback=Label(feedbackCard,"Feedback","切って確かめる",49,0,22,540,77,Cyan);
        feedbackDetail=Label(feedbackCard,"FeedbackDetail","切るたびに、ここにタイミングを表示",22,0,-40,540,42,Muted);
        var controls=Panel(transform,"Controls",0,-447,1840,170);
        play=Action(controls,"PlayPause","試し切りを始める",-716,16,322,70,TogglePractice,true);
        int[] deltas={-10,-1,1,10};float[] xs={-455,-344,125,236};
        for(int i=0;i<4;i++)
        {int d=deltas[i];adjustments.Add(Action(controls,"Adjust"+d,d.ToString("+0;-0"),xs[i],16,99,68,()=>ctl?.ChangeOffset(d)));}
        value=Label(controls,"OffsetValue","",46,-110,16,286,72,Ink);
        value.enableAutoSizing=true;value.fontSizeMin=34;value.fontSizeMax=46;
        Label(controls,"DirectionHint","← 早める                                      遅らせる →",20,-110,-49,805,34,Muted);
        save=Action(controls,"Save","保存して戻る",674,16,360,70,()=>ctl?.SaveAndExit(),true);
        state=Label(controls,"SaveState","",19,674,-49,360,34,Muted);
        BuildSettings();BuildExitDialog();
    }
    void BuildSettings()
    {
        settings=new GameObject("AudioSettings",typeof(RectTransform));settings.transform.SetParent(transform,false);
        SongSelectVisuals.Stretch(settings.GetComponent<RectTransform>());
        var dim=Rect(settings.transform,"Dim",0,0,1920,1080).gameObject.AddComponent<Image>();dim.color=new Color(0,0,0,.78f);
        var p=Panel(settings.transform,"SettingsCard",0,0,900,690);
        p.GetComponent<SongSelectPanelGraphic>().color=new Color(.025f,.043f,.065f,1);
        Label(p,"Title","音・設定",35,0,280,800,60,Ink);
        Label(p,"OutputLabel","調整値の保存先",23,0,201,760,42,Muted);
        profiles=new[]{Action(p,"Speaker","PCスピーカー",-205,139,385,64,()=>ctl?.SelectProfile(0)),
            Action(p,"Headphones","有線イヤホン",205,139,385,64,()=>ctl?.SelectProfile(1))};
        volume=Label(p,"ClickVolume","",24,-75,37,570,50,Ink);
        Action(p,"VolumeDown","−",233,37,68,56,()=>ctl?.ChangeVolume(-.1f));
        Action(p,"VolumeUp","＋",319,37,68,56,()=>ctl?.ChangeVolume(.1f));
        connections=Label(p,"Connections","",22,0,-42,800,50,Muted);
        Action(p,"Restore","保存した値に戻す",0,-129,790,56,()=>ctl?.RestoreSaved());
        Label(p,"Hint","出力先自体はWindowsで切り替えてください。\n試し切り中は基準音だけが鳴ります。",20,0,-200,790,66,Muted);
        Action(p,"Close","閉じる",0,-280,790,60,()=>{settings.SetActive(false);Refresh();},true);
        settings.SetActive(false);
    }
    void BuildExitDialog()
    {
        exitDialog=new GameObject("UnsavedChanges",typeof(RectTransform));exitDialog.transform.SetParent(transform,false);
        SongSelectVisuals.Stretch(exitDialog.GetComponent<RectTransform>());
        var dim=Rect(exitDialog.transform,"Dim",0,0,1920,1080).gameObject.AddComponent<Image>();dim.color=new Color(0,0,0,.82f);
        var p=Panel(exitDialog.transform,"Dialog",0,0,900,320);
        p.GetComponent<SongSelectPanelGraphic>().color=new Color(.025f,.043f,.065f,1);
        Label(p,"Title","変更を保存せずに戻りますか？",32,0,82,820,68,Ink);
        Label(p,"Body","以前の保存値は、そのまま残ります。",23,0,13,820,48,Muted);
        Action(p,"Continue","調整を続ける",-207,-91,382,66,()=>{exitDialog.SetActive(false);Refresh();},true);
        Action(p,"Discard","保存せずに戻る",207,-91,382,66,()=>ctl?.DiscardAndExit());
        exitDialog.SetActive(false);
    }
    public void TogglePractice()
    {
        if(ctl==null||IsExitDialogOpen||IsSettingsOpen)return;
        if(ctl.IsRunning)ctl.Stop();else ctl.BeginLivePractice();
        Refresh();
    }
    public void OpenSettings()
    {if(ctl==null)return;if(ctl.IsRunning)ctl.Stop();settings.SetActive(true);Refresh();}
    public void OnBackClicked()
    {
        if(ctl==null){GamePlayManager.ExitCalibration();return;}
        if(IsSettingsOpen){settings.SetActive(false);Refresh();return;}
        if(ctl.IsRunning){ctl.Stop();Refresh();}
        if(ctl.Draft.IsDirty){exitDialog.SetActive(true);Refresh();}else ctl.DiscardAndExit();
    }
    public void Tick()
    {
        if (ScreenTransition.IsBusy) return;
        if(ctl==null)return;
        var keyboard=Keyboard.current;
        if(keyboard!=null)
        {
            if(keyboard.escapeKey.wasPressedThisFrame)
            {if(IsExitDialogOpen){exitDialog.SetActive(false);Refresh();}else if(ctl.IsRunning){ctl.Stop();Refresh();}else OnBackClicked();}
            else if(!IsSettingsOpen&&!IsExitDialogOpen)
            {
                if(keyboard.spaceKey.wasPressedThisFrame)TogglePractice();
                int step=keyboard.leftShiftKey.isPressed||keyboard.rightShiftKey.isPressed?10:1;
                if(keyboard.leftArrowKey.wasPressedThisFrame)ctl.ChangeOffset(-step);
                if(keyboard.rightArrowKey.wasPressedThisFrame)ctl.ChangeOffset(step);
            }
        }
        if(shownMode!=ctl.Mode||shownOffset!=ctl.Draft.OffsetMs||shownDirty!=ctl.Draft.IsDirty)Refresh();
        RefreshFeedback();
        if(IsSettingsOpen)connections.text=$"セーバー  左：{(ctl.Stick1Ready?"接続中":"入力待ち")}    右：{(ctl.Stick2Ready?"接続中":"入力待ち")}";
    }
    void RefreshFeedback()
    {
        if(ctl.HasLastError&&ctl.IsRunning)
        {
            double error=ctl.LastErrorMs;
            feedback.text=CalibrationProtocol.LiveFeedback(error);
            feedback.color=error < -8?Blue:error > 8?Coral:Cyan;
            feedbackDetail.text=error < -8?$"{System.Math.Abs(error):0} ms 早め  ·  − 側で調整":
                error > 8?$"{System.Math.Abs(error):0} ms 遅め  ·  ＋ 側で調整":"中心付近です。その調子！";
        }
        else if(ctl.LastInputWasMiss&&ctl.IsLive)
        {feedback.text="見逃し";feedback.color=Muted;feedbackDetail.text="次のノーツで、もう一度";}
        else if(!ctl.IsRunning&&(ctl.Notice.Contains("中断")||ctl.Notice.Contains("音声機器")))
        {feedback.text="一時停止";feedback.color=Coral;feedbackDetail.text="画面・音の出力先を確認して再開";}
        else
        {
            feedback.color=Cyan;
            feedback.text=ctl.IsRunning?(ctl.RunTime<CalibrationProtocol.FirstNoteSeconds?"音を聴いて準備":"音に合わせてカット"):"切って確かめる";
            feedbackDetail.text=ctl.IsRunning?"青は左手  ·  赤は右手":"切るたびに、ここにタイミングを表示";
        }
        float age=Time.unscaledTime-ctl.LastFeedbackTime;
        feedbackCard.localScale=Vector3.one*(1+.025f*Mathf.Clamp01(1-age/.22f));
    }
    public void Refresh()
    {
        if(ctl?.Draft==null)return;
        shownMode=ctl.Mode;shownOffset=ctl.Draft.OffsetMs;shownDirty=ctl.Draft.IsDirty;
        value.text=CalibrationDraft.FormatMs(ctl.Draft.OffsetMs);
        state.text=ctl.Draft.IsDirty?"変更中 · まだ保存していません":"今の保存値を使用中";
        Caption(play,ctl.IsRunning?"一時停止":"試し切りを始める");
        // ダイアログを開いた同じフレームから背後を無効化し、UI描画の更新順にも依存しない。
        bool modal=IsSettingsOpen||IsExitDialogOpen;
        bool canAdjust=(!ctl.IsRunning||ctl.IsLive)&&!modal;
        foreach(var b in adjustments)Enable(b,canAdjust);
        Enable(save,canAdjust);Enable(play,!modal);Enable(settingsButton,!modal);Enable(back,!modal);
        instruction.text=ctl.IsRunning?"音に合わせて切る → タイミングを見る → 下で調整":"音に合わせて、青・赤のノーツを切ってみよう";
        volume.text=$"基準音の音量   {ctl.ReferenceVolume*100:0}%";
        for(int i=0;i<profiles.Length;i++)Caption(profiles[i],(ctl.Draft.Profile==i?"●  ":"")+(i==0?"PCスピーカー":"有線イヤホン"));
        RefreshFeedback();
    }
}
