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

public class ClearAchievementPlayTests
{
    readonly Dictionary<FieldInfo, object> savedSession = new Dictionary<FieldInfo, object>();
    string id, output;
    bool previousLow;

    [SetUp]
    public void SetUp()
    {
        foreach (var field in typeof(GameSession).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (!field.IsLiteral && !field.IsInitOnly) savedSession[field] = field.GetValue(null);
        id = "__ClearAchievement_" + Guid.NewGuid().ToString("N");
        GameSession.SelectedSongId = id;
        GameSession.SelectedSongTitle = "ACHIEVEMENT TEST";
        GameSession.SelectedDifficulty = "Normal";
        GameSession.IsCalibrationMode = false;
        GameSession.ResetResult();
        previousLow = DisplaySettings.ReducedEffects;
        DisplaySettings.SetReducedEffectsForTest(false);
        SceneManager.SetActiveScene(SceneManager.CreateScene(id));
        string[] args = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(args, "-achievementOutput");
        output = at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
        if (output != null) Directory.CreateDirectory(output);
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        Time.timeScale = 1;
        // 次の撮影テストへリザルトの到着カーテンを持ち越さない。
        float transitionDeadline = Time.realtimeSinceStartup + 3;
        while (ScreenTransition.IsBusy && Time.realtimeSinceStartup < transitionDeadline) yield return null;
        var scene = SceneManager.GetActiveScene();
        SceneManager.SetActiveScene(SceneManager.CreateScene(id + "_Cleanup"));
        yield return SceneManager.UnloadSceneAsync(scene);
        DisplaySettings.SetReducedEffectsForTest(previousLow);
        HighScoreStore.Clear(id, "Normal");
        foreach (var field in savedSession) field.Key.SetValue(null, field.Value);
        savedSession.Clear();
    }

    [UnityTest]
    public IEnumerator TextAndGeometryOpenWithoutStretchingTheLetters()
    {
        foreach (bool perfect in new[] { false, true })
        {
            var view = ClearAchievementPresentation.Create(perfect);
            yield return null;
            string name = perfect ? "all-perfect" : "full-combo";
            var text = view.transform.Find("Content/LetterReveal/Lettering/AchievementText").GetComponent<TMP_Text>();
            Assert.AreEqual(perfect ? "ALL PERFECT" : "FULL COMBO", text.text);
            Assert.AreEqual("ChakraPetch-BoldItalic", text.font.name);
            Assert.True(text.fontSharedMaterial.HasProperty("_FaceTex"));
            Assert.IsNotNull(text.fontSharedMaterial.GetTexture("_FaceTex"));
            var mask = text.transform.parent.parent.GetComponent<RectTransform>();
            view.Tick(.12f);
            float start = view.FrameHalfWidth, startMask = mask.rect.width;
            var startScale = text.transform.parent.localScale;
            view.Tick(.3f);
            float middle = view.FrameHalfWidth;
            view.Tick(.9f);
            Assert.Greater(middle, start);
            Assert.Greater(view.FrameHalfWidth, middle);
            Assert.Greater(mask.rect.width, startMask);
            Assert.AreEqual(startScale.x / startScale.y,
                text.transform.parent.localScale.x / text.transform.parent.localScale.y, .0001f,
                "開く間に文字の縦横比を変えない");
            Canvas.ForceUpdateCanvases();
            text.ForceMeshUpdate();
            var frameMesh = view.GetComponentInChildren<ClearAchievementGraphic>().canvasRenderer.GetMesh();
            Assert.Greater(frameMesh.vertexCount, 100, "枠と光の図形を実際のCanvasRendererへ送る");
            Vector3 min = mask.InverseTransformPoint(text.transform.TransformPoint(text.textBounds.min));
            Vector3 max = mask.InverseTransformPoint(text.transform.TransformPoint(text.textBounds.max));
            Assert.Greater(min.x, mask.rect.xMin + 5);
            Assert.Less(max.x, mask.rect.xMax - 5);
            Capture(name + ".png");
            if (output != null)
            {
                for (int frame = 0; frame <= 93; frame++)
                {
                    view.Tick(frame / 30f);
                    Capture(name + "-" + frame.ToString("D3") + ".png");
                }
                view.Tick(1.1f);
                Capture(name + "-4x3.png", 1024, 768);
                Capture(name + "-ultrawide.png", 1920, 810);
            }
            DisplaySettings.SetReducedEffectsForTest(true);
            view.Tick(.2f);
            Assert.AreEqual(904, view.FrameHalfWidth);
            var lowScale = text.transform.parent.localScale;
            view.Tick(1.1f);
            Assert.AreEqual(lowScale, text.transform.parent.localScale);
            Capture(name + "-low.png");
            DisplaySettings.SetReducedEffectsForTest(false);
            Object.Destroy(view.gameObject);
            yield return null;
        }
    }

    [UnityTest]
    public IEnumerator PerfectCompletionShowsAllPerfectThenBonusThenResult() => FinishFlow(true);

    [UnityTest]
    public IEnumerator UnbrokenGoodCompletionShowsFullComboThenBonusThenResult() => FinishFlow(false);

    IEnumerator FinishFlow(bool perfect)
    {
        var root = new GameObject("FinishingGame");
        var score = root.AddComponent<ScoreManager>();
        score.RegisterHit(JudgmentTier.Perfect);
        score.RegisterHit(JudgmentTier.Perfect);
        score.RegisterHit(perfect ? JudgmentTier.Perfect : JudgmentTier.Good);
        var manager = root.AddComponent<GamePlayManager>();
        manager.enabled = false;
        manager.scoreManager = score;
        manager.songPlayer = root.AddComponent<SongPlayer>();
        manager.noteSpawner = root.AddComponent<NoteSpawner>();
        typeof(GamePlayManager).GetMethod("FinishGame", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(manager, null);
        var achievement = Object.FindFirstObjectByType<ClearAchievementPresentation>();
        Assert.IsNotNull(achievement);
        Assert.AreEqual(perfect, achievement.IsAllPerfect);
        Assert.AreEqual(1, Object.FindObjectsByType<ClearAchievementPresentation>(FindObjectsSortMode.None).Length);
        Assert.IsNull(Object.FindFirstObjectByType<ComboBonusPresentation>());
        Assert.False(score.IsFinalized);
        // timeScale=0でも終了演出を最後まで進められる。
        Time.timeScale = 0;
        float deadline = Time.realtimeSinceStartup + 12;
        while (Object.FindFirstObjectByType<ComboBonusPresentation>() == null && Time.realtimeSinceStartup < deadline) yield return null;
        Assert.IsNotNull(Object.FindFirstObjectByType<ComboBonusPresentation>());
        yield return null;
        Assert.IsNull(Object.FindFirstObjectByType<ClearAchievementPresentation>());
        while (SceneManager.GetActiveScene().name != "Result" && Time.realtimeSinceStartup < deadline) yield return null;
        Assert.AreEqual("Result", SceneManager.GetActiveScene().name);
        yield return null;
        Assert.AreEqual(perfect ? 1200 : 1000, GameSession.FinalScore);
        Assert.AreEqual(300, GameSession.FinalComboBonus);
        Assert.AreEqual(3, GameSession.FinalMaxCombo);
        Assert.AreEqual(1, HighScoreStore.Load(id, "Normal").entries.Count);
        Assert.IsNull(Object.FindFirstObjectByType<ClearAchievementPresentation>());
    }

    [UnityTest]
    public IEnumerator EmptyRunSkipsAchievement()
    {
        var root = new GameObject("EmptyGame");
        var manager = root.AddComponent<GamePlayManager>();
        manager.enabled = false;
        manager.scoreManager = root.AddComponent<ScoreManager>();
        manager.songPlayer = root.AddComponent<SongPlayer>();
        manager.noteSpawner = root.AddComponent<NoteSpawner>();
        manager.resultSceneName = "";
        typeof(GamePlayManager).GetMethod("FinishGame", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(manager, null);
        Assert.IsNull(Object.FindFirstObjectByType<ClearAchievementPresentation>());
        Assert.IsNotNull(Object.FindFirstObjectByType<ComboBonusPresentation>());
        yield return null;
    }

    // 実際の TMP と UI メッシュを撮影し、確認用素材の貼り付けで代用しない。
    void Capture(string filename, int width = 1280, int height = 720)
    {
        if (output == null) return;
        var cameraRoot = new GameObject("AchievementCapture");
        var camera = cameraRoot.AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(.025f, .035f, .075f);
        camera.cullingMask = 1 << 5;
        camera.nearClipPlane = .1f;
        camera.farClipPlane = 100;
        cameraRoot.AddComponent<UniversalAdditionalCameraData>();
        var previous = new List<(Canvas canvas, RenderMode mode, Camera camera, float plane)>();
        var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
        var pixels = new Texture2D(width, height, TextureFormat.RGB24, false);
        var active = RenderTexture.active;
        try
        {
            target.Create();
            camera.targetTexture = target;
            camera.aspect = width / (float)height;
            foreach (var canvas in Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None))
            {
                if (!canvas.isRootCanvas) continue;
                previous.Add((canvas, canvas.renderMode, canvas.worldCamera, canvas.planeDistance));
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = camera;
                canvas.planeDistance = 10;
                foreach (var child in canvas.GetComponentsInChildren<Transform>()) child.gameObject.layer = 5;
            }
            Canvas.ForceUpdateCanvases();
            RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
            RenderTexture.active = target;
            pixels.ReadPixels(new Rect(0, 0, width, height), 0, 0);
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
            Object.DestroyImmediate(cameraRoot);
            target.Release();
            Object.DestroyImmediate(target);
            Object.DestroyImmediate(pixels);
        }
    }
}
