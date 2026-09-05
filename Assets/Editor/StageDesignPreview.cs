using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// 専用のバッチ Editor でのみ動く、実プレイ画面の描画確認。シーン・譜面は保存しない。
[InitializeOnLoad]
public static class StageDesignPreview
{
    private const string Running = "StageDesignPreview.Running";
    private const string OutputKey = "StageDesignPreview.Output";
    private static int frame;
    private static int phase;
    private static double started;
    private static string output;
    private static bool allVariants;
    private static int themeIndex;
    private static string capturePath;
    private static Texture2D overview;

    static StageDesignPreview()
    {
        EditorApplication.playModeStateChanged += OnPlayState;
        EditorApplication.update += Tick;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void SelectPreviewSongAfterReload()
    {
        if (!SessionState.GetBool(Running, false)) return;
        GameSession.SelectedSongId = "ElDorado";
        GameSession.SelectedDifficulty = "normal";
        GameSession.IsCalibrationMode = false;
    }

    public static void Render()
    {
        if (!Application.isBatchMode) throw new InvalidOperationException("専用のバッチ Editor から呼び出してください。");
        string[] args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, "-stagePreviewOutput");
        if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("-stagePreviewOutput が必要です。");
        output = Path.GetFullPath(args[index + 1]);
        Directory.CreateDirectory(output);
        SessionState.SetString(OutputKey, output);
        SessionState.SetBool(Running, true);
        SessionState.SetBool(Running + ".AllVariants", Array.IndexOf(args, "-stagePreviewAll") >= 0);
        EditorSceneManager.OpenScene("Assets/Scenes/Game.unity", OpenSceneMode.Single);
        GameSession.SelectedSongId = "ElDorado";
        GameSession.SelectedDifficulty = "normal";
        GameSession.IsCalibrationMode = false;
        EditorApplication.isPlaying = true;
    }

    private static void OnPlayState(PlayModeStateChange state)
    {
        if (!SessionState.GetBool(Running, false)) return;
        if (state == PlayModeStateChange.EnteredPlayMode)
        {
            phase = 0;
            started = EditorApplication.timeSinceStartup;
            output = SessionState.GetString(OutputKey, "");
            allVariants = SessionState.GetBool(Running + ".AllVariants", false);
            themeIndex = 0;
        }
    }

    private static void Tick()
    {
        if (!SessionState.GetBool(Running, false) || !EditorApplication.isPlaying || started <= 0) return;
        try
        {
            if (EditorApplication.timeSinceStartup - started > 100)
                throw new TimeoutException("描画確認がタイムアウトしました。");
            var spawner = UnityEngine.Object.FindFirstObjectByType<NoteSpawner>();
            if (phase == 0)
            {
                if (spawner == null || spawner.TotalNoteCount == 0 || UnityEngine.Object.FindFirstObjectByType<FloorRenderer>() == null) return;
                var manager = UnityEngine.Object.FindFirstObjectByType<GamePlayManager>();
                manager.StopAllCoroutines();
                manager.enabled = false;
                foreach (var pulse in UnityEngine.Object.FindObjectsByType<GateBeatPulse>(FindObjectsSortMode.None)) pulse.enabled = false;
                if (manager.songPlayer != null) manager.songPlayer.GetComponent<AudioSource>().Stop();
                foreach (var countdown in UnityEngine.Object.FindObjectsByType<GameStartCountdown>(FindObjectsSortMode.None))
                    countdown.gameObject.SetActive(false);
                spawner.approachTime = 2f;
                // 旧 Note-Recorder の同名型を避け、ゲーム本体のアセンブリを明示する。
                string json = "{\"bpm\":172,\"coordScale\":1,\"notes\":[" +
                    "{\"time\":400,\"x\":-2.45,\"y\":-0.65,\"count\":12,\"type\":\"long\",\"color\":\"blue\"}," +
                    "{\"time\":540,\"x\":2.2,\"y\":0.1,\"count\":8,\"type\":\"long\",\"color\":\"gold\"}," +
                    "{\"time\":950,\"x\":0.5,\"y\":1.45,\"count\":1,\"color\":\"red\"}," +
                    "{\"time\":1150,\"x\":1.6,\"y\":-1.05,\"count\":1,\"color\":\"blue\",\"direction\":\"right\"}," +
                    "{\"time\":1530,\"x\":-0.9,\"y\":-0.7,\"count\":50,\"type\":\"long\",\"color\":\"default\"}," +
                    "{\"time\":1850,\"x\":0.7,\"y\":0.6,\"count\":1,\"color\":\"gold\"}]}";
                var chartType = typeof(NoteSpawner).Assembly.GetType("ChartData", true);
                object chart = JsonUtility.FromJson(json, chartType);
                typeof(NoteSpawner).GetMethod("SetChart").Invoke(spawner, new[] { chart });
                spawner.Tick(0);
                if (allVariants) ReplaceStage(StageTheme.ObsidianRelay);
                frame = 0; phase = 1;
                return;
            }
            if (phase == 1 && ++frame > 35)
            {
                var stage = UnityEngine.Object.FindFirstObjectByType<FloorRenderer>();
                var labels = UnityEngine.Object.FindObjectsByType<LongNoteCountStyle>(FindObjectsSortMode.None);
                if (labels.Length != 3) throw new InvalidOperationException("残数ラベルが揃っていません: " + labels.Length);
                foreach (var label in labels)
                {
                    var text = label.GetComponent<TMPro.TextMeshPro>();
                    text.ForceMeshUpdate();
                    if (text.font.name != LongNoteCountStyle.FontName || text.textInfo.characterCount == 0)
                        throw new InvalidOperationException("数字の描画データが不正です。");
                }
                var gate = UnityEngine.Object.FindFirstObjectByType<JudgeGateFrame>();
                if (gate == null || gate.GetComponentsInChildren<Collider>().Length != 0)
                    throw new InvalidOperationException("ゲートが未生成、または装飾にColliderがあります。");
                File.AppendAllText(Path.Combine(output, "runtime-check.txt"),
                    "Runtime stage: " + stage.ActiveTheme + "\nDraw groups: " + stage.GetComponentsInChildren<MeshRenderer>().Length +
                    "\nCount labels: " + string.Join(", ", labels.Select(l => l.GetComponent<TMPro.TextMeshPro>().text)) +
                    "\nCamera: " + Camera.main.transform.position + "\nScene was not saved.\n");
                capturePath = Path.Combine(output, allVariants ? "stage-" + themeIndex + "-" + stage.ActiveTheme + ".png" : "gameplay-stage.png");
                CaptureCamera(capturePath);
                if (allVariants && ++themeIndex < StageThemeCatalog.Count)
                {
                    ReplaceStage((StageTheme)themeIndex);
                    frame = 0;
                    return;
                }
                phase = 2; frame = 0;
                return;
            }
            if (phase == 2 && ++frame > 20)
            {
                var path = capturePath;
                if (!File.Exists(path) || new FileInfo(path).Length < 10000) return;
                SessionState.SetBool(Running, false);
                Debug.Log("[StageDesignPreview] PASS: " + path);
                EditorApplication.Exit(0);
            }
        }
        catch (Exception exception)
        {
            SessionState.SetBool(Running, false);
            Debug.LogException(exception);
            EditorApplication.Exit(1);
        }
    }

    // 検証用の一時インスタンスだけを交換。判定・カメラ・ノーツ・シーン資産は変更しない。
    private static void ReplaceStage(StageTheme theme)
    {
        var previous = UnityEngine.Object.FindFirstObjectByType<FloorRenderer>();
        Transform parent = previous != null ? previous.transform.parent : null;
        if (previous != null) UnityEngine.Object.DestroyImmediate(previous.gameObject);
        var go = new GameObject("FloorRenderer");
        go.transform.SetParent(parent, false);
        var stage = go.AddComponent<FloorRenderer>();
        stage.randomizeOnPlay = false;
        stage.Build(theme);
    }

    // バッチ Editor には表示中の GameView がないため、URP の描画要求で直接出力する。
    private static void CaptureCamera(string path)
    {
        var camera = Camera.main;
        var target = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32);
        var pixels = new Texture2D(1920, 1080, TextureFormat.RGB24, false);
        var oldTarget = camera.targetTexture;
        var oldActive = RenderTexture.active;
        var canvases = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None)
            .Where(c => c.isRootCanvas && c.renderMode == RenderMode.ScreenSpaceOverlay).ToArray();
        try
        {
            target.Create();
            camera.targetTexture = target;
            camera.aspect = 1920f / 1080f;
            // 保存しない検証用インスタンスだけをカメラ描画に含める。UIのスタイルは変更しない。
            foreach (var canvas in canvases)
            {
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = camera;
                canvas.planeDistance = 1f;
            }
            Canvas.ForceUpdateCanvases();
            var request = new UniversalRenderPipeline.SingleCameraRequest { destination = target };
            RenderPipeline.SubmitRenderRequest(camera, request);
            RenderTexture.active = target;
            pixels.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0);
            pixels.Apply();
            File.WriteAllBytes(path, pixels.EncodeToPNG());
            if (allVariants)
            {
                // 同じ実カメラの描画結果を4枠に並べる。比較画像のためにシーンは加工しない。
                if (overview == null) overview = new Texture2D(3840, 2160, TextureFormat.RGB24, false);
                overview.SetPixels((themeIndex % 2) * 1920, (1 - themeIndex / 2) * 1080, 1920, 1080, pixels.GetPixels());
                if (themeIndex == StageThemeCatalog.Count - 1)
                {
                    overview.Apply();
                    File.WriteAllBytes(Path.Combine(output, "stage-overview.png"), overview.EncodeToPNG());
                    UnityEngine.Object.DestroyImmediate(overview);
                    overview = null;
                }
            }
        }
        finally
        {
            foreach (var canvas in canvases) canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            camera.targetTexture = oldTarget;
            RenderTexture.active = oldActive;
            target.Release();
            UnityEngine.Object.DestroyImmediate(target);
            UnityEngine.Object.DestroyImmediate(pixels);
        }
    }
}
