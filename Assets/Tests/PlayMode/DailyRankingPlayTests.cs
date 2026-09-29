using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

public class DailyRankingPlayTests
{
    readonly Dictionary<FieldInfo, object> saved = new Dictionary<FieldInfo, object>();
    string song, output;
    bool oldLow;

    [SetUp] public void Setup()
    {
        foreach (var field in typeof(GameSession).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (!field.IsLiteral && !field.IsInitOnly) saved[field] = field.GetValue(null);
        song = "__DailyPlay_" + Guid.NewGuid().ToString("N");
        GameSession.SelectedSongId = song;
        GameSession.SelectedSongTitle = "Daily rank test";
        GameSession.SelectedDifficulty = "Normal";
        GameSession.IsCalibrationMode = false;
        GameSession.ResetResult();
        oldLow = DisplaySettings.ReducedEffects;
        DisplaySettings.SetReducedEffectsForTest(false);
        var args = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(args, "-dailyRankOutput");
        output = at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
        if (output != null) Directory.CreateDirectory(output);
        SceneManager.SetActiveScene(SceneManager.CreateScene(song));
    }

    [UnityTearDown] public IEnumerator Cleanup()
    {
        Time.timeScale = 1;
        var scene = SceneManager.GetActiveScene();
        SceneManager.SetActiveScene(SceneManager.CreateScene(song + "_cleanup"));
        yield return SceneManager.UnloadSceneAsync(scene);
        HighScoreStore.Clear(song, "Normal");
        PlayerPrefs.DeleteKey(DailyRankingStore.Key(song, "Normal"));
        PlayerPrefs.DeleteKey(SongAchievementStore.Key(song, "Normal"));
        PlayerPrefs.Save();
        DisplaySettings.SetReducedEffectsForTest(oldLow);
        foreach (var entry in saved) entry.Key.SetValue(null, entry.Value);
        saved.Clear();
    }

    [UnityTest] public IEnumerator FinishedGameRanksFinalBonusScoreAndResultReloadDoesNotRecordAgain()
    {
        DailyRankingStore.Record(song, "Normal", "earlier", 1000, DateTime.Now);
        var root = new GameObject("FinishingGame");
        var score = root.AddComponent<ScoreManager>();
        for (int i = 0; i < 3; i++) score.RegisterHit(JudgmentTier.Perfect);
        score.RegisterMiss();
        var manager = root.AddComponent<GamePlayManager>();
        manager.enabled = false;
        manager.scoreManager = score;
        manager.songPlayer = root.AddComponent<SongPlayer>();
        manager.noteSpawner = root.AddComponent<NoteSpawner>();
        typeof(GamePlayManager).GetMethod("FinishGame", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(manager, null);
        float deadline = Time.realtimeSinceStartup + 15;
        while (SceneManager.GetActiveScene().name != "Result" && Time.realtimeSinceStartup < deadline) yield return null;
        yield return null;
        Assert.AreEqual("Result", SceneManager.GetActiveScene().name);
        Assert.AreEqual(1200, GameSession.FinalScore, "コンボ精算後の得点をランキングへ登録");
        Assert.AreEqual(1, GameSession.FinalDailyRanking.Rank);
        Assert.AreEqual(2, GameSession.FinalDailyRanking.TotalPlays);
        var snapshot = GameSession.FinalDailyRanking;
        GameSession.RecordCompletedDailyRanking();
        yield return SceneManager.LoadSceneAsync("Result");
        yield return null;
        Assert.AreSame(snapshot, GameSession.FinalDailyRanking);
        Assert.True(DailyRankingStore.TryRead(PlayerPrefs.GetString(DailyRankingStore.Key(song, "Normal")), snapshot.Day, out var table));
        Assert.AreEqual(2, table.entries.Count);
        Assert.NotNull(GameObject.Find("DailyRanking"));
    }

    [UnityTest] public IEnumerator CalibrationEmptyResultAndScenePreviewDoNotRegisterDailyScores()
    {
        GameSession.RecordCompletedDailyRanking();
        Assert.IsNull(GameSession.FinalDailyRanking);
        GameSession.FinalPerfect = 10;
        GameSession.IsCalibrationMode = true;
        GameSession.RecordCompletedDailyRanking();
        Assert.IsNull(GameSession.FinalDailyRanking);
        GameSession.IsCalibrationMode = false;
        yield return SceneManager.LoadSceneAsync("Result");
        yield return null;
        Assert.False(PlayerPrefs.HasKey(DailyRankingStore.Key(song, "Normal")));
        Assert.AreEqual("プレイ終了後に表示", GameObject.Find("DailyRanking").transform.Find("Details/Status").GetComponent<TMP_Text>().text);
    }

    [UnityTest] public IEnumerator RevealSkipAndLowModeKeepRankReadableAndBackUsable()
    {
        Complete(18000);
        yield return SceneManager.LoadSceneAsync("Result");
        yield return null;
        var reveal = Object.FindFirstObjectByType<ResultReveal>();
        reveal.enabled = false;
        var panel = GameObject.Find("DailyRanking");
        var rank = panel.transform.Find("Rank");
        var back = Object.FindFirstObjectByType<ResultController>().GetComponent<Canvas>().GetComponentInChildren<Button>();
        reveal.Tick(0);
        Assert.AreEqual(0, panel.GetComponent<CanvasGroup>().alpha);
        Assert.False(back.IsInteractable());
        Time.timeScale = 0;
        reveal.Tick(2.9f);
        Assert.AreEqual(0, rank.GetComponent<CanvasGroup>().alpha, "回転中に確定順位を先に見せない");
        var wheel = GameObject.Find("DailyRankingWheel");
        Assert.Greater(wheel.GetComponent<CanvasGroup>().alpha, 0);
        Assert.False(back.IsInteractable());
        reveal.Tick(DailyRankingWheel.StopTime + .05f);
        Assert.Greater(wheel.transform.Find("Frame/Stamp").localScale.x, 1);
        DisplaySettings.SetReducedEffectsForTest(true);
        reveal.Tick(2.9f);
        Assert.AreEqual(0, wheel.GetComponent<CanvasGroup>().alpha);
        Assert.AreEqual(Vector3.one, rank.localScale);
        reveal.Tick(999);
        Assert.AreEqual(1, rank.GetComponent<CanvasGroup>().alpha);
        Assert.AreEqual(Vector3.one, rank.localScale);
        Assert.True(back.IsInteractable());
        Assert.False(panel.GetComponent<CanvasGroup>().blocksRaycasts);
        Assert.IsNull(panel.transform.Find("Scope"));
        Assert.AreEqual("1 人中", panel.transform.Find("Count").GetComponent<TMP_Text>().text);
        Assert.AreEqual(0, wheel.GetComponent<CanvasGroup>().alpha);
        Assert.AreEqual(0, panel.transform.Find("Sweep").GetComponent<Image>().color.a);
        Assert.AreEqual(0, panel.transform.Find("LandingGlow").GetComponent<Image>().color.a);
        GameSession.ResetResult();
        Assert.IsNull(GameSession.FinalDailyRanking);
    }

    [UnityTest] public IEnumerator PodiumNormalTiesAndLongNumbersFitTheActualResultScene()
    {
        Complete(84000);
        yield return SceneManager.LoadSceneAsync("Result");
        yield return null;
        var title = GameObject.Find("TitleRow").transform.Find("TitleEn").GetComponent<TMP_Text>();
        title.text = "DAILY RANKING";
        var reveal = Object.FindFirstObjectByType<ResultReveal>();
        reveal.enabled = false;
        foreach (int rank in new[] { 1, 2, 3, 8, 10001 })
        {
            var old = GameObject.Find("DailyRanking");
            if (old != null) Object.Destroy(old);
            yield return null;
            var table = new DailyRankingStore.Table { day = "2026-09-30" };
            for (int i = 1; i < rank; i++) table.entries.Add(new DailyRankingStore.Entry { runId = "r" + i, score = 90000 });
            table.entries.Add(new DailyRankingStore.Entry { runId = "lower", score = 80000 });
            if (rank > 1) table.entries.Add(new DailyRankingStore.Entry { runId = "tie", score = 84000 });
            while (rank <= 3 && table.entries.Count < 35)
                table.entries.Add(new DailyRankingStore.Entry { runId = "lower" + table.entries.Count, score = 70000 });
            table.entries.Add(new DailyRankingStore.Entry { runId = "current", score = 84000 });
            var view = DailyRankingPresentation.Build(GameObject.Find("ResultStats").transform, reveal, DailyRankingStore.Evaluate(table, "current"));
            reveal.Tick(999);
            var wheel = Object.FindFirstObjectByType<DailyRankingWheel>();
            Assert.AreEqual(rank <= 3, wheel != null, "上位3位だけホイールを作る");
            Assert.IsNull(view.transform.Find("Scope"));
            Assert.True(view.transform.Find("Count").GetComponent<TMP_Text>().text.EndsWith(" 人中"));
            Canvas.ForceUpdateCanvases();
            foreach (var text in view.GetComponentsInChildren<TMP_Text>())
            {
                text.ForceMeshUpdate();
                Assert.False(text.isTextOverflowing, text.name + " must fit");
                Assert.LessOrEqual(text.preferredWidth, text.rectTransform.rect.width + 2, text.name + " width");
            }
            Capture("rank-" + rank + ".png", 1920, 1080);
            if (rank <= 3)
            {
                reveal.Tick(DailyRankingWheel.StopTime - .03f);
                Assert.AreEqual(rank.ToString(), wheel.transform.Find("Frame/WheelMask/Reel/Cell2").GetComponent<TMP_Text>().text);
                reveal.Tick(DailyRankingWheel.ImpactTime);
                Assert.AreEqual(rank + "<size=64> 位</size>", wheel.transform.Find("Frame/Stamp/Value").GetComponent<TMP_Text>().text);
                Assert.AreEqual(1, wheel.transform.Find("Frame/Stamp").localScale.x, .001f);
                Capture("podium-" + rank + ".png", 1920, 1080);
                reveal.Tick(999);
                Assert.AreEqual(0, wheel.GetComponent<CanvasGroup>().alpha);
            }
            if (rank == 1)
            {
                Capture("rank-1-720.png", 1280, 720);
                for (int frame = 0; frame <= 132 && output != null; frame++)
                {
                    reveal.Tick(frame / 20f);
                    if (frame / 20f > DailyRankingWheel.StartTime && frame / 20f < DailyRankingWheel.StopTime)
                        foreach (var cell in wheel.transform.Find("Frame/WheelMask/Reel").GetComponentsInChildren<TMP_Text>())
                        {
                            int shown = int.Parse(cell.text.Replace(",", ""));
                            Assert.That(shown, Is.InRange(1, table.entries.Count));
                        }
                    Capture("frame-" + frame.ToString("D3") + ".png", 1280, 720);
                }
                ExportWheelAudio(wheel, table.entries.Count, rank);
                DisplaySettings.SetReducedEffectsForTest(true);
                reveal.Tick(3.12f);
                Capture("rank-1-low.png", 1280, 720);
                DisplaySettings.SetReducedEffectsForTest(false);
            }
        }
    }

    void Complete(int score)
    {
        GameSession.FinalScore = score;
        GameSession.FinalPerfect = 180;
        GameSession.FinalGreat = 35;
        GameSession.FinalGood = 7;
        GameSession.FinalBad = 1;
        GameSession.FinalMiss = 2;
        GameSession.FinalMaxCombo = 120;
        GameSession.RecordCompletedDailyRanking();
    }

    // プレビューも製品と同じ音源を使い、撮影した20fpsの演出時刻に合わせて混ぜる。
    void ExportWheelAudio(DailyRankingWheel wheel, int count, int rank)
    {
        if (output == null) return;
        const int rate = 22050;
        var mix = new float[rate * 8];
        var tick = (AudioClip)typeof(DailyRankingWheel).GetField("tickSound", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(wheel);
        var impact = (AudioClip)typeof(DailyRankingWheel).GetField("impactSound", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(wheel);
        int steps = (int)typeof(DailyRankingWheel).GetField("steps", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(wheel);
        int last = -1;
        for (int frame = 0; frame <= 132; frame++)
        {
            float time = frame / 20f;
            float p = Mathf.Clamp01((time - DailyRankingWheel.StartTime) / (DailyRankingWheel.StopTime - DailyRankingWheel.StartTime));
            int cell = Mathf.FloorToInt(steps * Mathf.Pow(1 - p, 3));
            if (time > DailyRankingWheel.StartTime && time < DailyRankingWheel.StopTime && cell != last) Mix(tick, time, .16f);
            last = cell;
        }
        Mix(impact, Mathf.Ceil(DailyRankingWheel.ImpactTime * 20) / 20, .65f);
        using (var writer = new BinaryWriter(File.Create(Path.Combine(output, "wheel-preview.wav"))))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + mix.Length * 2);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2);
            writer.Write((short)2); writer.Write((short)16); writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(mix.Length * 2);
            foreach (float sample in mix) writer.Write((short)(Mathf.Clamp(sample, -1, 1) * 32767));
        }
        void Mix(AudioClip clip, float time, float volume)
        {
            var samples = new float[clip.samples]; clip.GetData(samples, 0);
            int start = Mathf.RoundToInt(time * rate);
            for (int i = 0; i < samples.Length && start + i < mix.Length; i++) mix[start + i] += samples[i] * volume;
        }
    }

    void Capture(string filename, int width, int height)
    {
        if (output == null) return;
        var cameraRoot = new GameObject("DailyRankCapture");
        var camera = cameraRoot.AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black;
        camera.cullingMask = 1 << 5;
        camera.nearClipPlane = .1f; camera.farClipPlane = 100;
        cameraRoot.AddComponent<UniversalAdditionalCameraData>();
        var previous = new List<(Canvas canvas, RenderMode mode, Camera camera, float plane)>();
        var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
        var pixels = new Texture2D(width, height, TextureFormat.RGB24, false);
        var active = RenderTexture.active;
        try
        {
            target.Create(); camera.targetTexture = target; camera.aspect = width / (float)height;
            foreach (var canvas in Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None))
            {
                if (!canvas.isRootCanvas) continue;
                previous.Add((canvas, canvas.renderMode, canvas.worldCamera, canvas.planeDistance));
                canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = camera; canvas.planeDistance = 10;
                foreach (var item in canvas.GetComponentsInChildren<Transform>()) item.gameObject.layer = 5;
            }
            Canvas.ForceUpdateCanvases();
            RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
            RenderTexture.active = target;
            pixels.ReadPixels(new Rect(0, 0, width, height), 0, 0); pixels.Apply();
            File.WriteAllBytes(Path.Combine(output, filename), pixels.EncodeToPNG());
        }
        finally
        {
            foreach (var item in previous)
                if (item.canvas != null) { item.canvas.renderMode = item.mode; item.canvas.worldCamera = item.camera; item.canvas.planeDistance = item.plane; }
            RenderTexture.active = active;
            camera.targetTexture = null;
            Object.DestroyImmediate(pixels); target.Release(); Object.DestroyImmediate(target); Object.DestroyImmediate(cameraRoot);
        }
    }
}
