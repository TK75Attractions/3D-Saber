using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// プロジェクターモード(高コントラスト表示)の検証。
// テストアセンブリ既定は OFF(TestDisplayModeDefaults)。ここでは明示的に ON/OFF を切り替え、TearDown で OFF に戻す。
public class ProjectorModeTests
{
    private readonly List<GameObject> created = new List<GameObject>();

    [TearDown]
    public void Cleanup()
    {
        foreach (var go in created)
        {
            if (go != null) Object.DestroyImmediate(go);
        }
        created.Clear();
        var vol = GameObject.Find(ProjectorMode.VolumeObjectName);
        if (vol != null) Object.DestroyImmediate(vol);
        foreach (var n in Object.FindObjectsByType<CuttableNote>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (n != null) Object.DestroyImmediate(n.gameObject);
        }
        DisplaySettings.SetProjectorModeForTest(false);
    }

    private Camera MakeCamera()
    {
        var go = new GameObject("projCam");
        created.Add(go);
        go.transform.position = new Vector3(0f, 0f, -10f);
        var cam = go.AddComponent<Camera>();
        cam.pixelRect = new Rect(0, 0, 1600, 900);
        cam.aspect = 1600f / 900f;
        return cam;
    }

    // ---- 設定 ----

    [Test]
    public void TestOverride_DoesNotPersist()
    {
        DisplaySettings.SetProjectorModeForTest(true);
        Assert.IsTrue(DisplaySettings.ProjectorMode);
        DisplaySettings.SetProjectorModeForTest(false);
        Assert.IsFalse(DisplaySettings.ProjectorMode);
        // キャッシュを捨てても PlayerPrefs には書いていないので、テストが値を汚さない
        DisplaySettings.ResetProjectorModeCacheForTest();
        bool fromPrefs = DisplaySettings.ProjectorMode;
        Assert.AreEqual(PlayerPrefs.GetInt("displayProjectorMode", 1) != 0, fromPrefs);
        DisplaySettings.SetProjectorModeForTest(false);
    }

    [Test]
    public void ToggleLabel_ReflectsState()
    {
        Assert.AreEqual("PROJECTOR: ON", ProjectorModeToggleUI.LabelFor(true));
        Assert.AreEqual("PROJECTOR: OFF", ProjectorModeToggleUI.LabelFor(false));
    }

    // ---- ポストプロセス ----

    [Test]
    public void Apply_On_CreatesGlobalVolume_AndEnablesCameraPostProcessing()
    {
        DisplaySettings.SetProjectorModeForTest(true);
        var cam = MakeCamera();

        var volume = ProjectorMode.Apply(cam);
        Assert.IsNotNull(volume, "ポストプロセス用 Volume が生成される");
        Assert.IsTrue(volume.isGlobal);
        Assert.IsTrue(volume.enabled);
        Assert.Greater(volume.priority, 0f, "シーン側の Volume より優先");
        Assert.IsTrue(volume.profile.TryGet(out ColorAdjustments ca), "露出/コントラスト/彩度の上書きを持つ");
        Assert.AreEqual(ProjectorMode.PostExposure, ca.postExposure.value, 1e-4f);
        Assert.AreEqual(ProjectorMode.Contrast, ca.contrast.value, 1e-4f);
        Assert.AreEqual(ProjectorMode.Saturation, ca.saturation.value, 1e-4f);
        Assert.IsTrue(volume.profile.TryGet(out Bloom bloom) && bloom.intensity.value == 0f, "Bloom は霞になるので効かせない");
        Assert.IsTrue(cam.GetUniversalAdditionalCameraData().renderPostProcessing, "カメラのポストプロセスを有効化");
        Assert.AreSame(volume, ProjectorMode.Apply(cam), "2回目は同じ Volume を使い回す");
    }

    [Test]
    public void Apply_Off_DisablesVolume_AndCameraPostProcessing()
    {
        var cam = MakeCamera();
        DisplaySettings.SetProjectorModeForTest(true);
        var volume = ProjectorMode.Apply(cam);
        Assert.IsTrue(volume.enabled);

        DisplaySettings.SetProjectorModeForTest(false);
        var same = ProjectorMode.Apply(cam);
        Assert.AreSame(volume, same);
        Assert.IsFalse(same.enabled, "OFF では Volume を無効化");
        Assert.IsFalse(cam.GetUniversalAdditionalCameraData().renderPostProcessing, "OFF ではカメラのポストプロセスも戻す");
    }

    // ---- ステージ(ゲート/小節線/フォグ) ----

    [Test]
    public void StageValues_AreThickerBrighterAndLessFoggy_InProjectorMode()
    {
        DisplaySettings.SetProjectorModeForTest(false);
        float fogOff = GameStageSkin.FogDensity, barOff = GameStageSkin.GateBarThickness, emOff = GameStageSkin.GateEmission;
        float lineOff = GameStageSkin.BarLineAlpha, fillOff = GameStageSkin.PanelFillAlpha;
        DisplaySettings.SetProjectorModeForTest(true);
        Assert.Less(GameStageSkin.FogDensity, fogOff, "奥のノーツが溶けないようフォグは薄く");
        Assert.Greater(GameStageSkin.GateBarThickness, barOff, "ゲートの線は太く");
        Assert.Greater(GameStageSkin.GateEmission, emOff, "ゲートは明るく");
        Assert.Greater(GameStageSkin.BarLineAlpha, lineOff, "小節線は濃く");
        Assert.Greater(GameStageSkin.PanelFillAlpha, fillOff, "判定面の塗りは少し濃く");
        Assert.Less(SimultaneousNoteLink.DefaultAlpha, GameStageSkin.BarLineAlpha, "同時線は小節線より薄い、の序列は保つ");
    }

    // ---- ノーツ ----

    private NoteVisuals BuildNoteVisuals(SaberHand hand, CutDirection dir)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        created.Add(go);
        var note = go.AddComponent<CuttableNote>();
        note.RequiredHand = hand;
        note.RequiredDirection = dir;
        var v = go.AddComponent<NoteVisuals>();
        var awake = typeof(NoteVisuals).GetMethod("Awake",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        awake.Invoke(v, null);
        return v;
    }

    [Test]
    public void NoteVisuals_RailsThicker_AndColorsRemapped_InProjectorMode()
    {
        DisplaySettings.SetProjectorModeForTest(false);
        var normal = BuildNoteVisuals(SaberHand.Left, CutDirection.None);
        float thickOff = normal.FindChildByName("EdgeTop").localScale.y;
        Assert.AreEqual(UISkinPalette.LogoBlue.r, normal.baseColor.r, 1e-3f, "通常モードは左手=LogoBlue のまま");

        DisplaySettings.SetProjectorModeForTest(true);
        var proj = BuildNoteVisuals(SaberHand.Left, CutDirection.None);
        Assert.Greater(proj.FindChildByName("EdgeTop").localScale.y, thickOff, "縁取りレールは太く");
        Assert.GreaterOrEqual(proj.baseEmissionStrength, ProjectorMode.NoteEmission, "発光は強く");
        Assert.Greater(proj.baseColor.r + proj.baseColor.g, UISkinPalette.LogoBlue.r + UISkinPalette.LogoBlue.g,
            "深い青は明るいシアン寄りに置き換わる(輝度差で見分ける)");

        var flick = BuildNoteVisuals(SaberHand.Any, CutDirection.Up);
        Assert.Greater(flick.baseColor.r, 0.9f, "バイオレットのフリック色はピンク寄り(赤成分が高い)へ");
    }

    [Test]
    public void BuildArrow_UsesWhiteBarsOnDarkBacking_InProjectorMode()
    {
        DisplaySettings.SetProjectorModeForTest(true);
        var parent = new GameObject("noteProj");
        created.Add(parent);
        NoteSpawner.BuildArrow(parent.transform, CutDirection.Up);
        var bar = parent.transform.Find("Arrow/BarL").GetComponent<MeshRenderer>().sharedMaterial;
        Assert.Greater(bar.GetColor("_BaseColor").r, 0.9f, "矢印は白");
        var backing = parent.transform.Find("Arrow/ArrowBacking").GetComponent<MeshRenderer>().sharedMaterial;
        Assert.Less(backing.GetColor("_BaseColor").r, 0.1f, "下敷きは暗い");
        Assert.Greater(parent.transform.Find("Arrow/BarL").localScale.x, 0.1f, "矢印の線は太い");

        DisplaySettings.SetProjectorModeForTest(false);
        var parent2 = new GameObject("noteNormal");
        created.Add(parent2);
        NoteSpawner.BuildArrow(parent2.transform, CutDirection.Up);
        var bar2 = parent2.transform.Find("Arrow/BarL").GetComponent<MeshRenderer>().sharedMaterial;
        Assert.Less(bar2.GetColor("_BaseColor").r, 0.1f, "通常モードの矢印は黒のまま");
    }

    [Test]
    public void NoteSpawner_ScalesNoteFace_InProjectorMode()
    {
        DisplaySettings.SetProjectorModeForTest(true);
        var sGo = new GameObject("spawnerProj");
        created.Add(sGo);
        var sp = sGo.AddComponent<NoteSpawner>();
        sp.buildTimingCues = false;
        var prefab = new GameObject("notePrefab");
        created.Add(prefab);
        prefab.transform.localScale = new Vector3(0.8f, 0.8f, 0.8f);
        prefab.AddComponent<CuttableNote>();
        sp.notePrefab = prefab;
        sp.approachTime = 2.0f;
        var chart = new ChartData { bpm = 120f };
        chart.notes.Add(new NoteData { time = 1000f, x = 0, y = 0, type = "tap" });
        CuttableNote spawned = null;
        sp.OnNoteSpawned += n => spawned = n;
        sp.SetChart(chart);
        sp.Tick(0.0);

        Assert.IsNotNull(spawned);
        Assert.AreEqual(0.8f * ProjectorMode.NoteScale, spawned.transform.localScale.x, 1e-4f, "正面は一回り大きく");
        Assert.AreEqual(0.8f, spawned.transform.localScale.z, 1e-4f, "奥行き(ロングの z 伸長の土台)は変えない");
    }

    // ---- セーバーの刃 ----

    private SaberInputBridge MakeBladeBridge(Camera cam)
    {
        var go = new GameObject("bladeBridge");
        created.Add(go);
        var bridge = go.AddComponent<SaberInputBridge>();
        bridge.targetCamera = cam;
        bridge.useBladeMode = true;
        bridge.enableSmoothing = false;
        bridge.remapToCameraView = false;
        return bridge;
    }

    [Test]
    public void Blade_IsWiderWithDarkOutline_InProjectorMode()
    {
        var cam = MakeCamera();
        DisplaySettings.SetProjectorModeForTest(true);
        var bridge = MakeBladeBridge(cam);
        bridge.ApplyMouseWorld(new Vector3(1f, 0.5f, 0f)); // 擬似ブレードを公開 → 線が生成される
        var blade = bridge.GetComponent<LineRenderer>();
        Assert.IsNotNull(blade);
        Assert.AreEqual(bridge.bladeWidth * ProjectorMode.BladeWidthScale, blade.startWidth, 1e-4f, "刃は太く");
        var outline = bridge.BladeOutlineForTest;
        Assert.IsNotNull(outline, "暗い縁取り線が敷かれる");
        Assert.IsTrue(outline.enabled);
        Assert.Greater(outline.startWidth, blade.startWidth, "縁取りは刃より太い");
        Assert.Greater(outline.GetPosition(0).z, blade.GetPosition(0).z, "縁取りは刃より奥に置く");

        // OFF に戻すと縁取りは消え、刃は元の幅
        DisplaySettings.SetProjectorModeForTest(false);
        bridge.RefreshProjectorStyle();
        Assert.IsFalse(outline.enabled);
        Assert.AreEqual(bridge.bladeWidth, blade.startWidth, 1e-4f);
    }

    [Test]
    public void Blade_HasNoOutline_InNormalMode()
    {
        var cam = MakeCamera();
        DisplaySettings.SetProjectorModeForTest(false);
        var bridge = MakeBladeBridge(cam);
        bridge.ApplyMouseWorld(new Vector3(1f, 0.5f, 0f));
        Assert.IsNull(bridge.BladeOutlineForTest, "通常モードでは縁取り線を作らない");
        Assert.AreEqual(bridge.bladeWidth, bridge.GetComponent<LineRenderer>().startWidth, 1e-4f);
    }

    // ---- UI ----

    [Test]
    public void SubtleGray_IsBrighter_InProjectorMode()
    {
        DisplaySettings.SetProjectorModeForTest(false);
        Color normal = UISkinPalette.SubtleGray;
        DisplaySettings.SetProjectorModeForTest(true);
        Color proj = UISkinPalette.SubtleGray;
        Assert.Greater(proj.r + proj.g + proj.b, normal.r + normal.g + normal.b);
    }

    [Test]
    public void Hotkey_Ensure_IsSingleton()
    {
        var a = ProjectorModeHotkey.Ensure();
        created.Add(a.gameObject);
        Assert.AreSame(a, ProjectorModeHotkey.Ensure());
    }
}
