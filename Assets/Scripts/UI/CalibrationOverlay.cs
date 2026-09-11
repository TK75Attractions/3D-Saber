using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// 調整中だけの UI。3D のカメラ・ノーツ・通常プレイ HUD の資産には変更を加えない。
public sealed class CalibrationOverlay : MonoBehaviour
{
    static readonly Color Ink = new Color(.91f,.97f,1f), Muted = new Color(.65f,.77f,.82f),
        Cyan = new Color(.43f,.93f,.87f), Blue = new Color(.4f,.72f,1f), Coral = new Color(1f,.59f,.48f),
        PanelColor = new Color(.035f,.068f,.09f,.97f);
    CalibrationController ctl;
    TextMeshProUGUI value, saved, meaning, output, sticks, notice, stageHeading, stageSub,
        count, liveHit, summary, summaryDetail, proposal, volume, profileHint;
    GameObject welcome, resultPanel, exitDialog;
    CalibrationTimingGraphic timeline, histogram;
    readonly List<Button> lockedWhileRunning = new List<Button>();
    Button measure, stop, tryProposal;
    Button[] profiles;
    CalibrationRunMode shownMode = (CalibrationRunMode)(-1);
    string shownNotice;
    CalibrationResult shownResult;
    public bool IsExitDialogOpen => exitDialog != null && exitDialog.activeSelf;

    public static CalibrationOverlay Ensure()
    {
        var existing=Object.FindFirstObjectByType<CalibrationOverlay>(); if(existing!=null) return existing;
        var go=new GameObject("CalibrationOverlay",typeof(Canvas),typeof(CanvasScaler),typeof(GraphicRaycaster));
        var canvas=go.GetComponent<Canvas>(); canvas.renderMode=RenderMode.ScreenSpaceOverlay; canvas.sortingOrder=100;
        var scaler=go.GetComponent<CanvasScaler>(); scaler.uiScaleMode=CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution=new Vector2(1920,1080); scaler.matchWidthOrHeight=.5f;
        var overlay=go.AddComponent<CalibrationOverlay>(); overlay.Build(); return overlay;
    }
    public void Bind(CalibrationController controller) { ctl=controller; Refresh(); }
    RectTransform Rect(Transform p,string name,float x,float y,float w,float h) =>
        SongSelectVisuals.Rect(p,name,new Vector2(x,y),new Vector2(w,h));
    Transform Panel(Transform p,string name,float x,float y,float w,float h) =>
        SongSelectVisuals.Panel(p,name,new Vector2(x,y),new Vector2(w,h),PanelColor,new Color(.27f,.44f,.49f),14).transform;
    TextMeshProUGUI Label(Transform p,string name,string text,float size,float x,float y,float w,float h,
        Color color,bool center=false,bool strong=false)
    {
        var t=SongSelectVisuals.Label(p,name,text,size,new Vector2(x,y),new Vector2(w,h),color,
            center?TextAlignmentOptions.Center:TextAlignmentOptions.MidlineLeft,strong);
        t.textWrappingMode=TextWrappingModes.Normal; t.overflowMode=TextOverflowModes.Overflow; return t;
    }
    Button Action(Transform p,string name,string text,float x,float y,float w,float h,
        UnityEngine.Events.UnityAction click,bool main=false,bool lockDuringRun=true)
    {
        var rt=Rect(p,name,x,y,w,h); var b=rt.gameObject.AddComponent<Button>();
        SongSelectVisuals.StyleAction(b,text,main); b.onClick.AddListener(click);
        b.GetComponentInChildren<TextMeshProUGUI>().fontSize=23;
        if(lockDuringRun) lockedWhileRunning.Add(b); return b;
    }
    void Build()
    {
        if(Object.FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>()==null)
            new GameObject("EventSystem",typeof(UnityEngine.EventSystems.EventSystem),typeof(UnityEngine.InputSystem.UI.InputSystemUIInputModule));
        var header=Panel(transform,"Header",0,470,1920,140);
        Label(header,"Title","判定タイミング調整",42,-515,10,780,70,Ink,false,true);
        Label(header,"Eyebrow","3D-SABER  /  SYNC LAB",18,-515,-37,780,25,Cyan);
        output=Label(header,"Output","",23,615,10,560,55,Ink,true);
        Label(header,"Setup","実機セーバー2本  /  プロジェクター",19,615,-34,560,30,Muted,true);
        string[] steps={"01  環境を確認","02  見て・切って確かめる","03  結果を確認して保存"};
        for(int i=0;i<3;i++)
        {
            var step=Panel(transform,"Step"+i,(i-1)*600,355,574,66);
            Label(step,"Label",steps[i],24,0,0,550,55,i==1?Cyan:Ink,true,true);
        }
        BuildEnvironment(); BuildAdjustment(); BuildCenter(); BuildReadout(); BuildFooter(); BuildExitDialog();
    }
    void BuildEnvironment()
    {
        var p=Panel(transform,"Environment",-720,0,400,580);
        Label(p,"Section","環境プロフィール",25,0,246,348,42,Cyan,false,true);
        profiles=new[] {
            Action(p,"Speaker","PCスピーカー",0,176,348,54,()=>ctl?.SelectProfile(0)),
            Action(p,"Headphones","有線イヤホン",0,112,348,54,()=>ctl?.SelectProfile(1))
        };
        profileHint=Label(p,"ProfileHint","",18,0,62,348,34,Muted);
        sticks=Label(p,"Connections","",22,0,12,348,58,Ink);
        Label(p,"TipTitle","始める前に",23,0,-60,348,38,Cyan,false,true);
        Label(p,"Tips","いつもと同じ立ち位置・持ち方で。\n可能なら投影機を低遅延モードに。\n出力先を変えたら再確認。",20,0,-132,348,96,Ink);
        volume=Label(p,"Volume","",20,-40,-221,250,40,Muted);
        Action(p,"VolumeDown","−",92,-221,48,44,()=>ctl?.ChangeVolume(-.1f));
        Action(p,"VolumeUp","＋",149,-221,48,44,()=>ctl?.ChangeVolume(.1f));
        Label(p,"NoSfx","確認中はカット効果音なし",17,0,-260,348,26,Muted);
    }
    void BuildAdjustment()
    {
        var p=Panel(transform,"Adjustment",720,0,400,580);
        Label(p,"Section","ノーツと判定のタイミング",23,0,246,348,42,Cyan,false,true);
        value=Label(p,"OffsetValue","",70,0,167,350,92,Ink,true,true);
        value.enableAutoSizing=true; value.fontSizeMin=46; value.fontSizeMax=70;
        saved=Label(p,"SavedValue","",19,0,101,350,35,Muted,true);
        meaning=Label(p,"Meaning","",23,0,34,348,90,Ink,true);
        int[] deltas={-10,-1,1,10};
        for(int i=0;i<4;i++)
        { int d=deltas[i]; Action(p,"Adjust"+d,d.ToString("+0;-0"),-132+i*88,-58,80,58,()=>ctl?.ChangeOffset(d)); }
        Label(p,"Earlier","← 早める",20,-87,-111,174,32,Blue,true);
        Label(p,"Later","遅らせる →",20,87,-111,174,32,Coral,true);
        Action(p,"Restore","この出力先の保存値に戻す",0,-170,348,52,()=>ctl?.RestoreSaved());
        Label(p,"Units","1 ms = 0.001 秒\n音量・ノーツ速度・判定幅は変わりません",18,0,-239,350,58,Muted,true);
    }
    void BuildCenter()
    {
        stageHeading=Label(transform,"StageHeading","",29,0,266,990,50,Cyan,true,true);
        stageSub=Label(transform,"StageSub","",22,0,215,990,48,Ink,true);
        count=Label(transform,"Progress","",30,0,-179,960,42,Ink,true,true);
        liveHit=Label(transform,"LastCut","",27,0,-232,960,46,Ink,true,true);
        welcome=Panel(transform,"Welcome",0,22,970,294).gameObject;
        Label(welcome.transform,"Title","数字より、まず体感を。",37,0,99,880,60,Ink,true,true);
        Label(welcome.transform,"Description","今の値はそのまま引き継ぎました。\nずれていなければ、変更しなくて大丈夫です。",25,0,23,866,85,Ink,true);
        Label(welcome.transform,"Directions","早く切ってしまう → 早める（−）\n遅く切ってしまう → 遅らせる（＋）",23,0,-76,860,74,Cyan,true);
        resultPanel=Panel(transform,"MeasurementResult",0,17,970,318).gameObject;
        summary=Label(resultPanel.transform,"Headline","",28,0,115,890,50,Cyan,true,true);
        summaryDetail=Label(resultPanel.transform,"Details","",21,0,55,890,76,Ink,true);
        proposal=Label(resultPanel.transform,"Suggestion","",22,0,-31,875,100,Ink,true);
        tryProposal=Action(resultPanel.transform,"TryRecommendation","提案値で試し切り（まだ保存しない）",0,-118,710,56,()=>ctl?.TryRecommendation(),true);
        resultPanel.SetActive(false);
    }
    void BuildReadout()
    {
        var p=Panel(transform,"TimingReadout",0,-361,1840,116);
        Label(p,"ReadoutLabel","音と到達の関係",20,-715,24,355,36,Cyan);
        Label(p,"Legend","白 = 基準音   青緑 = ノーツ到達",17,-715,-14,355,32,Ink);
        timeline=Rect(p,"SyncTimeline",-50,0,830,70).gameObject.AddComponent<CalibrationTimingGraphic>(); timeline.raycastTarget=false;
        histogram=Rect(p,"ErrorDistribution",620,0,490,68).gameObject.AddComponent<CalibrationTimingGraphic>();
        histogram.isDistribution=true; histogram.raycastTarget=false;
        Label(p,"DistributionTitle","カット誤差 / 色付き背景 = PERFECTの範囲",16,620,43,490,24,Muted,true);
        Label(p,"DistributionLegend","−250 ms   早い  /  0  /  遅い   +250 ms",16,620,-38,490,26,Ink,true);
    }
    void BuildFooter()
    {
        notice=Label(transform,"Notice","",20,0,-448,1800,43,Ink,true);
        Action(transform,"Observe","音と表示を見る",-715,-505,370,56,()=>ctl?.Begin(CalibrationRunMode.Observe));
        measure=Action(transform,"Measure","セーバーで測定",-326,-505,370,56,()=>ctl?.Begin(CalibrationRunMode.Measure),true);
        Action(transform,"Practice","今の値で試し切り",63,-505,370,56,()=>ctl?.Begin(CalibrationRunMode.Practice));
        Action(transform,"Save","保存して戻る",452,-505,370,56,()=>ctl?.SaveAndExit(),true);
        Action(transform,"Back","戻る",779,-505,245,56,OnBackClicked,false,false);
        stop=Action(transform,"Stop","停止する",0,-112,260,54,()=>ctl?.Stop(),false,false); stop.gameObject.SetActive(false);
    }
    void BuildExitDialog()
    {
        exitDialog=new GameObject("UnsavedChanges",typeof(RectTransform)); exitDialog.transform.SetParent(transform,false);
        SongSelectVisuals.Stretch(exitDialog.GetComponent<RectTransform>());
        var dim=Rect(exitDialog.transform,"Dim",0,0,1920,1080).gameObject.AddComponent<Image>(); dim.color=new Color(0,0,0,.82f);
        var p=Panel(exitDialog.transform,"Dialog",0,0,900,350);
        Label(p,"Title","変更を保存せずに戻りますか？",32,0,108,820,64,Ink,true,true);
        Label(p,"Body","仮の設定は破棄され、以前の保存値が残ります。",23,0,27,820,56,Muted,true);
        Action(p,"Continue","調整を続ける",-207,-98,382,64,()=>exitDialog.SetActive(false),true,false);
        Action(p,"Discard","保存せずに戻る",207,-98,382,64,()=>ctl?.DiscardAndExit(),false,false);
        exitDialog.SetActive(false);
    }
    public void OnBackClicked()
    {
        if(ctl==null) { GamePlayManager.ExitCalibration(); return; }
        if(ctl.IsRunning) { ctl.Stop(); return; }
        if(ctl.Draft.IsDirty) exitDialog.SetActive(true); else ctl.DiscardAndExit();
    }
    public void Tick()
    {
        if(ctl==null) return;
        if(Keyboard.current!=null && Keyboard.current.escapeKey.wasPressedThisFrame)
        { if(IsExitDialogOpen) exitDialog.SetActive(false); else OnBackClicked(); }
        if(shownMode!=ctl.Mode || shownNotice!=ctl.Notice || shownResult!=ctl.Result) Refresh();
        measure.interactable=!ctl.IsRunning&&ctl.CanMeasure; measure.GetComponent<SongSelectActionStyle>().Refresh();
        sticks.text=$"棒1  {(ctl.Stick1Ready?"接続中":"入力待ち")}     棒2  {(ctl.Stick2Ready?"接続中":"入力待ち")}";
        if(ctl.IsRunning)
        {
            if(ctl.RunTime<CalibrationProtocol.FirstNoteSeconds) count.text=$"{Mathf.Clamp((int)System.Math.Ceiling((CalibrationProtocol.FirstNoteSeconds-ctl.RunTime)/CalibrationProtocol.BeatSeconds),1,4)}  /  音を聴いて準備";
            else if(ctl.ProgressCount==0) count.text="ウォームアップ  /  最初の4ノーツは集計しません";
            else count.text=ctl.Mode==CalibrationRunMode.Observe?"切らずに、音とノーツを見比べましょう":
                $"{ctl.ProgressCount:00} / 24  ノーツ     カット記録 {ctl.CollectedCount:00}";
            liveHit.text=ctl.Mode==CalibrationRunMode.Observe?"青は左手  /  赤は右手  /  カットは不要です":ctl.LastCut;
        }
        timeline.SetTiming(ctl.RunTime,ctl.Draft.OffsetMs,ctl.IsRunning);
        histogram.SetResult(ctl.Result,ctl.HasLastError,ctl.LastErrorMs);
    }
    public void Refresh()
    {
        if(ctl==null || ctl.Draft==null) return;
        shownMode=ctl.Mode; shownNotice=ctl.Notice; shownResult=ctl.Result;
        value.text=CalibrationDraft.FormatMs(ctl.Draft.OffsetMs);
        saved.text=$"保存値  {CalibrationDraft.FormatMs(ctl.Draft.SavedOffsetMs)}"+(ctl.Draft.OffsetMs!=ctl.Draft.SavedOffsetMs?"  /  変更中":"");
        meaning.text=CalibrationDraft.Explain(ctl.Draft.OffsetMs); output.text=ctl.Draft.ProfileName+(ctl.Draft.IsDirty?"  /  未保存":"  /  保存済み");
        profileHint.text=ctl.Draft.HasSavedProfile?"出力先ごとに値を保存できます":"初回：現在の値から開始（未確認）";
        volume.text=$"基準音の音量   {ctl.ReferenceVolume*100:0}%";
        for(int i=0;i<profiles.Length;i++) profiles[i].GetComponentInChildren<TextMeshProUGUI>().text=
            (ctl.Draft.Profile==i?"●  ":"")+(i==0?"PCスピーカー":"有線イヤホン");
        foreach(var b in lockedWhileRunning) { b.interactable=!ctl.IsRunning; b.GetComponent<SongSelectActionStyle>().Refresh(); }
        measure.interactable=!ctl.IsRunning&&ctl.CanMeasure; measure.GetComponent<SongSelectActionStyle>().Refresh();
        welcome.SetActive(!ctl.IsRunning&&ctl.Result==null); resultPanel.SetActive(ctl.Result!=null); stop.gameObject.SetActive(ctl.IsRunning);
        stageHeading.text=ctl.Mode==CalibrationRunMode.Measure?"セーバー測定 / 100 BPM":ctl.Mode==CalibrationRunMode.Observe?"音と表示の確認 / 100 BPM":
            ctl.Mode==CalibrationRunMode.Practice?"試し切り / 本番と同じ判定":"いつもの環境で、無理なく合わせる";
        stageSub.text=ctl.IsRunning?"基準音の「カッ」に合わせて、ノーツを切り終えるタイミングを確認":"画面の中央は、そのまま3Dの試し切りエリアになります";
        if(!ctl.IsRunning) { count.text=""; liveHit.text="判定幅・ノーツ速度・本番のスコアには影響しません"; }
        notice.text=ctl.Notice;
        if(ctl.Result!=null)
        {
            var r=ctl.Result;
            summary.text=r.Captured==0?"入力を確認できませんでした":$"切るタイミングの中心  {CalibrationDraft.FormatMs(r.MedianMs)}";
            string spread=r.Accepted>0?$"{r.SpreadMs:0} ms":"未測定";
            string left=r.LeftCount>0?CalibrationDraft.FormatMs(r.LeftMs):"未測定";
            string right=r.RightCount>0?CalibrationDraft.FormatMs(r.RightMs):"未測定";
            summaryDetail.text=$"有効 {r.Accepted} / 24   未入力 {r.Missing}   外れ値 {r.Excluded}   ばらつき {spread}\n"+
                $"左 {r.LeftCount}回 / {left}     右 {r.RightCount}回 / {right}\n"+
                $"早い {r.Early}    中央（±8 ms） {r.Center}    遅い {r.Late}";
            proposal.text=r.Message; tryProposal.gameObject.SetActive(r.CanRecommend);
        }
    }
}
