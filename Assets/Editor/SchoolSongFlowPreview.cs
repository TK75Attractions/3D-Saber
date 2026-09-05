using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// 専用バッチだけで動く実シーン/実譜面の自動カット確認。シーン資産は保存しない。
[InitializeOnLoad]
public static class SchoolSongFlowPreview
{
    private const string Key = "SchoolSongFlowPreview.Running";
    private static readonly double[] Starts = { 40.5, 82, 110.5, 149 };
    private const int Fps = 20, FramesPerShot = 160, Width = 1280, Height = 720;
    private static int phase, shot, frame, settle;
    private static double started, lastCutAt = -100;
    private static string output;
    private static NoteSpawner spawner;
    private static GamePlayManager manager;
    private static PreviewChart full;
    private static RenderTexture target;
    private static Texture2D pixels;
    private static readonly List<Live> live = new List<Live>();
    private static readonly FieldInfo Clock = typeof(SongPlayer).GetField("startDspTime", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo Scheduled = typeof(SongPlayer).GetField("scheduled", BindingFlags.NonPublic | BindingFlags.Instance);

    [Serializable] private class PreviewChart
    {
        public float bpm, coordScale, offsetMs;
        public int displayLevel;
        public List<PreviewNote> notes;
    }
    [Serializable] private class PreviewNote
    {
        public float beat, time, x, y, lengthMs;
        public string type, color, direction;
        public int count;
    }
    private class Live { public CuttableNote note; public PreviewNote data; public int cuts; }

    static SchoolSongFlowPreview()
    {
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += state =>
        {
            if (SessionState.GetBool(Key, false) && state == PlayModeStateChange.EnteredPlayMode)
            {
                started = EditorApplication.timeSinceStartup;
                phase = shot = frame = settle = 0;
                output = SessionState.GetString(Key + ".Output", "");
            }
        };
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void SelectSong()
    {
        if (!SessionState.GetBool(Key, false)) return;
        GameSession.SelectedSongId = "Epilogue";
        GameSession.SelectedDifficulty = "hard";
        GameSession.IsCalibrationMode = false;
    }

    public static void Render()
    {
        if (!Application.isBatchMode) throw new InvalidOperationException("専用バッチのみ");
        var args = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(args, "-schoolFlowOutput");
        if (at < 0 || at + 1 >= args.Length) throw new ArgumentException("出力先が必要です");
        output = Path.GetFullPath(args[at + 1]);
        Directory.CreateDirectory(Path.Combine(output, "Frames"));
        SessionState.SetString(Key + ".Output", output);
        SessionState.SetBool(Key, true);
        EditorSceneManager.OpenScene("Assets/Scenes/Game.unity", OpenSceneMode.Single);
        SelectSong();
        EditorApplication.isPlaying = true;
    }

    private static void Tick()
    {
        if (!SessionState.GetBool(Key, false) || !EditorApplication.isPlaying || started <= 0) return;
        try
        {
            if (EditorApplication.timeSinceStartup - started > 900) throw new TimeoutException("確認映像の描画がタイムアウト");
            if (phase == 0)
            {
                spawner = UnityEngine.Object.FindFirstObjectByType<NoteSpawner>();
                manager = UnityEngine.Object.FindFirstObjectByType<GamePlayManager>();
                if (spawner == null || spawner.TotalNoteCount == 0 || manager == null) return;
                manager.StopAllCoroutines(); manager.enabled = false;
                manager.songPlayer.Stop();
                foreach (var c in UnityEngine.Object.FindObjectsByType<GameStartCountdown>(FindObjectsSortMode.None)) c.gameObject.SetActive(false);
                foreach (var j in UnityEngine.Object.FindObjectsByType<SaberCutJudge>(FindObjectsSortMode.None)) j.autonomous = false;
                full = JsonUtility.FromJson<PreviewChart>(File.ReadAllText(Path.Combine(Application.streamingAssetsPath, "Songs/Epilogue/chart_hard.json")));
                spawner.approachTime = 1.65f;
                spawner.SetExtraOffsetSeconds(0);
                target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32); target.Create();
                pixels = new Texture2D(Width, Height, TextureFormat.RGB24, false);
                spawner.OnNoteSpawned += OnSpawn;
                StartShot(); phase = 1;
                return;
            }
            if (phase == 1)
            {
                if (++settle < 15) return;
                phase = 2;
            }
            if (phase == 2)
            {
                double now = Starts[shot] + frame / (double)Fps;
                SetClock(now);
                spawner.Tick(now);
                foreach (var item in live.ToArray())
                {
                    if (item.note == null || item.note.IsCut || item.note.IsMissed) { live.Remove(item); continue; }
                    double hit = (item.data.time + full.offsetMs) / 1000.0;
                    while (item.cuts < item.data.count)
                    {
                        double cutAt = hit + (item.data.count > 1 ? item.cuts * item.data.lengthMs / 1000.0 / (item.data.count - 1) : 0);
                        if (now + .00001 < cutAt) break;
                        SetClock(cutAt);
                        var direction = item.note.RequiredDirection;
                        Vector2 vector = direction == CutDirection.None ? (item.cuts % 2 == 0 ? Vector2.down : Vector2.up) : CutDirectionHelper.ToVector(direction);
                        item.note.Cut(item.note.transform.position, new Vector3(vector.x, vector.y, 0) * 4, CutDirection.None, item.note.RequiredHand);
                        item.cuts++;
                        if (manager.scoreManager.LastTier == JudgmentTier.Perfect) lastCutAt = now;
                        if (item.note == null || item.note.IsCut) break;
                    }
                }
                SetClock(now);
                var pulse = UnityEngine.Object.FindFirstObjectByType<GateBeatPulse>();
                // 自動映像では実時間でなく映像のフレーム時間を使って同じ減衰を可視化する。
                if (pulse != null)
                {
                    typeof(GateBeatPulse).GetField("lastPerfect", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(pulse, lastCutAt);
                    pulse.Tick(now);
                }
                if (manager.barLineSpawner != null) manager.barLineSpawner.Tick(now);
                Capture(Path.Combine(output, "Frames", $"frame-{shot * FramesPerShot + frame:D5}.png"));
                frame++;
                if (frame < FramesPerShot) return;
                File.AppendAllText(Path.Combine(output, "runtime-check.txt"), $"Shot {shot}: audio {Starts[shot]:F3}s, {FramesPerShot} frames, actual chart {full.notes.Count} notes, scene not saved.\n");
                if (++shot < Starts.Length) { StartShot(); phase = 1; return; }
                File.WriteAllText(Path.Combine(output, "clips.json"), "{\"fps\":20,\"duration_each\":8,\"audio_starts\":[40.5,82,110.5,149],\"mode\":\"automatic cuts; not hardware playtest\"}");
                SessionState.SetBool(Key, false);
                Debug.Log("[SchoolSongFlowPreview] PASS: " + output);
                EditorApplication.Exit(0);
            }
        }
        catch (Exception exception)
        {
            SessionState.SetBool(Key, false);
            Debug.LogException(exception); EditorApplication.Exit(1);
        }
    }

    private static void OnSpawn(CuttableNote note)
    {
        var data = full.notes.First(n => Math.Abs((n.time + full.offsetMs) / 1000.0 - note.HitTime) < .001 &&
            Mathf.Abs(n.x - note.transform.position.x) < .001f && Mathf.Abs(n.y - note.transform.position.y) < .001f);
        live.Add(new Live { note = note, data = data });
    }

    private static void StartShot()
    {
        live.Clear(); frame = settle = 0; lastCutAt = -100;
        foreach (var debris in UnityEngine.Object.FindObjectsByType<SlicePieceDecay>(FindObjectsSortMode.None)) UnityEngine.Object.DestroyImmediate(debris.gameObject);
        var subset = new PreviewChart { bpm = full.bpm, coordScale = full.coordScale, offsetMs = full.offsetMs,
            displayLevel = full.displayLevel, notes = full.notes.Where(n => (n.time + full.offsetMs) / 1000.0 >= Starts[shot]).ToList() };
        var chartType = typeof(NoteSpawner).Assembly.GetType("ChartData", true);
        typeof(NoteSpawner).GetMethod("SetChart").Invoke(spawner, new[] { JsonUtility.FromJson(JsonUtility.ToJson(subset), chartType) });
        manager.scoreManager.Reset();
        var old = UnityEngine.Object.FindFirstObjectByType<FloorRenderer>();
        var parent = old != null ? old.transform.parent : null;
        if (old != null) UnityEngine.Object.DestroyImmediate(old.gameObject);
        var stage = new GameObject("FloorRenderer").AddComponent<FloorRenderer>();
        stage.transform.SetParent(parent, false); stage.randomizeOnPlay = false; stage.Build((StageTheme)shot);
        spawner.Tick(Starts[shot]);
    }

    private static void SetClock(double time)
    {
        Scheduled.SetValue(manager.songPlayer, true);
        Clock.SetValue(manager.songPlayer, AudioSettings.dspTime - time);
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
