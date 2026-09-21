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

public class ComboBonusPlayTests
{
    readonly Dictionary<FieldInfo, object> savedSession = new Dictionary<FieldInfo, object>();
    string songId;
    string output;
    bool oldLow;

    [SetUp]
    public void SetUp()
    {
        foreach (var f in typeof(GameSession).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (!f.IsLiteral && !f.IsInitOnly) savedSession[f] = f.GetValue(null);
        songId = "__ComboBonus_" + Guid.NewGuid().ToString("N");
        GameSession.SelectedSongId = songId;
        GameSession.SelectedSongTitle = "COMBO BONUS TEST";
        GameSession.SelectedDifficulty = "Normal";
        GameSession.IsCalibrationMode = false;
        GameSession.ResetResult();
        oldLow = DisplaySettings.ReducedEffects;
        DisplaySettings.SetReducedEffectsForTest(false);
        SceneManager.SetActiveScene(SceneManager.CreateScene(songId));
        string[] args = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(args, "-comboBonusOutput");
        output = at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
        if (output != null) Directory.CreateDirectory(output);
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        Time.timeScale = 1;
        var scene = SceneManager.GetActiveScene();
        SceneManager.SetActiveScene(SceneManager.CreateScene(songId + "_Cleanup"));
        yield return SceneManager.UnloadSceneAsync(scene);
        HighScoreStore.Clear(songId, "Normal");
        DisplaySettings.SetReducedEffectsForTest(oldLow);
        foreach (var entry in savedSession) entry.Key.SetValue(null, entry.Value);
        savedSession.Clear();
    }

    [UnityTest]
    public IEnumerator FinishFlow_AwardsAtLandingThenSavesAndShowsResult()
    {
        var root = new GameObject("FinishingGame");
        var score = root.AddComponent<ScoreManager>();
        for (int i = 0; i < 120; i++) score.RegisterHit(JudgmentTier.Perfect);
        score.RegisterMiss();
        var manager = root.AddComponent<GamePlayManager>();
        manager.enabled = false;
        manager.scoreManager = score;
        manager.songPlayer = root.AddComponent<SongPlayer>();
        manager.noteSpawner = root.AddComponent<NoteSpawner>();
        var hud = GameHUDSkin.Ensure();
        yield return null;
        Assert.AreEqual("120", hud.transform.Find("MaxComboValue").GetComponent<TMP_Text>().text);
        Assert.AreEqual("", hud.transform.Find("ComboValue").GetComponent<TMP_Text>().text);
        Capture("hud-after-miss.png");
        typeof(GamePlayManager).GetMethod("FinishGame", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(manager, null);
        Assert.AreEqual(36000, score.Score);
        Assert.False(score.IsFinalized);
        float landingDeadline = Time.realtimeSinceStartup + 3;
        while (!score.IsFinalized && Time.realtimeSinceStartup < landingDeadline) yield return null;
        Assert.True(score.IsFinalized);
        Assert.AreEqual(48000, score.Score);
        float deadline = Time.realtimeSinceStartup + 10;
        while (SceneManager.GetActiveScene().name != "Result" && Time.realtimeSinceStartup < deadline) yield return null;
        Assert.AreEqual("Result", SceneManager.GetActiveScene().name);
        yield return null;
        Assert.AreEqual(48000, GameSession.FinalScore);
        Assert.AreEqual(120, GameSession.FinalMaxCombo);
        Assert.AreEqual(12000, GameSession.FinalComboBonus);
        var block = GameObject.Find("ResultStats").transform.Find("ScoreBlock");
        Assert.AreEqual("48,000", block.Find("ScoreValue").GetComponent<TMP_Text>().text);
        Assert.AreEqual("120", block.Find("MaxComboValue").GetComponent<TMP_Text>().text);
        Assert.AreEqual(48000, HighScoreStore.Load(songId, "Normal").entries[0].score);
        Assert.AreEqual(1, HighScoreStore.Load(songId, "Normal").entries.Count);
        var reveal = Object.FindFirstObjectByType<ResultReveal>();
        reveal.enabled = false;
        reveal.Tick(99);
        yield return new WaitForSecondsRealtime(.5f);
        Capture("result.png");
    }

    [UnityTest]
    public IEnumerator MaxComboLivesAboveCurrentComboAndSurvivesBreak()
    {
        var score = new GameObject("Score").AddComponent<ScoreManager>();
        for (int i = 0; i < 404; i++) score.RegisterHit(JudgmentTier.Perfect);
        var hud = GameHUDSkin.Ensure();
        yield return null;
        var max = hud.transform.Find("MaxComboValue").GetComponent<TMP_Text>();
        var current = hud.transform.Find("ComboValue").GetComponent<TMP_Text>();
        Assert.AreEqual("404", max.text);
        Assert.AreEqual("404", current.text);
        Assert.Greater(max.rectTransform.anchoredPosition.y, current.rectTransform.anchoredPosition.y);
        Capture("hud-404.png");
        AssertLabelsBelowDigits(hud, "MaxComboValue", "MaxComboLabel");
        AssertLabelsBelowDigits(hud, "ComboValue", "ComboLabel");
        score.RegisterHit(JudgmentTier.Bad);
        yield return null;
        Assert.AreEqual("404", max.text);
        Assert.AreEqual("", current.text);
        score.Reset();
        yield return null;
        Assert.AreEqual("0", max.text);
    }

    static void AssertLabelsBelowDigits(GameHUDSkin hud, string valueName, string labelName)
    {
        Canvas.ForceUpdateCanvases();
        var value = hud.transform.Find(valueName).GetComponent<TMP_Text>();
        var label = hud.transform.Find(labelName).GetComponent<TMP_Text>();
        value.ForceMeshUpdate(); label.ForceMeshUpdate();
        // フォントの行送り用余白ではなく、実際に描く字形の頂点で比較する。
        float bottom = float.PositiveInfinity, labelTop = float.NegativeInfinity;
        for (int i = 0; i < value.textInfo.characterCount; i++)
        {
            var character = value.textInfo.characterInfo[i];
            if (character.isVisible) bottom = Mathf.Min(bottom, value.transform.TransformPoint(character.bottomLeft).y);
        }
        for (int i = 0; i < label.textInfo.characterCount; i++)
        {
            var character = label.textInfo.characterInfo[i];
            if (character.isVisible) labelTop = Mathf.Max(labelTop, label.transform.TransformPoint(character.topRight).y);
        }
        Assert.Greater(bottom, labelTop, "増加時の拡大中もラベルに重ならない");
    }

    [UnityTest]
    public IEnumerator PresentationTiersAndReducedEffectsRenderWithoutClipping()
    {
        foreach (int combo in new[] { 0, 8, 20, 60, 120, 404, 9999 })
        {
            var view = ComboBonusPresentation.Create(combo);
            yield return null;
            view.Tick(1.25f);
            Canvas.ForceUpdateCanvases();
            foreach (var text in view.GetComponentsInChildren<TMP_Text>())
            {
                text.ForceMeshUpdate();
                Assert.LessOrEqual(text.preferredWidth, text.rectTransform.rect.width + 1, text.name + " width");
            }
            Capture("bonus-" + combo + ".png");
            if (combo == 404 && output != null)
            {
                for (int frame = 0; frame < 118; frame++)
                {
                    view.Tick(frame / 30f);
                    Capture("motion-" + frame.ToString("D3") + ".png");
                }
            }
            DisplaySettings.SetReducedEffectsForTest(true);
            view.Tick(.6f);
            Assert.AreEqual(Vector3.one, view.transform.Find("Lettering").localScale);
            if (combo == 404) Capture("bonus-404-low.png");
            DisplaySettings.SetReducedEffectsForTest(false);
            Object.Destroy(view.gameObject);
            yield return null;
        }
    }

    // 製品のCanvasをそのまま描画先へ映す。素材やシーンは書き換えない。
    void Capture(string filename)
    {
        if (output == null) return;
        var cameraGo = new GameObject("CaptureCamera");
        var camera = cameraGo.AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(.025f, .035f, .075f);
        camera.cullingMask = 1 << 5;
        camera.nearClipPlane = .1f;
        camera.farClipPlane = 100;
        cameraGo.AddComponent<UniversalAdditionalCameraData>();
        var canvases = Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None);
        var previous = new List<(Canvas canvas, RenderMode mode, Camera camera, float plane)>();
        var target = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32);
        var pixels = new Texture2D(1280, 720, TextureFormat.RGB24, false);
        var active = RenderTexture.active;
        try
        {
            target.Create();
            camera.targetTexture = target;
            camera.aspect = 1280f / 720;
            foreach (var canvas in canvases)
            {
                if (!canvas.isRootCanvas) continue;
                previous.Add((canvas, canvas.renderMode, canvas.worldCamera, canvas.planeDistance));
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = camera;
                canvas.planeDistance = 10;
                foreach (var t in canvas.GetComponentsInChildren<Transform>()) t.gameObject.layer = 5;
            }
            Canvas.ForceUpdateCanvases();
            RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
            RenderTexture.active = target;
            pixels.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
            pixels.Apply();
            File.WriteAllBytes(Path.Combine(output, filename), pixels.EncodeToPNG());
        }
        finally
        {
            foreach (var entry in previous)
            {
                entry.canvas.renderMode = entry.mode;
                entry.canvas.worldCamera = entry.camera;
                entry.canvas.planeDistance = entry.plane;
            }
            RenderTexture.active = active;
            Object.DestroyImmediate(cameraGo);
            target.Release();
            Object.DestroyImmediate(target);
            Object.DestroyImmediate(pixels);
        }
    }
}
