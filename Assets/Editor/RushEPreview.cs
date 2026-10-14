using System;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// 文化祭用のローカル音源を入れた端末で、曲名・出典・3難易度を実シーンで確認する。
[InitializeOnLoad]
public static class RushEPreview
{
    const string Key = "RushEPreview.Running";
    static double started, ready;
    static int state;
    static SongSelectController controller;
    static RushEPreview()
    {
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += change =>
        {
            if (SessionState.GetBool(Key, false) && change == PlayModeStateChange.EnteredPlayMode)
            { started = EditorApplication.timeSinceStartup; ready = 0; state = 0; }
        };
    }
    public static void Render()
    {
        if (!Application.isBatchMode) throw new InvalidOperationException("専用バッチのみ");
        string[] args = Environment.GetCommandLineArgs(); int at = Array.IndexOf(args, "-rushEPreviewOutput");
        if (at < 0 || at + 1 >= args.Length) throw new ArgumentException("出力先が必要です");
        string output = Path.GetFullPath(args[at + 1]); Directory.CreateDirectory(output);
        SessionState.SetString(Key + ".Output", output);
        SessionState.SetBool(Key + ".Projector", DisplaySettings.ProjectorMode);
        SessionState.SetBool(Key, true);
        EditorSceneManager.OpenScene("Assets/Scenes/SongSelect.unity", OpenSceneMode.Single);
        EditorApplication.isPlaying = true;
    }
    static void Tick()
    {
        if (!SessionState.GetBool(Key, false) || !EditorApplication.isPlaying || started <= 0) return;
        try
        {
            if (EditorApplication.timeSinceStartup - started > 90) throw new TimeoutException("Rush Eの選曲を準備できません");
            if (ready == 0)
            {
                controller = UnityEngine.Object.FindFirstObjectByType<SongSelectController>();
                if (controller == null || UnityEngine.Object.FindFirstObjectByType<SongSelectSkin>()?.IsReady != true) return;
                int index = Enumerable.Range(0, controller.SongCount).Where(i => controller.SongIdAt(i) == "RushE").DefaultIfEmpty(-1).First();
                if (index < 0) throw new InvalidOperationException("音源と譜面が揃ったRush Eが選曲にありません");
                DisplaySettings.ProjectorMode = false; ProjectorMode.Apply(Camera.main);
                controller.previewSource.mute = true;
                var pointer = UnityEngine.Object.FindFirstObjectByType<SongSelectAimPointer>(); if (pointer != null) pointer.enabled = false;
                foreach (var judge in UnityEngine.Object.FindObjectsByType<SaberCutJudge>(FindObjectsSortMode.None)) judge.autonomous = false;
                controller.Select(index); Choose(); return;
            }
            if (EditorApplication.timeSinceStartup - ready < 1.8) return;
            string output = SessionState.GetString(Key + ".Output", "");
            string label = new[] { "easy", "normal", "hard" }[state];
            var capture = typeof(SongSelectDesignPreview).GetMethod("Capture", BindingFlags.Static | BindingFlags.NonPublic);
            if (capture == null) throw new MissingMethodException("SongSelectDesignPreview.Capture");
            capture.Invoke(null, new object[] { Path.Combine(output, "rush-e-" + label + ".png"), 1920, 1080 });
            capture.Invoke(null, new object[] { Path.Combine(output, "rush-e-" + label + "-720p.png"), 1280, 720 });
            var credits = GameObject.Find("SongSourceCredit").GetComponent<TextMeshProUGUI>(); credits.ForceMeshUpdate();
            if (credits.richText || credits.raycastTarget || credits.isTextOverflowing
                || !credits.text.Contains("Sheet Music Boss") || !credits.text.Contains("Qskm9MTz2V4"))
                throw new InvalidOperationException("作者・元動画の表示が欠けています");
            if (!controller.startButton.interactable) throw new InvalidOperationException("開始ボタンが無効です");
            File.AppendAllText(Path.Combine(output, "checks.txt"), label + ": level=" + controller.CurrentDifficultyDisplayLevel() + "; credits visible; start enabled\n");
            if (++state < 3) { Choose(); return; }
            // 出典が無い曲へ切り替えたとき、前の曲のクレジットを残さない。
            int next = Enumerable.Range(0, controller.SongCount).First(i => controller.SongIdAt(i) != "RushE");
            controller.Select(next);
            if (!string.IsNullOrEmpty(credits.text)) throw new InvalidOperationException("別の曲にRush Eのクレジットが残りました");
            Finish(0);
        }
        catch (Exception error) { Debug.LogException(error); Finish(1); }
    }
    static void Choose() { controller.SetDifficulty(state); ready = EditorApplication.timeSinceStartup; }
    static void Finish(int exit)
    {
        DisplaySettings.ProjectorMode = SessionState.GetBool(Key + ".Projector", false);
        SessionState.SetBool(Key, false);
        Debug.Log("[RushEPreview] " + (exit == 0 ? "PASS" : "FAIL"));
        EditorApplication.Exit(exit);
    }
}
