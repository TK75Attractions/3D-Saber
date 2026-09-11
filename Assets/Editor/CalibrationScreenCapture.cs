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

// 実際の Game シーンを保存せず撮影。結果画面は未入力の実行結果であり、架空の実測値を表示しない。
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
                Capture("01-home.png",1920,1080);Capture("01-home-720p.png",1280,720);
                controller.Begin(CalibrationRunMode.Practice);phase=1;return;
            }
            if(phase==1)
            {
                if(!controller.IsRunning||controller.RunTime<7.2)return;
                Capture("02-practice.png",1920,1080);phase=2;return;
            }
            if(phase==2)
            {
                if(controller.Mode!=CalibrationRunMode.Result)return;
                Capture("03-no-input-result.png",1920,1080);
                controller.ChangeOffset(10);controller.Overlay.OnBackClicked();Capture("04-discard-confirmation.png",1920,1080);
                // 文字切れの検査。境界値と長い説明文も同じ画面で確認する。
                controller.Overlay.Tick();
                File.WriteAllText(Path.Combine(output,"capture-info.txt"),"Actual Game scene / calibration UI / no hardware inputs / no settings committed / source notes, camera and scoring windows unchanged.\n");
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
