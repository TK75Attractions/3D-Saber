using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// 実シーンの曲選択を複数状態で撮影する。保存やユーザー設定の変更は行わない。
[InitializeOnLoad]
public static class SongSelectDesignPreview
{
    const string Key = "SongSelectDesignPreview.Running";
    static double started, ready;
    static int state;
    static string output;
    static SongSelectController controller;
    static SongSelectDesignPreview()
    {
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += change =>
        {
            if (SessionState.GetBool(Key, false) && change == PlayModeStateChange.EnteredPlayMode)
            {
                started = EditorApplication.timeSinceStartup; ready = 0; state = 0;
                output = SessionState.GetString(Key + ".Output", "");
            }
        };
    }
    public static void Render()
    {
        if (!Application.isBatchMode) throw new InvalidOperationException("専用バッチのみ");
        var args = Environment.GetCommandLineArgs(); int at = Array.IndexOf(args, "-songSelectPreviewOutput");
        if (at < 0 || at + 1 >= args.Length) throw new ArgumentException("出力先が必要です");
        output = Path.GetFullPath(args[at + 1]); Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output,"runtime-check.txt"),"");
        SessionState.SetString(Key + ".Output", output); SessionState.SetBool(Key, true);
        EditorSceneManager.OpenScene("Assets/Scenes/SongSelect.unity", OpenSceneMode.Single);
        EditorApplication.isPlaying = true;
    }
    static void Tick()
    {
        if (!SessionState.GetBool(Key, false) || !EditorApplication.isPlaying || started <= 0) return;
        try
        {
            if (EditorApplication.timeSinceStartup - started > 120) throw new TimeoutException("選曲画面が準備できません");
            if (ready == 0)
            {
                controller = UnityEngine.Object.FindFirstObjectByType<SongSelectController>();
                if (controller == null || controller.SongCount == 0 || GameObject.Find("DifficultyRibbonRow") == null) return;
                AudioListener.pause = true;
                foreach (var judge in UnityEngine.Object.FindObjectsByType<SaberCutJudge>(FindObjectsSortMode.None)) judge.autonomous = false;
                Choose(); return;
            }
            if (EditorApplication.timeSinceStartup - ready < 2) return;
            var label = state == 0 ? "school-easy" : state == 1 ? "school-master" : "el-dorado-normal";
            Capture(Path.Combine(output, label + ".png"), 1920, 1080);
            if (state == 1) Capture(Path.Combine(output, "school-master-720p.png"), 1280, 720);
            File.AppendAllText(Path.Combine(output, "runtime-check.txt"), $"{label}: selected={controller.SongIdAt(controller.SelectedIndex)}, level={controller.CurrentDifficultyDisplayLevel()}, canStart={controller.startButton.interactable}, navNotes={UnityEngine.Object.FindObjectsByType<CuttableNote>(FindObjectsSortMode.None).Length}; scene not saved.\n");
            if (++state < 3) { Choose(); return; }
            VerifyInteractions();
            SessionState.SetBool(Key, false); Debug.Log("[SongSelectDesignPreview] PASS"); EditorApplication.Exit(0);
        }
        catch (Exception e) { SessionState.SetBool(Key, false); Debug.LogException(e); EditorApplication.Exit(1); }
    }
    static void Choose()
    {
        string id = state < 2 ? "Epilogue" : "ElDorado";
        int index = Enumerable.Range(0, controller.SongCount).FirstOrDefault(i => controller.SongIdAt(i) == id);
        controller.Select(index); controller.SetDifficulty(state == 0 ? 0 : state == 1 ? 2 : 1);
        ready = EditorApplication.timeSinceStartup;
    }
    static void VerifyInteractions()
    {
        // 装飾パネルが入力を遮っていないことを実際の UI レイキャストで確認する。
        Canvas.ForceUpdateCanvases();
        foreach (var button in controller.difficultyButtons)
        {
            AssertHit(button);
            ExecuteEvents.Execute(button.gameObject,new PointerEventData(EventSystem.current){button=PointerEventData.InputButton.Left},ExecuteEvents.pointerClickHandler);
            if (controller.SelectedDifficultyIndex != Array.IndexOf(controller.difficultyButtons,button))
                throw new InvalidOperationException("難易度ボタンのクリックが反映されません");
        }
        AssertHit(controller.startButton);
        AssertHit(GameObject.Find("BackToTitle").GetComponent<Button>());
        AssertHit(GameObject.Find("CalibrationButton").GetComponent<Button>());
        var row=GameObject.Find("WheelRow_"+controller.SelectedIndex).GetComponent<Button>();
        AssertHit(row);
        // クリック後に再描画する曲を選び、曲リスト側の接続も確認する。
        int next=Mathf.Min(controller.SelectedIndex+1,controller.SongCount-1);
        row=GameObject.Find("WheelRow_"+next).GetComponent<Button>();
        AssertHit(row);
        ExecuteEvents.Execute(row.gameObject,new PointerEventData(EventSystem.current){button=PointerEventData.InputButton.Left},ExecuteEvents.pointerClickHandler);
        if(controller.SelectedIndex!=next) throw new InvalidOperationException("曲リストのクリックが反映されません");
        File.AppendAllText(Path.Combine(output,"runtime-check.txt"),"UI raycasts: START / Back / Calibration / 3 difficulties / track rows PASS; difficulty and track clicks PASS.\n");
    }
    static void AssertHit(Button button)
    {
        var rt=button.targetGraphic.rectTransform;
        var canvas=rt.GetComponentInParent<Canvas>();
        var pointer=new PointerEventData(EventSystem.current){position=RectTransformUtility.WorldToScreenPoint(canvas.worldCamera,rt.TransformPoint(rt.rect.center))};
        var hits=new List<RaycastResult>();EventSystem.current.RaycastAll(pointer,hits);
        if(hits.Count==0 || hits[0].gameObject.GetComponentInParent<Button>()!=button)
            throw new InvalidOperationException(button.name+": パネル中心でボタンに届きません");
    }
    static void Capture(string path, int width, int height)
    {
        var camera = Camera.main; var oldTarget = camera.targetTexture; var oldActive = RenderTexture.active;
        var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
        var pixels = new Texture2D(width, height, TextureFormat.RGB24, false);
        try
        {
            target.Create(); camera.targetTexture = target; camera.aspect = width / (float)height;
            var nav=UnityEngine.Object.FindFirstObjectByType<SongSelectSlashNav>(); if(nav!=null) nav.Tick(0);
            Canvas.ForceUpdateCanvases();
            // 見た目の確認が空のパネルを見逃さないよう、描画メッシュも検証する。
            foreach (var graphic in UnityEngine.Object.FindObjectsByType<UnityEngine.UI.Graphic>(FindObjectsSortMode.None))
            {
                if (!(graphic is SongSelectPanelGraphic || graphic is SongSelectBackdropGraphic || graphic is SongSelectCoverGraphic)) continue;
                if (graphic.canvasRenderer == null) throw new InvalidOperationException(graphic.name + ": CanvasRenderer がありません");
                var mesh = graphic.canvasRenderer.GetMesh();
                if (mesh == null || mesh.vertexCount == 0) throw new InvalidOperationException(graphic.name + ": 描画メッシュが空です");
            }
            RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
            RenderTexture.active = target; pixels.ReadPixels(new Rect(0, 0, width, height), 0, 0); pixels.Apply();
            File.WriteAllBytes(path, pixels.EncodeToPNG());
        }
        finally
        {
            camera.targetTexture = oldTarget; RenderTexture.active = oldActive;
            target.Release(); UnityEngine.Object.DestroyImmediate(target); UnityEngine.Object.DestroyImmediate(pixels);
        }
    }
}
