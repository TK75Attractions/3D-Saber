using System;
using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// 実Gameシーンの背景比較。ノーツだけ固定した8秒デモを各背景ごとに撮影し、シーン資産は保存しない。
[InitializeOnLoad]
public static class ScenicWorldsPreview
{
    private const string Key="ScenicWorldsPreview.Running";
    private const int Width=1280,Height=720,Fps=24,Frames=288;
    private static int phase,frame,settle,themeIndex,lastTheme;
    private static double started;
    private static string output,folder;
    private static bool stills;
    private static bool dynamicsDemo;
    private static readonly StagePerformanceTimeline demo = new StagePerformanceTimeline { sections = new[] {
        new StagePerformanceTimeline.Section { startSeconds=3,endSeconds=11.5,fadeInSeconds=2,fadeOutSeconds=2.5f }
    }};
    private static FloorRenderer stage;
    private static FoundryStageMotion foundry;
    private static ScenicStageWorld world;
    private static GamePlayManager manager;
    private static TextMeshProUGUI caption;
    private static RenderTexture target;
    private static Texture2D pixels,newOverview,oldOverview;

    static ScenicWorldsPreview()
    {
        EditorApplication.update+=Tick;
        EditorApplication.playModeStateChanged+=state=>
        {
            if(!SessionState.GetBool(Key,false)||state!=PlayModeStateChange.EnteredPlayMode) return;
            started=EditorApplication.timeSinceStartup; phase=frame=settle=0;
            output=SessionState.GetString(Key+".Output",""); stills=SessionState.GetBool(Key+".Stills",false);
            dynamicsDemo=SessionState.GetBool(Key+".Dynamics",false);
            themeIndex=SessionState.GetInt(Key+".First",0); lastTheme=SessionState.GetInt(Key+".Last",9);
        };
    }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void SelectSong()
    {
        if(!SessionState.GetBool(Key,false)) return;
        GameSession.SelectedSongId="ElDorado"; GameSession.SelectedDifficulty="normal"; GameSession.IsCalibrationMode=false;
    }
    public static void Render()
    {
        if(!Application.isBatchMode) throw new InvalidOperationException("専用バッチから実行してください。");
        var args=Environment.GetCommandLineArgs(); int at=Array.IndexOf(args,"-scenicPreviewOutput");
        if(at<0||at+1>=args.Length) throw new ArgumentException("出力先が必要です。");
        output=Path.GetFullPath(args[at+1]); Directory.CreateDirectory(output);
        SessionState.SetString(Key+".Output",output); SessionState.SetBool(Key,true);
        SessionState.SetBool(Key+".Stills",Array.IndexOf(args,"-scenicPreviewStills")>=0);
        SessionState.SetBool(Key+".Dynamics",Array.IndexOf(args,"-scenicDynamicsDemo")>=0);
        int only=Array.IndexOf(args,"-scenicPreviewTheme");
        int first=0,last=StageThemeCatalog.Count-1;
        if(only>=0 && only+1<args.Length) { first=int.Parse(args[only+1]); last=first; }
        if(first<0||last>=StageThemeCatalog.Count) throw new ArgumentOutOfRangeException("背景番号");
        SessionState.SetInt(Key+".First",first); SessionState.SetInt(Key+".Last",last);
        File.WriteAllText(Path.Combine(output,"runtime-check.txt"),"Actual Game scene; same camera and held notes. No scene/chart saving.\n");
        EditorSceneManager.OpenScene("Assets/Scenes/Game.unity",OpenSceneMode.Single);
        SelectSong(); EditorApplication.isPlaying=true;
    }
    private static void Tick()
    {
        if(!SessionState.GetBool(Key,false)||!EditorApplication.isPlaying||started<=0) return;
        try
        {
            if(EditorApplication.timeSinceStartup-started>1200) throw new TimeoutException("全背景プレビューのタイムアウト");
            if(phase==0)
            {
                var spawner=UnityEngine.Object.FindFirstObjectByType<NoteSpawner>();
                manager=UnityEngine.Object.FindFirstObjectByType<GamePlayManager>();
                if(spawner==null||spawner.TotalNoteCount==0||manager==null||UnityEngine.Object.FindFirstObjectByType<FloorRenderer>()==null) return;
                manager.StopAllCoroutines(); manager.enabled=false; manager.songPlayer.Stop();
                foreach(var countdown in UnityEngine.Object.FindObjectsByType<GameStartCountdown>(FindObjectsSortMode.None)) countdown.gameObject.SetActive(false);
                foreach(var judge in UnityEngine.Object.FindObjectsByType<SaberCutJudge>(FindObjectsSortMode.None)) judge.autonomous=false;
                spawner.approachTime=2; spawner.SetExtraOffsetSeconds(0);
                string json="{\"bpm\":172,\"coordScale\":1,\"notes\":["+
                    "{\"time\":400,\"x\":-2.45,\"y\":-0.65,\"count\":12,\"type\":\"long\",\"color\":\"blue\"},"+
                    "{\"time\":540,\"x\":2.2,\"y\":0.1,\"count\":8,\"type\":\"long\",\"color\":\"gold\"},"+
                    "{\"time\":950,\"x\":0.5,\"y\":1.45,\"count\":1,\"color\":\"red\"},"+
                    "{\"time\":1150,\"x\":1.6,\"y\":-1.05,\"count\":1,\"color\":\"blue\",\"direction\":\"right\"},"+
                    "{\"time\":1530,\"x\":-0.9,\"y\":-0.7,\"count\":50,\"type\":\"long\",\"color\":\"default\"},"+
                    "{\"time\":1850,\"x\":0.7,\"y\":0.6,\"count\":1,\"color\":\"gold\"}]}";
                var chartType=typeof(NoteSpawner).Assembly.GetType("ChartData",true);
                typeof(NoteSpawner).GetMethod("SetChart").Invoke(spawner,new[]{JsonUtility.FromJson(json,chartType)}); spawner.Tick(0);
                target=new RenderTexture(Width,Height,24,RenderTextureFormat.ARGB32); target.Create(); pixels=new Texture2D(Width,Height,TextureFormat.RGB24,false);
                BuildCaption(); StartWorld(); phase=1; return;
            }
            if(phase==1)
            {
                if(++settle<12) return;
                if(stage.GetComponentsInChildren<Collider>().Length!=0) throw new InvalidOperationException("装飾にColliderがあります。");
                var watch=System.Diagnostics.Stopwatch.StartNew();
                for(int i=0;i<200;i++) Drive(i/60.0);
                watch.Stop();
                File.AppendAllText(Path.Combine(output,"runtime-check.txt"),
                    themeIndex+" "+stage.ActiveTheme+": renderers="+stage.GetComponentsInChildren<Renderer>().Length+
                    ", moving="+(world!=null?world.MovingObjectCount:foundry!=null?4:0)+", Tick CPU ms="+(watch.Elapsed.TotalMilliseconds/200).ToString("F4")+"\n");
                phase=2;
            }
            if(phase==2)
            {
                Drive(stills?1.5:frame/(double)Fps);
                var pulse=UnityEngine.Object.FindFirstObjectByType<GateBeatPulse>(); if(pulse!=null) pulse.Tick(Time.unscaledTimeAsDouble);
                Capture(Path.Combine(folder,"Frames",$"frame-{frame:D5}.png"));
                if(frame==(stills?1:36)) SavePoster();
                if(dynamicsDemo && frame==168) File.WriteAllBytes(Path.Combine(folder,"chorus-study.png"),pixels.EncodeToPNG());
                if(++frame<(stills?2:dynamicsDemo?Frames:192)) return;
                File.AppendAllText(Path.Combine(output,"runtime-check.txt"),"Captured "+frame+" frames: "+folder+"\n");
                if(++themeIndex<=lastTheme) { StartWorld(); phase=1; return; }
                target.Release(); UnityEngine.Object.DestroyImmediate(target); UnityEngine.Object.DestroyImmediate(pixels);
                if(newOverview!=null) UnityEngine.Object.DestroyImmediate(newOverview);
                if(oldOverview!=null) UnityEngine.Object.DestroyImmediate(oldOverview);
                SessionState.SetBool(Key,false); Debug.Log("[ScenicWorldsPreview] PASS: "+output); EditorApplication.Exit(0);
            }
        }
        catch(Exception exception)
        {
            SessionState.SetBool(Key,false); Debug.LogException(exception); EditorApplication.Exit(1);
        }
    }
    private static void StartWorld()
    {
        var old=UnityEngine.Object.FindFirstObjectByType<FloorRenderer>();
        Transform parent=old!=null?old.transform.parent:manager.transform;
        if(old!=null) UnityEngine.Object.DestroyImmediate(old.gameObject);
        stage=new GameObject("PreviewWorld").AddComponent<FloorRenderer>(); stage.transform.SetParent(parent,false);
        stage.randomizeOnPlay=false; stage.Build((StageTheme)themeIndex);
        world=stage.GetComponentInChildren<ScenicStageWorld>(); foundry=FoundryStageMotion.Ensure(stage);
        folder=Path.Combine(output,$"{themeIndex:D2}-{stage.ActiveTheme}"); Directory.CreateDirectory(Path.Combine(folder,"Frames"));
        caption.text=$"{themeIndex+1:00} / {StageThemeCatalog.Count}    {StageThemeCatalog.DisplayName(stage.ActiveTheme).ToUpperInvariant()}";
        frame=settle=0;
    }
    private static void Drive(double time)
    {
        float chorus=dynamicsDemo?demo.Evaluate(time):0;
        stage.Tick(time,chorus);
        if(world!=null) world.Tick(time,chorus);
        if(foundry!=null) foundry.Tick(time,chorus);
        if(dynamicsDemo && caption!=null)
            caption.text=$"{themeIndex+1:00} / {StageThemeCatalog.Count}    {StageThemeCatalog.DisplayName(stage.ActiveTheme).ToUpperInvariant()}     |     "+
                (chorus>.01f ? "CHORUS EFFECT STUDY (DEMO TIMING)" : "AMBIENT MOTION");
    }
    private static void BuildCaption()
    {
        var canvas=new GameObject("PreviewCaption",typeof(Canvas)).GetComponent<Canvas>();
        canvas.renderMode=RenderMode.ScreenSpaceOverlay; canvas.sortingOrder=100;
        var text=new GameObject("ThemeLabel",typeof(RectTransform),typeof(CanvasRenderer),typeof(TextMeshProUGUI)); text.transform.SetParent(canvas.transform,false);
        caption=text.GetComponent<TextMeshProUGUI>(); caption.font=TMP_Settings.defaultFontAsset; caption.fontSize=22; caption.fontStyle=FontStyles.Bold;
        caption.color=new Color(.82f,.91f,.94f); caption.raycastTarget=false; caption.outlineWidth=.18f; caption.outlineColor=new Color32(0,0,0,220);
        var rect=caption.rectTransform; rect.anchorMin=rect.anchorMax=rect.pivot=Vector2.zero; rect.anchoredPosition=new Vector2(24,14); rect.sizeDelta=new Vector2(1100,42);
    }
    private static void SavePoster()
    {
        File.WriteAllBytes(Path.Combine(folder,"preview.png"),pixels.EncodeToPNG());
        if(themeIndex>=4)
        {
            if(newOverview==null) newOverview=new Texture2D(Width*3,Height*2,TextureFormat.RGB24,false);
            int i=themeIndex-4; newOverview.SetPixels(i%3*Width,(1-i/3)*Height,Width,Height,pixels.GetPixels()); newOverview.Apply();
            File.WriteAllBytes(Path.Combine(output,"new-worlds-overview.png"),newOverview.EncodeToPNG());
        }
        else
        {
            if(oldOverview==null) oldOverview=new Texture2D(Width*2,Height*2,TextureFormat.RGB24,false);
            oldOverview.SetPixels(themeIndex%2*Width,(1-themeIndex/2)*Height,Width,Height,pixels.GetPixels()); oldOverview.Apply();
            File.WriteAllBytes(Path.Combine(output,"existing-worlds-overview.png"),oldOverview.EncodeToPNG());
        }
    }
    private static void Capture(string path)
    {
        var camera=Camera.main; var oldTarget=camera.targetTexture; var oldActive=RenderTexture.active;
        var canvases=UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None).Where(c=>c.isRootCanvas&&c.renderMode==RenderMode.ScreenSpaceOverlay).ToArray();
        try
        {
            camera.targetTexture=target; camera.aspect=Width/(float)Height;
            foreach(var canvas in canvases) { canvas.renderMode=RenderMode.ScreenSpaceCamera; canvas.worldCamera=camera; canvas.planeDistance=1; }
            Canvas.ForceUpdateCanvases(); RenderPipeline.SubmitRenderRequest(camera,new UniversalRenderPipeline.SingleCameraRequest{destination=target});
            RenderTexture.active=target; pixels.ReadPixels(new Rect(0,0,Width,Height),0,0); pixels.Apply(); File.WriteAllBytes(path,pixels.EncodeToPNG());
        }
        finally { foreach(var canvas in canvases) canvas.renderMode=RenderMode.ScreenSpaceOverlay; camera.targetTexture=oldTarget; RenderTexture.active=oldActive; }
    }
}
