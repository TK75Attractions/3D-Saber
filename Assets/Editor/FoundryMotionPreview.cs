using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

// 専用バッチの一時シーンだけで撮影。見やすさ比較のためノーツは固定、背景は曲時計と同じ秒単位で動かす。
[InitializeOnLoad]
public static class FoundryMotionPreview
{
    private const string Key = "FoundryMotionPreview.Running";
    private const int Width = 1280, Height = 720, Fps = 24, FrameCount = 288;
    private static int phase, frame, settle;
    private static double started;
    private static string output;
    private static FoundryStageMotion motion;
    private static RenderTexture target;
    private static Texture2D pixels;
    private static int maxParticles;

    static FoundryMotionPreview()
    {
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += state =>
        {
            if (!SessionState.GetBool(Key, false) || state != PlayModeStateChange.EnteredPlayMode) return;
            started = EditorApplication.timeSinceStartup;
            phase = frame = settle = 0;
            output = SessionState.GetString(Key + ".Output", "");
        };
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void PrepareSong()
    {
        if (!SessionState.GetBool(Key, false)) return;
        GameSession.SelectedSongId = "ElDorado";
        GameSession.SelectedDifficulty = "normal";
        GameSession.IsCalibrationMode = false;
        SceneManager.sceneLoaded += ForceFoundry;
    }

    private static void ForceFoundry(Scene scene, LoadSceneMode mode)
    {
        if (scene.name != "Game" || !SessionState.GetBool(Key, false)) return;
        SceneManager.sceneLoaded -= ForceFoundry;
        var stage = new GameObject("PreviewFoundry").AddComponent<FloorRenderer>();
        stage.randomizeOnPlay = false;
        stage.Build(StageTheme.AmberFoundry);
    }

    public static void Render()
    {
        if (!Application.isBatchMode) throw new InvalidOperationException("専用バッチから呼び出してください。");
        var args = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(args, "-foundryPreviewOutput");
        if (at < 0 || at + 1 >= args.Length) throw new ArgumentException("出力先が必要です。");
        output = Path.GetFullPath(args[at + 1]);
        Directory.CreateDirectory(Path.Combine(output, "Frames"));
        SessionState.SetString(Key + ".Output", output);
        SessionState.SetBool(Key, true);
        EditorSceneManager.OpenScene("Assets/Scenes/Game.unity", OpenSceneMode.Single);
        GameSession.SelectedSongId = "ElDorado";
        GameSession.SelectedDifficulty = "normal";
        GameSession.IsCalibrationMode = false;
        EditorApplication.isPlaying = true;
    }

    private static void Tick()
    {
        if (!SessionState.GetBool(Key, false) || !EditorApplication.isPlaying || started <= 0) return;
        try
        {
            if (EditorApplication.timeSinceStartup - started > 480) throw new TimeoutException("背景の描画確認がタイムアウトしました。");
            if (phase == 0)
            {
                var spawner = UnityEngine.Object.FindFirstObjectByType<NoteSpawner>();
                var manager = UnityEngine.Object.FindFirstObjectByType<GamePlayManager>();
                motion = UnityEngine.Object.FindFirstObjectByType<FoundryStageMotion>();
                if (spawner == null || spawner.TotalNoteCount == 0 || manager == null || motion == null || motion.LastTickSeconds < .5) return;
                File.WriteAllText(Path.Combine(output, "runtime-check.txt"), "Actual Game scene: AmberFoundry\nGamePlayManager drove motion: " + motion.LastTickSeconds.ToString("F3") + " seconds\n");
                manager.StopAllCoroutines(); manager.enabled = false; manager.songPlayer.Stop();
                foreach (var countdown in UnityEngine.Object.FindObjectsByType<GameStartCountdown>(FindObjectsSortMode.None)) countdown.gameObject.SetActive(false);
                foreach (var judge in UnityEngine.Object.FindObjectsByType<SaberCutJudge>(FindObjectsSortMode.None)) judge.autonomous = false;
                spawner.approachTime = 2;
                spawner.SetExtraOffsetSeconds(0);
                string json = "{\"bpm\":172,\"coordScale\":1,\"notes\":[" +
                    "{\"time\":400,\"x\":-2.45,\"y\":-0.65,\"count\":12,\"type\":\"long\",\"color\":\"blue\"}," +
                    "{\"time\":540,\"x\":2.2,\"y\":0.1,\"count\":8,\"type\":\"long\",\"color\":\"gold\"}," +
                    "{\"time\":950,\"x\":0.5,\"y\":1.45,\"count\":1,\"color\":\"red\"}," +
                    "{\"time\":1150,\"x\":1.6,\"y\":-1.05,\"count\":1,\"color\":\"blue\",\"direction\":\"right\"}," +
                    "{\"time\":1530,\"x\":-0.9,\"y\":-0.7,\"count\":50,\"type\":\"long\",\"color\":\"default\"}," +
                    "{\"time\":1850,\"x\":0.7,\"y\":0.6,\"count\":1,\"color\":\"gold\"}]}";
                var chartType = typeof(NoteSpawner).Assembly.GetType("ChartData", true);
                typeof(NoteSpawner).GetMethod("SetChart").Invoke(spawner, new[] { JsonUtility.FromJson(json, chartType) });
                spawner.Tick(0);
                target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32); target.Create();
                pixels = new Texture2D(Width, Height, TextureFormat.RGB24, false);
                phase = 1;
                return;
            }
            if (phase == 1)
            {
                if (++settle < 25) return;
                if (motion.GetComponentsInChildren<Collider>().Length != 0) throw new InvalidOperationException("装飾に判定が追加されています。");
                var watch = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 500; i++) motion.Tick(i / 60.0);
                watch.Stop();
                File.AppendAllText(Path.Combine(output, "runtime-check.txt"),
                    "Background Tick average CPU: " + (watch.Elapsed.TotalMilliseconds / 500).ToString("F4") + " ms (Editor, 500 calls; NOT full frame/GPU benchmark)\n" +
                    "Equipment draw renderers: " + motion.GetComponentsInChildren<Renderer>().Length + "\nCamera: " + Camera.main.transform.position + "\n");
                phase = 2;
            }
            if (phase == 2)
            {
                motion.Tick(frame / (double)Fps);
                maxParticles = Math.Max(maxParticles, motion.LiveParticleCount);
                var pulse = UnityEngine.Object.FindFirstObjectByType<GateBeatPulse>();
                if (pulse != null) pulse.Tick(Time.unscaledTimeAsDouble);
                Capture(Path.Combine(output, "Frames", $"frame-{frame:D5}.png"));
                if (frame == 31) File.WriteAllBytes(Path.Combine(output, "foundry-motion.png"), pixels.EncodeToPNG());
                if (frame == 31)
                {
                    // 同一カメラ・同一ノーツで効果なしの比較画像も残す。
                    motion.gameObject.SetActive(false);
                    Capture(Path.Combine(output, "foundry-before.png"));
                    motion.gameObject.SetActive(true);
                }
                if (++frame < FrameCount) return;
                File.AppendAllText(Path.Combine(output, "runtime-check.txt"),
                    "Frames: " + frame + " at " + Fps + " fps; background-only demonstration (notes held still).\nPeak live particles: " + maxParticles +
                    " / 96\nScene, chart, HUD, camera and note assets were not saved or edited.\n");
                target.Release(); UnityEngine.Object.DestroyImmediate(target); UnityEngine.Object.DestroyImmediate(pixels);
                SessionState.SetBool(Key, false);
                Debug.Log("[FoundryMotionPreview] PASS: " + output);
                EditorApplication.Exit(0);
            }
        }
        catch (Exception exception)
        {
            SessionState.SetBool(Key, false);
            Debug.LogException(exception); EditorApplication.Exit(1);
        }
    }

    private static void Capture(string path)
    {
        var camera = Camera.main;
        var oldTarget = camera.targetTexture; var oldActive = RenderTexture.active;
        var canvases = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None)
            .Where(c => c.isRootCanvas && c.renderMode == RenderMode.ScreenSpaceOverlay).ToArray();
        try
        {
            camera.targetTexture = target; camera.aspect = Width / (float)Height;
            foreach (var canvas in canvases)
            {
                canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = camera; canvas.planeDistance = 1;
            }
            Canvas.ForceUpdateCanvases();
            RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
            RenderTexture.active = target; pixels.ReadPixels(new Rect(0, 0, Width, Height), 0, 0); pixels.Apply();
            File.WriteAllBytes(path, pixels.EncodeToPNG());
        }
        finally
        {
            foreach (var canvas in canvases) canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            camera.targetTexture = oldTarget; RenderTexture.active = oldActive;
        }
    }
}
