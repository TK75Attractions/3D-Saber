using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

// 専用バッチの実シーンを撮る。演出の時計を偽装せず、フレームの実時刻を動画へ渡す。
[InitializeOnLoad]
public static class HardIntroPreview
{
    const string Key = "HardIntroPreview.Active";
    const int Width = 1280, Height = 720, SampleRate = 48000;
    static string output;
    static int phase;
    static double started, origin, lastFrame = -1, songStart = -1;
    static RenderTexture target;
    static Texture2D pixels;
    static SongSelectController selection;
    static AudioClip songClip;
    static float songVolume;
    static readonly List<double> times = new List<double>();
    static readonly List<Tuple<AudioClip, double, float>> cues = new List<Tuple<AudioClip, double, float>>();
    static readonly HashSet<string> stills = new HashSet<string>();
    static HardIntroPreview()
    {
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += state =>
        {
            if (!SessionState.GetBool(Key, false) || state != PlayModeStateChange.EnteredPlayMode) return;
            output = SessionState.GetString(Key + ".Output", "");
            started = EditorApplication.timeSinceStartup; phase = 0; origin = 0; lastFrame = songStart = -1;
            times.Clear(); cues.Clear(); stills.Clear();
        };
    }
    public static void Render()
    {
        if (!Application.isBatchMode) throw new InvalidOperationException("検証コピーの専用バッチのみ");
        var args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, "-hardIntroOutput");
        if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("撮影出力先が必要です");
        output = Path.GetFullPath(args[index + 1]); Directory.CreateDirectory(Path.Combine(output, "Frames"));
        SessionState.SetString(Key + ".Output", output); SessionState.SetBool(Key, true);
        EditorSceneManager.OpenScene("Assets/Scenes/SongSelect.unity", OpenSceneMode.Single);
        EditorApplication.isPlaying = true;
    }
    static void Tick()
    {
        if (!SessionState.GetBool(Key, false) || !EditorApplication.isPlaying || started <= 0) return;
        try
        {
            if (EditorApplication.timeSinceStartup - started > 240) throw new TimeoutException("HARD演出の撮影がタイムアウトしました");
            if (phase == 0)
            {
                selection = Object.FindFirstObjectByType<SongSelectController>();
                if (selection == null || selection.SongCount == 0 || Object.FindFirstObjectByType<SongSelectSkin>()?.IsReady != true) return;
                DisplaySettings.SetReducedEffectsForTest(false); DisplaySettings.SetProjectorModeForTest(false); ProjectorMode.Apply(Camera.main);
                for (int i = 0; i < selection.SongCount; i++) if (selection.SongIdAt(i) == "Epilogue") { selection.Select(i); break; }
                selection.SetDifficulty(2); selection.StopPreview();
                foreach (var p in Object.FindObjectsByType<SongSelectAimPointer>(FindObjectsSortMode.None)) p.enabled = false;
                target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32); target.Create();
                pixels = new Texture2D(Width, Height, TextureFormat.RGB24, false);
                phase = 1; return;
            }
            if (phase == 1)
            {
                selection.StartGame();
                if (!ScreenTransition.IsHardIntro) throw new InvalidOperationException("実選曲画面からHARD演出を開始できません");
                origin = AudioSettings.dspTime + .05; phase = 2;
                Capture(0); return;
            }
            double elapsed = Math.Max(0, AudioSettings.dspTime - origin);
            var manager = Object.FindFirstObjectByType<GamePlayManager>();
            if (manager != null && ScreenTransition.IsBusy && manager.songPlayer.IsScheduled)
                throw new InvalidOperationException("演出終了より先に楽曲が始まりました");
            var countdown = Object.FindFirstObjectByType<GameStartCountdown>();
            if (countdown != null && cues.Count == 0 && countdown.IsRunning)
            {
                double beat = (double)typeof(GameStartCountdown).GetField("beatSeconds", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(countdown);
                var sources = countdown.GetComponents<AudioSource>();
                for (int i = 0; i < sources.Length; i++) cues.Add(Tuple.Create(sources[i].clip, countdown.SongStartDspTime - origin - (3 - i) * beat, sources[i].volume));
            }
            if (manager != null && manager.songPlayer.IsScheduled)
            {
                songClip = manager.songPlayer.Clip; songVolume = manager.songPlayer.GetComponent<AudioSource>().volume;
                songStart = AudioSettings.dspTime - origin - manager.songPlayer.SongTime;
            }
            if (elapsed - lastFrame >= 1.0 / 24) Capture(elapsed);
            if (!ScreenTransition.IsBusy && manager != null && manager.songPlayer.IsPlaying && manager.songPlayer.SongTime > 1.5)
            { Finish(elapsed); return; }
        }
        catch (Exception error)
        {
            SessionState.SetBool(Key, false); Debug.LogException(error); EditorApplication.Exit(1);
        }
    }
    static void Capture(double elapsed)
    {
        var camera = Camera.main; if (camera == null) return;
        var canvases = Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None).Where(c => c.isRootCanvas && c.renderMode == RenderMode.ScreenSpaceOverlay).ToArray();
        var previousCameras = canvases.Select(c => c.worldCamera).ToArray(); var previousDistances = canvases.Select(c => c.planeDistance).ToArray();
        var oldTarget = camera.targetTexture; float oldAspect = camera.aspect; var oldActive = RenderTexture.active;
        try
        {
            camera.targetTexture = target; camera.aspect = Width / (float)Height;
            foreach (var canvas in canvases) { canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = camera; canvas.planeDistance = camera.nearClipPlane + .1f; }
            Canvas.ForceUpdateCanvases();
            var view = Object.FindFirstObjectByType<HardSongIntro>(); if (view != null) view.SetTime(view.TimeInIntro);
            Canvas.ForceUpdateCanvases();
            RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
            RenderTexture.active = target; pixels.ReadPixels(new Rect(0, 0, Width, Height), 0, 0); pixels.Apply();
            File.WriteAllBytes(Path.Combine(output, "Frames", $"frame-{times.Count:D5}.jpg"), pixels.EncodeToJPG(92));
            times.Add(elapsed); lastFrame = elapsed;
            foreach (var checkpoint in new[] { Tuple.Create("noise", 3.8), Tuple.Create("courtyard", 6.4), Tuple.Create("field", 11.5), Tuple.Create("gate", 15.5), Tuple.Create("name", 18.9), Tuple.Create("title", 22.3), Tuple.Create("game", 25.0) })
                if (elapsed >= checkpoint.Item2 && stills.Add(checkpoint.Item1)) File.WriteAllBytes(Path.Combine(output, checkpoint.Item1 + ".png"), pixels.EncodeToPNG());
        }
        finally
        {
            for (int i = 0; i < canvases.Length; i++) { canvases[i].renderMode = RenderMode.ScreenSpaceOverlay; canvases[i].worldCamera = previousCameras[i]; canvases[i].planeDistance = previousDistances[i]; }
            camera.targetTexture = oldTarget; camera.aspect = oldAspect; RenderTexture.active = oldActive;
        }
    }
    static void Finish(double duration)
    {
        var concat = new StringBuilder("ffconcat version 1.0\n");
        for (int i = 0; i < times.Count; i++)
        {
            concat.AppendLine($"file 'Frames/frame-{i:D5}.jpg'");
            concat.AppendLine("duration " + ((i + 1 < times.Count ? times[i + 1] : duration + 1.0 / 24) - times[i]).ToString("F6", CultureInfo.InvariantCulture));
        }
        concat.AppendLine($"file 'Frames/frame-{times.Count - 1:D5}.jpg'");
        File.WriteAllText(Path.Combine(output, "frames.ffconcat"), concat.ToString(), new UTF8Encoding(false));
        var mix = new float[(int)Math.Ceiling((duration + .1) * SampleRate)];
        Mix(mix, Resources.Load<AudioClip>("UI/HardIntro/MorseSignal"), 0, 1);
        foreach (var cue in cues) Mix(mix, cue.Item1, cue.Item2, cue.Item3);
        if (songClip != null && songStart >= 0) Mix(mix, songClip, songStart, songVolume);
        using (var writer = new BinaryWriter(File.Create(Path.Combine(output, "preview-audio.wav"))))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + mix.Length * 2); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(SampleRate); writer.Write(SampleRate * 2);
            writer.Write((short)2); writer.Write((short)16); writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(mix.Length * 2);
            foreach (float value in mix) writer.Write((short)Mathf.RoundToInt(Mathf.Clamp(value, -1, 1) * 32767));
        }
        File.WriteAllText(Path.Combine(output, "capture.json"), "{\"source\":\"Unity real StartGame flow\",\"frames\":" + times.Count + ",\"duration\":" + duration.ToString("F4", CultureInfo.InvariantCulture) + ",\"songStart\":" + songStart.ToString("F4", CultureInfo.InvariantCulture) + ",\"countdownCues\":" + cues.Count + "}");
        DisplaySettings.ResetReducedEffectsCacheForTest(); DisplaySettings.ResetProjectorModeCacheForTest();
        SessionState.SetBool(Key, false); Debug.Log("[HardIntroPreview] PASS: " + output); EditorApplication.Exit(0);
    }
    static void Mix(float[] mix, AudioClip clip, double start, float volume)
    {
        if (clip == null) return;
        int offset = (int)Math.Round(start * SampleRate), frames = Math.Min(clip.samples, (int)Math.Ceiling((mix.Length - offset) / (double)SampleRate * clip.frequency));
        if (frames <= 0) return;
        var samples = new float[frames * clip.channels];
        if (!clip.GetData(samples, 0)) throw new InvalidOperationException("撮影音声を取り出せません: " + clip.name);
        for (int i = Math.Max(0, offset); i < mix.Length; i++)
        {
            int at = (int)((i - offset) * (double)clip.frequency / SampleRate); if (at >= frames) break;
            float value = 0; for (int ch = 0; ch < clip.channels; ch++) value += samples[at * clip.channels + ch];
            mix[i] += value / clip.channels * volume;
        }
    }
}
