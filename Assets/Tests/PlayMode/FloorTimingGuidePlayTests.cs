using System;
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

public class FloorTimingGuidePlayTests
{
    string song, difficulty;
    bool calibration, projector, reduced;
    [SetUp] public void Save()
    {
        song = GameSession.SelectedSongId; difficulty = GameSession.SelectedDifficulty;
        calibration = GameSession.IsCalibrationMode;
        projector = DisplaySettings.ProjectorMode; reduced = DisplaySettings.ReducedEffects;
    }
    [TearDown] public void Restore()
    {
        GameSession.SelectedSongId = song; GameSession.SelectedDifficulty = difficulty;
        GameSession.IsCalibrationMode = calibration;
        DisplaySettings.SetProjectorModeForTest(projector); DisplaySettings.SetReducedEffectsForTest(reduced);
    }

    [UnityTest, Timeout(180000)]
    public IEnumerator GameRendersGuidesAcrossThemesAndReusesThemWithoutResourceGrowth()
    {
        GameSession.SelectedSongId = "Epilogue"; GameSession.SelectedDifficulty = "easy";
        GameSession.IsCalibrationMode = false;
        yield return SceneManager.LoadSceneAsync("Game", LoadSceneMode.Single);
        var manager = Object.FindFirstObjectByType<GamePlayManager>();
        float deadline = Time.realtimeSinceStartup + 30;
        while ((manager.noteSpawner.FloorGuide == null || !manager.songPlayer.IsPlaying) && Time.realtimeSinceStartup < deadline) yield return null;
        Assert.NotNull(manager.noteSpawner.FloorGuide, "実Game起動で床ガイドが接続される");
        manager.StopAllCoroutines(); manager.enabled = false; manager.songPlayer.Stop();
        foreach (var judge in Object.FindObjectsByType<SaberCutJudge>(FindObjectsSortMode.None)) judge.autonomous = false;
        foreach (var countdown in Object.FindObjectsByType<GameStartCountdown>(FindObjectsSortMode.None)) countdown.gameObject.SetActive(false);
        var spawner = manager.noteSpawner;
        spawner.approachTime = 1.5f; spawner.SetExtraOffsetSeconds(0);
        var output = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../Outputs/FloorGuide"));
        Directory.CreateDirectory(output);
        for (int theme = 0; theme < StageThemeCatalog.Count; theme++)
        {
            var old = Object.FindFirstObjectByType<FloorRenderer>();
            if (old != null) Object.Destroy(old.gameObject);
            yield return null;
            var floor = new GameObject("GuidePreviewStage").AddComponent<FloorRenderer>();
            floor.randomizeOnPlay = false; floor.Build((StageTheme)theme);
            for (int mode = 0; mode < 2; mode++)
            {
                DisplaySettings.SetProjectorModeForTest(mode == 1);
                DisplaySettings.SetReducedEffectsForTest(mode == 1);
                ProjectorMode.Apply(Camera.main);
                spawner.SetChart(Chart());
                foreach (double time in new[] { 2.35, 2.75, 3.0 })
                {
                    spawner.Tick(time); floor.Tick(time);
                    yield return null;
                    Assert.Greater(spawner.FloorGuide.MarkerCount, 0);
                    Capture(Path.Combine(output, $"theme-{theme:D2}-mode-{mode}-time-{time:F2}.png"));
                }
                if (theme == 0 && mode == 0)
                {
                    Directory.CreateDirectory(Path.Combine(output, "motion"));
                    spawner.SetChart(Chart());
                    for (int frame = 0; frame < 72; frame++)
                    {
                        double time = 1.85 + frame / 30.0;
                        spawner.Tick(time); floor.Tick(time);
                        yield return null;
                        Capture(Path.Combine(output, "motion", $"{frame:D4}.png"));
                    }
                }
            }
        }
        var guide = spawner.FloorGuide;
        var mesh = guide.GetComponent<MeshFilter>().sharedMesh;
        var material = guide.GetComponent<MeshRenderer>().sharedMaterial;
        for (int i = 0; i < 40; i++)
        {
            spawner.SetChart(Chart()); spawner.Tick(3);
            Assert.AreSame(mesh, guide.GetComponent<MeshFilter>().sharedMesh);
            Assert.AreSame(material, guide.GetComponent<MeshRenderer>().sharedMaterial);
            foreach (var note in spawner.LiveNotes)
                if (Math.Abs(note.HitTime - 3) < .001) note.Cut(note.transform.position, (Vector3)CutDirectionHelper.ToVector(note.RequiredDirection) * 8);
            spawner.Tick(3.01);
        }
        spawner.enabled = false;
        Assert.AreEqual(0, guide.MarkerCount, "停止したフレームでガイドを消す");
        spawner.enabled = true;
        spawner.SetChart(new ChartData()); Assert.AreEqual(0, guide.MarkerCount);
        Object.Destroy(guide.gameObject); yield return null; yield return null;
        Assert.True(mesh == null); Assert.True(material == null);
        var arrowOwner = new GameObject("ArrowCleanupCheck");
        NoteSpawner.BuildArrow(arrowOwner.transform, CutDirection.Up);
        var arrowMesh = arrowOwner.transform.Find("Arrow/Bars").GetComponent<MeshFilter>().sharedMesh;
        var outlineMesh = arrowOwner.transform.Find("Arrow/ArrowBacking").GetComponent<MeshFilter>().sharedMesh;
        Object.Destroy(arrowOwner); yield return null; yield return null;
        Assert.True(arrowMesh == null, "生成した矢印メッシュを解放する");
        Assert.True(outlineMesh == null, "輪郭の独立メッシュも解放する");
        File.WriteAllText(Path.Combine(output, "verification.txt"), "Actual Game camera; all stage themes, normal and projector/low settings; synthetic chart and clock. 40 chart resets, mesh/material reuse and cleanup verified.\n");
    }

    static ChartData Chart()
    {
        var chart = new ChartData { bpm = 120, coordScale = 1 };
        chart.notes.Add(new NoteData { time = 3000, x = -1.65f, y = .3f, direction = "up", count = 1 });
        chart.notes.Add(new NoteData { time = 3000, x = 1.65f, y = .3f, direction = "right", color = "blue", count = 1 });
        chart.notes.Add(new NoteData { time = 3450, x = -.55f, y = -.5f, color = "red", count = 1 });
        chart.notes.Add(new NoteData { time = 3800, x = .8f, y = 1.0f, direction = "downleft", count = 1 });
        return chart;
    }

    static void Capture(string path)
    {
        var camera = Camera.main;
        var target = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32);
        var pixels = new Texture2D(1280, 720, TextureFormat.RGB24, false);
        var oldTarget = camera.targetTexture; var oldActive = RenderTexture.active;
        var canvases = Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None)
            .Where(c => c.isRootCanvas && c.renderMode == RenderMode.ScreenSpaceOverlay).ToArray();
        try
        {
            target.Create(); camera.targetTexture = target; camera.aspect = 16f / 9;
            foreach (var canvas in canvases) { canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = camera; canvas.planeDistance = 1; }
            Canvas.ForceUpdateCanvases();
            RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
            RenderTexture.active = target;
            pixels.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0); pixels.Apply();
            File.WriteAllBytes(path, pixels.EncodeToPNG());
        }
        finally
        {
            foreach (var canvas in canvases) canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            camera.targetTexture = oldTarget; RenderTexture.active = oldActive;
            target.Release(); Object.Destroy(target); Object.Destroy(pixels);
        }
    }
}
