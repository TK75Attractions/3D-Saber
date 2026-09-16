using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;

// 専用バッチで実タイトル画面と開始遷移を確認する。シーンやユーザーの設定は保存しない。
[InitializeOnLoad]
public static class TitleDesignCapture
{
    const string Key = "TitleDesignCapture.Active";
    static string output;
    static double started;
    static double sceneStarted;
    static int phase;
    static bool allConcepts;
    static int concept;

    static TitleDesignCapture()
    {
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += state =>
        {
            if (!SessionState.GetBool(Key, false) || state != PlayModeStateChange.EnteredPlayMode) return;
            output = SessionState.GetString(Key + ".Output", "");
            started = EditorApplication.timeSinceStartup;
            sceneStarted = started;
            allConcepts = SessionState.GetBool(Key + ".AllConcepts", false);
            concept = TitleConceptSelection.Current;
            phase = 0;
        };
    }

    public static void Render()
    {
        if (!Application.isBatchMode) throw new InvalidOperationException("専用バッチから実行してください。");
        string[] args = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(args, "-titleOutput");
        if (at < 0 || at + 1 >= args.Length) throw new ArgumentException("撮影先が必要です。");
        output = Path.GetFullPath(args[at + 1]);
        Directory.CreateDirectory(output);
        SessionState.SetString(Key + ".Output", output);
        SessionState.SetBool(Key, true);
        SessionState.SetBool(Key + ".AllConcepts", Array.IndexOf(args, "-titleAllConcepts") >= 0);
        EditorSceneManager.OpenScene("Assets/Scenes/Title.unity", OpenSceneMode.Single);
        EditorApplication.isPlaying = true;
    }

    static void Tick()
    {
        if (!SessionState.GetBool(Key, false) || !EditorApplication.isPlaying || started <= 0) return;
        try
        {
            if (EditorApplication.timeSinceStartup - started > 180) throw new TimeoutException("タイトル確認が時間切れになりました。");
            if (phase == 0)
            {
                var note = UnityEngine.Object.FindFirstObjectByType<TitleStartNote>();
                if (note == null) return;
                // 検証中に実機の入力やマウス移動で切らないようにする。
                foreach (var judge in UnityEngine.Object.FindObjectsByType<SaberCutJudge>(FindObjectsSortMode.None))
                    judge.autonomous = false;
                if (EditorApplication.timeSinceStartup - sceneStarted < 3) return;
                Capture("title-1080p.png", 1920, 1080);
                Capture("title-720p.png", 1280, 720);
                string[] labels = UnityEngine.Object.FindObjectsByType<TextMeshProUGUI>(FindObjectsSortMode.None)
                    .Where(t => t.gameObject.activeInHierarchy).Select(t => t.name + ": " + t.text)
                    .Concat(UnityEngine.Object.FindObjectsByType<TitleWordmark>(FindObjectsSortMode.None)
                        .Where(t => t.gameObject.activeInHierarchy).Select(t => t.name + ": " + t.Word)).ToArray();
                File.WriteAllLines(OutputPath("visible-labels.txt"), labels);
                // ノーツの見た目と透明な開始ボタンの当たり領域が一致していることも確認する。
                var eventSystem = EventSystem.current;
                if (eventSystem == null) throw new InvalidOperationException("EventSystemがありません。");
                var pointer = new PointerEventData(eventSystem)
                {
                    position = Camera.main.WorldToScreenPoint(note.transform.position),
                    button = PointerEventData.InputButton.Left
                };
                var hits = new List<RaycastResult>();
                eventSystem.RaycastAll(pointer, hits);
                var button = hits.Count > 0 ? hits[0].gameObject.GetComponentInParent<Button>() : null;
                if (button == null || button.name != "StartTarget")
                    throw new InvalidOperationException("開始ノーツの位置に開始ボタンがありません。");
                ExecuteEvents.Execute(button.gameObject, pointer, ExecuteEvents.pointerClickHandler);
                if (!note.Note.IsCut) throw new InvalidOperationException("開始ノーツを切れませんでした。");
                phase = 1;
            }
            else if (SceneManager.GetActiveScene().name == "SongSelect")
            {
                File.WriteAllText(OutputPath("runtime-check.txt"),
                    "PASS: Actual Title scene rendered at 1920x1080 and 1280x720.\n" +
                    "PASS: Clicking the visible title note hits StartTarget, cuts the note and transitions to SongSelect.\n" +
                    "Hardware input and physical projector were not tested.\n");
                if (allConcepts && concept + 1 < TitleConceptSelection.Count)
                {
                    concept++;
                    TitleConceptSelection.Select(concept);
                    SceneManager.LoadScene("Title");
                    sceneStarted = EditorApplication.timeSinceStartup;
                    phase = 0;
                    return;
                }
                if (allConcepts) File.WriteAllText(Path.Combine(output, "all-concepts-check.txt"),
                    "PASS: All four title concepts rendered at 1080p/720p and each clickable note transitioned to SongSelect.\n");
                SessionState.SetBool(Key, false);
                Debug.Log("[TitleDesignCapture] PASS");
                EditorApplication.Exit(0);
            }
        }
        catch (Exception e)
        {
            SessionState.SetBool(Key, false);
            Debug.LogException(e);
            EditorApplication.Exit(1);
        }
    }

    static string OutputPath(string filename)
    {
        string directory = allConcepts ? Path.Combine(output, "concept-" + concept) : output;
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, filename);
    }

    static void Capture(string filename, int width, int height)
    {
        var camera = Camera.main;
        if (camera == null) throw new InvalidOperationException("タイトルのカメラがありません。");
        var oldTarget = camera.targetTexture;
        var oldActive = RenderTexture.active;
        float oldAspect = camera.aspect;
        var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
        var image = new Texture2D(width, height, TextureFormat.RGB24, false);
        try
        {
            target.Create();
            camera.targetTexture = target;
            camera.aspect = width / (float)height;
            Canvas.ForceUpdateCanvases();
            RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
            RenderTexture.active = target;
            image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            image.Apply();
            File.WriteAllBytes(OutputPath(filename), image.EncodeToPNG());
        }
        finally
        {
            camera.targetTexture = oldTarget;
            camera.aspect = oldAspect;
            RenderTexture.active = oldActive;
            target.Release();
            UnityEngine.Object.DestroyImmediate(target);
            UnityEngine.Object.DestroyImmediate(image);
        }
    }
}
