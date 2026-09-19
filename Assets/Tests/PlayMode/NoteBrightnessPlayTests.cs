using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;

public class NoteBrightnessPlayTests
{
    Scene previousScene, testScene;
    bool projectorMode;
    NoteSpawner spawner;
    CuttableNote current;
    readonly List<Material> observedMaterials = new List<Material>();

    [SetUp]
    public void SetUp()
    {
        previousScene = SceneManager.GetActiveScene();
        testScene = SceneManager.CreateScene("NoteBrightness");
        SceneManager.SetActiveScene(testScene);
        projectorMode = DisplaySettings.ProjectorMode;
        var prefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
        prefab.SetActive(false);
        spawner = new GameObject("BrightnessSpawner").AddComponent<NoteSpawner>();
        spawner.notePrefab = prefab;
        spawner.OnNoteSpawned += note => current = note;
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        DisplaySettings.SetProjectorModeForTest(projectorMode);
        SceneManager.SetActiveScene(previousScene);
        if (testScene.IsValid() && testScene.isLoaded)
            yield return SceneManager.UnloadSceneAsync(testScene);
        // 修正前の再現実行で複製された材質も、後続テストへ残さない。
        foreach (var material in observedMaterials)
            if (material != null) Object.Destroy(material);
        observedMaterials.Clear();
        yield return null;
    }

    void Spawn(string color, string direction, int count)
    {
        var chart = new ChartData();
        chart.notes.Add(new NoteData { time = 1000, color = color, direction = direction, count = count });
        spawner.SetChart(chart);
        spawner.Tick(0.5);
    }

    Material ObserveBody()
    {
        // キャッシュではなく、Renderer が今まさに描画する材質を検査する。
        var material = current.GetComponent<MeshRenderer>().sharedMaterial;
        if (!observedMaterials.Contains(material)) observedMaterials.Add(material);
        return material;
    }

    [UnityTest]
    public IEnumerator MissThenReuseRestoresVisibleColorAndEmissionInBothDisplayModes()
    {
        foreach (bool projector in new[] { false, true })
        {
            DisplaySettings.SetProjectorModeForTest(projector);
            foreach (var kind in new[] {
                (color: "red", direction: "none", count: 1),
                (color: "blue", direction: "none", count: 1),
                (color: "red", direction: "up", count: 1),
                (color: "blue", direction: "up", count: 3),
                (color: "gold", direction: "none", count: 1) })
            {
                Spawn(kind.color, kind.direction, kind.count);
                yield return null;
                var first = current;
                var initialBody = ObserveBody();
                Color initialColor = initialBody.GetColor("_BaseColor");
                Color initialEmission = initialBody.GetColor("_EmissionColor");
                int created = spawner.CreatedNoteCount;
                for (int cycle = 0; cycle < 3; cycle++)
                {
                    if (kind.count > 1)
                        current.Cut(current.transform.position, Vector3.up * 6, CutDirection.Up, SaberHand.Left);
                    current.MarkMiss();
                    var missedBody = ObserveBody();
                    Assert.Less(missedBody.GetColor("_BaseColor").maxColorComponent, initialColor.maxColorComponent);
                    spawner.Tick(10);
                    Assert.False(first.gameObject.activeInHierarchy);
                    Spawn(kind.color, kind.direction, kind.count);
                    yield return null;
                    Assert.AreSame(first, current);
                    Assert.AreEqual(created, spawner.CreatedNoteCount);
                    var visibleBody = ObserveBody();
                    string context = $"projector={projector}, {kind}, cycle={cycle}";
                    Assert.AreEqual(initialColor, visibleBody.GetColor("_BaseColor"), context);
                    Assert.AreEqual(initialEmission, visibleBody.GetColor("_EmissionColor"), context);
                    Assert.AreSame(initialBody, visibleBody, "ミス時に表示用の材質を複製しない: " + context);
                }
            }
        }
    }

    [UnityTest]
    public IEnumerator MissUsesOwnedMaterialAndStaysDimUntilReuse()
    {
        Spawn("blue", "up", 1);
        yield return null;
        var ownedBody = ObserveBody();
        current.MarkMiss();
        var missedBody = ObserveBody();
        Assert.AreSame(ownedBody, missedBody, "表示用材質と明るさを復元する材質が一致すること");
        Color dimEmission = missedBody.GetColor("_EmissionColor");
        current.GetComponent<NoteVisuals>().SetEmissionBoost(4f);
        yield return null;
        Assert.AreEqual(dimEmission, ObserveBody().GetColor("_EmissionColor"));
        Object.Destroy(spawner.gameObject);
        yield return null;
        yield return null;
        Assert.True(ownedBody == null, "表示用材質もプール破棄時に解放すること");
    }

    [UnityTest]
    public IEnumerator RecycledNoteRendersWithTheSameBrightnessAsFreshNote()
    {
        spawner.buildTimingCues = false;
        var camera = new GameObject("BrightnessCamera").AddComponent<Camera>();
        camera.enabled = false;
        camera.transform.position = new Vector3(0, 0, -5);
        camera.orthographic = true;
        camera.orthographicSize = 1f;
        camera.aspect = 1f;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black;
        camera.allowHDR = false;
        var data = camera.GetUniversalAdditionalCameraData();
        data.renderPostProcessing = false;
        data.renderShadows = false;
        data.volumeLayerMask = 0;
        foreach (bool projector in new[] { false, true })
        {
            DisplaySettings.SetProjectorModeForTest(projector);
            Spawn("blue", "up", 1);
            current.transform.position = Vector3.zero;
            yield return null;
            var first = current;
            Color[] fresh = Render(camera, projector + "-fresh");
            current.MarkMiss();
            ObserveBody();
            spawner.Tick(10);
            Spawn("blue", "up", 1);
            current.transform.position = Vector3.zero;
            yield return null;
            Assert.AreSame(first, current);
            Color[] reused = Render(camera, projector + "-reused");
            float freshBrightness = 0, difference = 0;
            for (int i = 0; i < fresh.Length; i++)
            {
                freshBrightness += fresh[i].grayscale;
                difference += Mathf.Abs(fresh[i].r - reused[i].r)
                    + Mathf.Abs(fresh[i].g - reused[i].g) + Mathf.Abs(fresh[i].b - reused[i].b);
            }
            Assert.Greater(freshBrightness / fresh.Length, .02f, "空の描画を比較しない");
            Assert.Less(difference / (fresh.Length * 3), .002f,
                "ミス後の再利用で実描画の明るさを変えない: projector=" + projector);
        }
    }

    static Color[] Render(Camera camera, string label)
    {
        var target = new RenderTexture(256, 256, 24, RenderTextureFormat.ARGB32);
        var image = new Texture2D(256, 256, TextureFormat.RGB24, false);
        var previousTarget = camera.targetTexture;
        var previousActive = RenderTexture.active;
        try
        {
            target.Create();
            camera.targetTexture = target;
            RenderPipeline.SubmitRenderRequest(camera,
                new UniversalRenderPipeline.SingleCameraRequest { destination = target });
            RenderTexture.active = target;
            image.ReadPixels(new Rect(0, 0, 256, 256), 0, 0);
            image.Apply();
            string[] args = System.Environment.GetCommandLineArgs();
            int index = System.Array.IndexOf(args, "-noteBrightnessCapture");
            if (index >= 0 && index + 1 < args.Length)
            {
                System.IO.Directory.CreateDirectory(args[index + 1]);
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(args[index + 1], label + ".png"), image.EncodeToPNG());
            }
            return image.GetPixels();
        }
        finally
        {
            camera.targetTexture = previousTarget;
            RenderTexture.active = previousActive;
            target.Release();
            Object.Destroy(target);
            Object.Destroy(image);
        }
    }
}
