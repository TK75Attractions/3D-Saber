using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// 実選曲UIを撮影。音源との合成用に、譜面プレビューだけ24fpsの確定時刻で描画する。
[InitializeOnLoad]
public static class SongChartPreviewCapture
{
    private const string Key="SongChartPreviewCapture.Running";
    private static string output;
    private static double started;
    private static int phase,frame;
    private static SongSelectController controller;
    static SongChartPreviewCapture()
    {
        EditorApplication.update+=Tick;
        EditorApplication.playModeStateChanged+=state=>
        {
            if(SessionState.GetBool(Key,false) && state==PlayModeStateChange.EnteredPlayMode)
            {
                output=SessionState.GetString(Key+".Output",""); started=EditorApplication.timeSinceStartup; phase=frame=0;
            }
        };
    }
    public static void Render()
    {
        if(!Application.isBatchMode) throw new InvalidOperationException("専用バッチから実行してください。");
        var args=Environment.GetCommandLineArgs(); int at=Array.IndexOf(args,"-chartPreviewOutput");
        if(at<0 || at+1>=args.Length) throw new ArgumentException("出力先が必要です。");
        output=Path.GetFullPath(args[at+1]); Directory.CreateDirectory(Path.Combine(output,"Frames"));
        SessionState.SetString(Key+".Output",output); SessionState.SetBool(Key,true);
        EditorSceneManager.OpenScene("Assets/Scenes/SongSelect.unity",OpenSceneMode.Single); EditorApplication.isPlaying=true;
    }
    private static void Tick()
    {
        if(!SessionState.GetBool(Key,false) || !EditorApplication.isPlaying || started<=0) return;
        try
        {
            if(EditorApplication.timeSinceStartup-started>600) throw new TimeoutException("選曲プレビュー撮影のタイムアウト");
            if(phase==0)
            {
                controller=UnityEngine.Object.FindFirstObjectByType<SongSelectController>();
                if(controller?.ChartPreview?.View==null) return;
                int index=Enumerable.Range(0,controller.SongCount).Single(i=>controller.SongIdAt(i)=="Epilogue");
                controller.Select(index); controller.SetDifficulty(2);
                foreach(var judge in UnityEngine.Object.FindObjectsByType<SaberCutJudge>(FindObjectsSortMode.None)) judge.autonomous=false;
                Capture(Camera.main,Path.Combine(output,"jacket-before.png"),1920,1080); phase=1; return;
            }
            var preview=controller.ChartPreview;
            if(phase==1)
            {
                if(!preview.IsPlaying || !preview.View.IsVisible) return;
                // 自動Updateと撮影時計が競合しないよう、表示だけを同じ描画処理で駆動する。
                controller.enabled=false;
                preview.View.Prepare(ChartLoader.LoadFromStreamingAssets("Epilogue","Hard"),preview.Window,StagePerformanceTimeline.Load("Epilogue"),"Hard");
                File.WriteAllText(Path.Combine(output,"capture-info.txt"),$"Actual SongSelect scene / Epilogue Hard / audioStart={preview.Window.Start:F3} / duration={preview.Window.Duration:F3} / 24fps / no scene or chart changes.\n");
                phase=2;
            }
            if(phase==2)
            {
                preview.View.Show(); preview.View.Tick(preview.Window.Start+frame/24.0);
                RenderPipeline.SubmitRenderRequest(preview.View.PreviewCamera,new UniversalRenderPipeline.SingleCameraRequest{destination=preview.View.Texture});
                Capture(Camera.main,Path.Combine(output,"Frames",$"frame-{frame:D5}.png"),1280,720);
                if(frame==60) Capture(Camera.main,Path.Combine(output,"preview-playing.png"),1920,1080);
                if(++frame<240) return;
                controller.StopPreview(); Capture(Camera.main,Path.Combine(output,"jacket-after.png"),1920,1080);
                SessionState.SetBool(Key,false); Debug.Log("[SongChartPreviewCapture] PASS"); EditorApplication.Exit(0);
            }
        }
        catch(Exception e) { SessionState.SetBool(Key,false); Debug.LogException(e); EditorApplication.Exit(1); }
    }
    private static void Capture(Camera camera,string path,int width,int height)
    {
        var oldTarget=camera.targetTexture; var oldActive=RenderTexture.active; float oldAspect=camera.aspect;
        var target=new RenderTexture(width,height,24,RenderTextureFormat.ARGB32); var image=new Texture2D(width,height,TextureFormat.RGB24,false);
        try
        {
            target.Create(); camera.targetTexture=target; camera.aspect=width/(float)height;
            var nav=UnityEngine.Object.FindFirstObjectByType<SongSelectSlashNav>(); if(nav!=null) nav.Tick(0);
            Canvas.ForceUpdateCanvases(); RenderPipeline.SubmitRenderRequest(camera,new UniversalRenderPipeline.SingleCameraRequest{destination=target});
            RenderTexture.active=target; image.ReadPixels(new Rect(0,0,width,height),0,0); image.Apply(); File.WriteAllBytes(path,image.EncodeToPNG());
        }
        finally { camera.targetTexture=oldTarget; camera.aspect=oldAspect; RenderTexture.active=oldActive; target.Release(); UnityEngine.Object.DestroyImmediate(target); UnityEngine.Object.DestroyImmediate(image); }
    }
}
