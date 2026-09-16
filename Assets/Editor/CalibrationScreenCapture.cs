using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// 実際のGameシーンを保存せず撮影。自動カットによる表示検査を実機測定と区別する。
[InitializeOnLoad]
public static class CalibrationScreenCapture
{
    const string Key="CalibrationCapture.Active";
    static string output; static double started; static int phase;
    static CalibrationController controller;
    static CalibrationScreenCapture()
    {
        EditorApplication.update+=Tick;
        EditorApplication.playModeStateChanged+=state=>
        {
            if(!SessionState.GetBool(Key,false))return;
            if(state==PlayModeStateChange.EnteredPlayMode)
            {
                GameSession.IsCalibrationMode=true;
                output=SessionState.GetString(Key+".Output","");started=EditorApplication.timeSinceStartup;phase=0;
                SceneManager.LoadScene("Game");
            }
        };
    }
    public static void Render()
    {
        if(!Application.isBatchMode)throw new InvalidOperationException("専用バッチで実行してください。");
        var args=Environment.GetCommandLineArgs();int at=Array.IndexOf(args,"-calibrationOutput");
        if(at<0)throw new ArgumentException("出力先を指定してください。");
        output=Path.GetFullPath(args[at+1]);Directory.CreateDirectory(output);
        SessionState.SetString(Key+".Output",output);SessionState.SetBool(Key,true);
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
        GameSession.IsCalibrationMode=true;EditorApplication.isPlaying=true;
    }
    static void Tick()
    {
        if(!SessionState.GetBool(Key,false)||!EditorApplication.isPlaying||started<=0)return;
        try
        {
            if(EditorApplication.timeSinceStartup-started>150)throw new TimeoutException("調整画面の撮影タイムアウト");
            if(phase==0)
            {
                controller=UnityEngine.Object.FindFirstObjectByType<CalibrationController>();if(controller?.Draft==null)return;
                if(EditorApplication.timeSinceStartup-started<3)return;
                foreach(var canvas in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None))
                    if(canvas.isRootCanvas) { canvas.renderMode=RenderMode.ScreenSpaceCamera;canvas.worldCamera=Camera.main;canvas.planeDistance=.5f; }
                Capture("01-ready.png",1920,1080);Capture("01-ready-720p.png",1280,720);
                controller.BeginLivePractice();phase=1;return;
            }
            if(phase==1)
            {
                if(!controller.IsLive||controller.RunTime<6)return;
                var note=UnityEngine.Object.FindObjectsByType<CuttableNote>(FindObjectsSortMode.None)
                    .FirstOrDefault(n=>!n.IsFinalized&&controller.RunTime-n.HitTime>.012&&controller.RunTime-n.HitTime<.070);
                if(note==null)return;
                note.Cut(note.transform.position,Vector3.right*6,CutDirection.None,note.RequiredHand);
                if(!controller.HasLastError)return;
                var caption=SongSelectVisuals.Label(controller.Overlay.transform,"CaptureLabel",
                    "表示確認用の自動カット  /  実機の測定値ではありません",20,new Vector2(0,-328),new Vector2(1500,40),Color.white,TMPro.TextAlignmentOptions.Center);
                Capture("02-live-feedback.png",1920,1080);Capture("02-live-feedback-720p.png",1280,720);
                UnityEngine.Object.Destroy(caption.gameObject);controller.Overlay.OpenSettings();phase=2;return;
            }
            if(phase==2)
            {
                Capture("03-settings.png",1920,1080);controller.Overlay.OnBackClicked();
                controller.ChangeOffset(10);controller.Overlay.OnBackClicked();Capture("04-discard-confirmation.png",1920,1080);
                // 文字切れの検査。境界値と長い説明文も同じ画面で確認する。
                controller.Overlay.Tick();
                File.WriteAllText(Path.Combine(output,"capture-info.txt"),"Actual Game scene / live calibration UI / feedback uses an automated CuttableNote.Cut event, not a hardware measurement / no settings committed / notes, camera and scoring windows unchanged.\n");
                SessionState.SetBool(Key,false);GameSession.IsCalibrationMode=false;Debug.Log("[CalibrationCapture] PASS");EditorApplication.Exit(0);
            }
        }
        catch(Exception e) {SessionState.SetBool(Key,false);Debug.LogException(e);EditorApplication.Exit(1);}
    }
    static void Capture(string name,int width,int height)
    {
        var camera=Camera.main;var old=camera.targetTexture;var active=RenderTexture.active;float aspect=camera.aspect;
        var target=new RenderTexture(width,height,24,RenderTextureFormat.ARGB32);var image=new Texture2D(width,height,TextureFormat.RGB24,false);
        try
        {
            target.Create();camera.targetTexture=target;camera.aspect=width/(float)height;
            controller.Overlay.Tick();Canvas.ForceUpdateCanvases();
            RenderPipeline.SubmitRenderRequest(camera,new UniversalRenderPipeline.SingleCameraRequest{destination=target});
            RenderTexture.active=target;image.ReadPixels(new Rect(0,0,width,height),0,0);image.Apply();
            File.WriteAllBytes(Path.Combine(output,name),image.EncodeToPNG());
        }
        finally {camera.targetTexture=old;camera.aspect=aspect;RenderTexture.active=active;target.Release();UnityEngine.Object.DestroyImmediate(target);UnityEngine.Object.DestroyImmediate(image);}
    }
}
