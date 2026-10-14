using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// 実シーンの秘密の入口・タイル演出・E譜面の開始を連続して撮影する。
[InitializeOnLoad]
public static class RushESecretPreview
{
    const string Key = "RushESecretPreview.Running";
    static int step;
    static double start, ready;
    static SongSelectController ctl;
    static SongSelectSkin skin;
    static RushESecretPreview()
    {
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += value =>
        {
            if (SessionState.GetBool(Key, false) && value == PlayModeStateChange.EnteredPlayMode)
            { start = EditorApplication.timeSinceStartup; step = 0; ready = 0; }
        };
    }
    public static void Render()
    {
        if (!Application.isBatchMode) throw new InvalidOperationException("専用バッチのみ");
        var args = Environment.GetCommandLineArgs(); int at = Array.IndexOf(args, "-rushEPreviewOutput");
        if (at < 0 || at + 1 >= args.Length) throw new ArgumentException("出力先が必要です");
        Directory.CreateDirectory(args[at + 1]); SessionState.SetString(Key + ".Output", Path.GetFullPath(args[at + 1]));
        SessionState.SetBool(Key + ".Projector", DisplaySettings.ProjectorMode); SessionState.SetBool(Key, true);
        EditorSceneManager.OpenScene("Assets/Scenes/SongSelect.unity"); EditorApplication.isPlaying = true;
    }
    static void Tick()
    {
        if (!SessionState.GetBool(Key, false) || !EditorApplication.isPlaying || start <= 0) return;
        try
        {
            if (EditorApplication.timeSinceStartup - start > 90) throw new TimeoutException("秘密の表示検証が完了しません");
            if (step == 0)
            {
                ctl = UnityEngine.Object.FindFirstObjectByType<SongSelectController>(); skin = UnityEngine.Object.FindFirstObjectByType<SongSelectSkin>();
                if (ctl == null || skin?.IsReady != true) return;
                DisplaySettings.ProjectorMode = false; ProjectorMode.Apply(Camera.main);
                UnityEngine.Object.FindFirstObjectByType<SongSelectAimPointer>().enabled = false;
                ctl.previewSource.mute = true;
                ctl.Select(Enumerable.Range(0, ctl.SongCount).First(i => ctl.SongIdAt(i) == "RushE")); ctl.SetDifficulty(2);
                ready = EditorApplication.timeSinceStartup; step++; return;
            }
            double elapsed = EditorApplication.timeSinceStartup - ready;
            if (step == 1 && elapsed > 1)
            {
                Capture("01-normal");
                for (int i = 0; i < 10; i++) if (!ctl.TryRushEHit(i * .2)) throw new InvalidOperationException("斬撃回数が進みません");
                ready = EditorApplication.timeSinceStartup; step++; return;
            }
            if (step == 2 && elapsed > .32)
            {
                var curtain = UnityEngine.Object.FindFirstObjectByType<RushETransitionGraphic>();
                Canvas.ForceUpdateCanvases();
                if (curtain?.canvasRenderer == null || curtain.canvasRenderer.GetMesh().vertexCount == 0) throw new InvalidOperationException("タイルの描画メッシュがありません");
                Capture("02-tiles"); step++; return;
            }
            if (step == 3 && elapsed > 2.2)
            {
                if (!ctl.RushESecret.Unlocked || ctl.RushESecret.Transitioning || ctl.CurrentDifficultyName != "E" || !skin.Background.SecretTheme) throw new InvalidOperationException("Eに切り替わりません");
                if (ctl.difficultyButtons.Any(b => b.gameObject.activeSelf)) throw new InvalidOperationException("通常難易度が残っています");
                Capture("03-secret-e");
                int rush = ctl.SelectedIndex; ctl.Select((rush + 1) % ctl.SongCount); ctl.Select(rush);
                if (ctl.RushESecret.Unlocked || ctl.RushESecret.Hits != 0 || skin.Background.SecretTheme) throw new InvalidOperationException("曲変更で復元しません");
                ready = EditorApplication.timeSinceStartup; step++; return;
            }
            if (step == 4 && elapsed > .8)
            {
                Capture("04-reset");
                for (int i = 0; i < 10; i++) ctl.TryRushEHit(i * .2);
                ready = EditorApplication.timeSinceStartup; step++; return;
            }
            if (step == 5 && elapsed > 2.2)
            {
                if (!ctl.startButton.GetComponent<SongSelectDiscTarget>().TryShoot()) throw new InvalidOperationException("Eを開始できません");
                ready = EditorApplication.timeSinceStartup; step++; return;
            }
            if (step == 6 && elapsed > 7)
            {
                var manager = UnityEngine.Object.FindFirstObjectByType<GamePlayManager>();
                if (SceneManager.GetActiveScene().name != "Game" || GameSession.SelectedDifficulty != "E" || manager?.songPlayer.Clip == null) throw new InvalidOperationException("E譜面でゲームが開始しません");
                if (manager.noteSpawner.TotalNoteCount != ChartLoader.LoadFromStreamingAssets("RushE", "E").notes.Count) throw new InvalidOperationException("Eのノーツが欠けています");
                Capture("05-game-e"); File.WriteAllText(Path.Combine(Output, "checks.txt"), "Ten cuts -> tiles -> purple E only -> reset on song change -> E game starts: PASS\n"); Finish(0);
            }
        }
        catch (Exception e) { Debug.LogException(e); Finish(1); }
    }
    static string Output => SessionState.GetString(Key + ".Output", "");
    static void Capture(string name)
    {
        var capture = typeof(SongSelectDesignPreview).GetMethod("Capture", BindingFlags.Static | BindingFlags.NonPublic);
        capture.Invoke(null, new object[] { Path.Combine(Output, name + ".png"), 1920, 1080 });
    }
    static void Finish(int code)
    {
        DisplaySettings.ProjectorMode = SessionState.GetBool(Key + ".Projector", false); SessionState.SetBool(Key, false);
        Debug.Log("[RushESecretPreview] " + (code == 0 ? "PASS" : "FAIL")); EditorApplication.Exit(code);
    }
}
