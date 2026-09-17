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
    static bool captureMotion;
    static int motionFrame;

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
            captureMotion = SessionState.GetBool(Key + ".Motion", false);
            concept = TitleConceptSelection.Current;
            phase = 0;
            motionFrame = 0;
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
        SessionState.SetBool(Key + ".Motion", Array.IndexOf(args, "-titleMotion") >= 0);
        EditorSceneManager.OpenScene("Assets/Scenes/Title.unity", OpenSceneMode.Single);
        EditorApplication.isPlaying = true;
    }

    static void Tick()
    {
        if (!SessionState.GetBool(Key, false) || !EditorApplication.isPlaying || started <= 0) return;
        try
        {
            if (EditorApplication.timeSinceStartup - started > 300) throw new TimeoutException("タイトル確認が時間切れになりました。");
            if (phase == 0 || phase == 2)
            {
                var note = UnityEngine.Object.FindFirstObjectByType<TitleStartNote>();
                var motion = UnityEngine.Object.FindFirstObjectByType<TitlePresentationMotion>();
                if (note == null || motion == null) return;
                concept = TitleConceptSelection.Current;
                // 検証中に実機の入力やマウス移動で切らないようにする。
                foreach (var judge in UnityEngine.Object.FindObjectsByType<SaberCutJudge>(FindObjectsSortMode.None))
                    judge.autonomous = false;
                if (EditorApplication.timeSinceStartup - sceneStarted < (phase == 2 ? .08 : 2)) return;
                motion.ManualTime = true;
                if (phase == 0 && captureMotion && motionFrame < 140)
                {
                    motion.SetPresentationTime(motionFrame / 20f, 0f);
                    Capture("frame-" + motionFrame.ToString("D4") + ".png", 960, 540);
                    motionFrame++;
                    return;
                }
                motion.SetPresentationTime(phase == 2 ? .08f : 3f, 0f);
                if (phase == 0)
                {
                    Capture("title-1080p.png", 1920, 1080);
                    Capture("title-720p.png", 1280, 720);
                    motion.SetPresentationTime(0f, 0f);
                    Capture("opening-000.png", 1280, 720);
                    motion.SetPresentationTime(.3f, 0f);
                    Capture("opening-030.png", 1280, 720);
                    motion.SetPresentationTime(.75f, 0f);
                    Capture("opening-075.png", 1280, 720);
                    for (int departureFrame = 0; departureFrame <= 24; departureFrame++)
                    {
                        motion.SetPresentationTime(3f, departureFrame / 24f);
                        Capture("dive-" + departureFrame.ToString("D3") + ".png", 960, 540);
                    }
                    motion.SetPresentationTime(3f, .4f);
                    Capture("departure-040.png", 1280, 720);
                    motion.SetPresentationTime(3f, 1f);
                    Capture("departure-100.png", 1280, 720);
                    motion.SetPresentationTime(3f, 0f);
                }
                var wordmarks = UnityEngine.Object.FindObjectsByType<TitleConceptAWordmark>(FindObjectsSortMode.None)
                    .Where(t => t.gameObject.activeInHierarchy).ToArray();
                string[] expectedWords = { "BEAT", "SLASH", "TRACE" };
                if (!wordmarks.Select(w => w.Word).OrderBy(w => w).SequenceEqual(expectedWords) ||
                    UnityEngine.Object.FindObjectsByType<TitleWordmark>(FindObjectsSortMode.None).Length != 0)
                    throw new InvalidOperationException("候補1の共通ロゴが3語だけ表示されていません。");
                string[] labels = UnityEngine.Object.FindObjectsByType<TextMeshProUGUI>(FindObjectsSortMode.None)
                    .Where(t => t.gameObject.activeInHierarchy).Select(t => t.name + ": " + t.text)
                    .Concat(wordmarks.Select(t => t.name + ": " + t.Word)).ToArray();
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
                motion.ManualTime = false;
                phase = phase == 2 ? 3 : 1;
            }
            else if (SceneManager.GetActiveScene().name == "SongSelect")
            {
                if (phase == 1)
                {
                    File.WriteAllText(OutputPath("runtime-check.txt"),
                        "PASS: Actual Title scene rendered at 1920x1080 and 1280x720, with the three approved wordmarks.\n" +
                        "PASS: Clicking the visible title note hits StartTarget, cuts the note and transitions to SongSelect.\n" +
                        "Hardware input and physical projector were not tested.\n");
                    TitleConceptSelection.Select(concept);
                    SceneManager.LoadScene("Title");
                    sceneStarted = EditorApplication.timeSinceStartup;
                    phase = 2;
                    return;
                }
                File.WriteAllText(OutputPath("opening-input-check.txt"),
                    "PASS: Clicking the note during the opening animation also cuts it and reaches SongSelect.\n");
                if (allConcepts && concept + 1 < TitleConceptSelection.Count)
                {
                    concept++;
                    TitleConceptSelection.Select(concept);
                    SceneManager.LoadScene("Title");
                    sceneStarted = EditorApplication.timeSinceStartup;
                    phase = 0;
                    motionFrame = 0;
                    return;
                }
                if (allConcepts) File.WriteAllText(Path.Combine(output, "all-concepts-check.txt"),
                    "PASS: All four backgrounds use the approved logo; 1080p/720p, opening and idle clicks reached SongSelect.\n");
                var choices = new List<int>();
                int previous = TitleConceptSelection.Current;
                for (int i = 0; i < 64; i++)
                {
                    int chosen = TitleConceptSelection.BeginTitle();
                    if (chosen < 0 || chosen >= TitleConceptSelection.Count || chosen == previous)
                        throw new InvalidOperationException("背景の抽選範囲または連続回避に失敗しました。");
                    choices.Add(chosen);
                    previous = chosen;
                }
                File.WriteAllText(Path.Combine(output, "random-background-check.txt"),
                    "PASS: 64 entries select valid backgrounds without consecutive repeats.\n" + string.Join(",", choices));
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
