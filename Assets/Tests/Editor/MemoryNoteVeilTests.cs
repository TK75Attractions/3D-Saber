using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

// EditMode では生成・破棄のメッセージを明示し、材質や別ルートの枠を残さない。
public class MemoryNoteVeilTests
{
    const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    GameObject root;
    bool projector;

    [SetUp]
    public void Setup()
    {
        projector = DisplaySettings.ProjectorMode;
        DisplaySettings.SetProjectorModeForTest(false);
        root = new GameObject("MemoryNoteVeilTests");
    }

    [TearDown]
    public void Cleanup()
    {
        var external = new List<GameObject>();
        foreach (var cue in root.GetComponentsInChildren<NoteTimingCue>(true))
        {
            if (cue.GhostRoot != null) external.Add(cue.GhostRoot);
            if (cue.ApproachRoot != null) external.Add(cue.ApproachRoot);
        }
        foreach (var note in root.GetComponentsInChildren<CuttableNote>(true))
            if (note.countLabel != null) external.Add(note.countLabel.gameObject);

        foreach (var go in external)
        {
            DisposeAll<NoteMeshBatch>(go);
            DisposeAll<LongNoteCountStyle>(go);
        }
        DisposeAll<SimultaneousNoteLink>(root);
        DisposeAll<NoteArrowMaterials>(root);
        DisposeAll<NoteVisuals>(root);
        DisposeAll<NoteMeshBatch>(root);
        DisposeAll<NoteTimingCue>(root);
        DisposeAll<SlicePieceDecay>(root);
        DisposeAll<GameplayCutFeedback>(root);
        DisposeAll<FloorTimingGuide>(root);
        DisposeAll<CuttableNote>(root);
        DisposeAll<NoteSpawner>(root);
        foreach (var go in external) if (go != null) Object.DestroyImmediate(go);
        if (root != null) Object.DestroyImmediate(root);
        DisplaySettings.SetProjectorModeForTest(projector);
    }

    static void DisposeAll<T>(GameObject go) where T : MonoBehaviour
    {
        if (go == null) return;
        foreach (var component in go.GetComponentsInChildren<T>(true))
            if (component != null)
                component.GetType().GetMethod("OnDestroy", PrivateInstance)?.Invoke(component, null);
    }

    static void UpdateVisuals(NoteVisuals visuals)
        => typeof(NoteVisuals).GetMethod("Update", PrivateInstance).Invoke(visuals, null);

    GameObject Go(string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(root.transform, false);
        return go;
    }

    CuttableNote Note(int cuts = 1, CutDirection direction = CutDirection.None)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.transform.SetParent(root.transform, false);
        Object.DestroyImmediate(go.GetComponent<Collider>());
        var note = go.AddComponent<CuttableNote>();
        note.RequiredCutCount = note.RemainingCuts = cuts;
        note.RequiredDirection = direction;
        var visuals = go.AddComponent<NoteVisuals>();
        visuals.Initialize();
        visuals.ResetForReuse();
        return note;
    }

    static void SetVeil(CuttableNote note, float veil)
    {
        note.Veil = veil;
        note.GetComponent<NoteVisuals>().SetVeil(veil);
    }

    static Material Body(CuttableNote note) => note.GetComponent<MeshRenderer>().sharedMaterial;

    static Material Part(CuttableNote note, string name)
        => note.transform.Find(name).GetComponent<MeshRenderer>().sharedMaterial;

    static float Alpha(Material material) => material.GetColor("_BaseColor").a;

    NoteSpawner Spawner()
    {
        var prefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
        prefab.name = "MemoryVeilPrefab";
        prefab.transform.SetParent(root.transform, false);
        Object.DestroyImmediate(prefab.GetComponent<Collider>());
        prefab.SetActive(false);
        var spawner = Go("Spawner").AddComponent<NoteSpawner>();
        spawner.notePrefab = prefab;
        spawner.noteRoot = root.transform;
        spawner.reuseNotes = false;
        spawner.earlyJudgeWindow = .15f;
        spawner.ConfigureFloorGuide(-3f);
        return spawner;
    }

    static void Spawn(NoteSpawner spawner, double time, params NoteData[] data)
    {
        var chart = new ChartData();
        chart.notes.AddRange(data);
        spawner.SetChart(chart);
        spawner.Tick(time);
    }

    [TestCase(0f, false)]
    [TestCase(.35f, false)]
    [TestCase(.95f, false)]
    [TestCase(0f, true)]
    [TestCase(.35f, true)]
    [TestCase(.95f, true)]
    public void BodyHaloOutlineAndBatchedOpaquePartsRespectVeil(float veil, bool projection)
    {
        DisplaySettings.SetProjectorModeForTest(projection);
        foreach (int cuts in new[] { 1, 3 })
        {
            var note = Note(cuts);
            var visuals = note.GetComponent<NoteVisuals>();
            visuals.SetContrastOutline(true);
            SetVeil(note, veil);

            float bodyAlpha = projection ? 1f : .85f;
            float haloAlpha = cuts == 1 ? .6f : .55f;
            Assert.AreEqual(veil, visuals.Veil);
            Assert.AreEqual(bodyAlpha * (1f - veil), Alpha(Body(note)), 1e-6f);
            Assert.AreEqual(haloAlpha * (1f - veil), Alpha(Part(note, "FrontHalo")), 1e-6f);
            Assert.AreEqual(.92f * (1f - veil), Alpha(Part(note, "ContrastOutline")), 1e-6f);
            Assert.AreEqual(veil < .5f, note.transform.Find("EdgeRails").GetComponent<MeshRenderer>().enabled);
            Assert.AreEqual(96, note.transform.Find("EdgeRails").GetComponent<MeshFilter>().sharedMesh.vertexCount);
            if (cuts > 1)
                Assert.AreEqual(veil < .5f, note.transform.Find("Divider").GetComponent<MeshRenderer>().enabled);

            foreach (var renderer in note.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (!renderer.name.StartsWith("InnerCore")) continue;
                Assert.AreEqual(veil < .5f, renderer.enabled);
            }
            if (veil == 0f)
                Assert.IsNull(typeof(NoteVisuals).GetField("veilMaterials", PrivateInstance).GetValue(visuals),
                    "通常ノーツでは復元用の配列を確保しない");

            visuals.pulseAmplitude = .4f;
            visuals.SetEmissionBoost(8f);
            UpdateVisuals(visuals);
            visuals.DimAfterMiss();
            UpdateVisuals(visuals);
            Assert.AreEqual(bodyAlpha * (1f - veil), Alpha(Body(note)), 1e-6f);
            Assert.AreEqual(haloAlpha * (1f - veil), Alpha(Part(note, "FrontHalo")), 1e-6f);

            note.ResetForSpawn();
            visuals.ResetForReuse();
            Assert.AreEqual(0f, note.Veil);
            Assert.AreEqual(0f, visuals.Veil);
            Assert.AreEqual(bodyAlpha, Alpha(Body(note)), 1e-6f);
            Assert.AreEqual(haloAlpha, Alpha(Part(note, "FrontHalo")), 1e-6f);
            Assert.IsTrue(note.transform.Find("EdgeRails").GetComponent<MeshRenderer>().enabled);
            Assert.AreEqual(1f, Body(note).GetFloat("_ZWrite"));
        }
    }

    [Test]
    public void LateOutlineBackLightAndCompleteInvisibilityRestoreWithoutAccumulatingAlpha()
    {
        var note = Note(1, CutDirection.Up);
        var visuals = note.GetComponent<NoteVisuals>();
        SetVeil(note, .35f);
        Assert.AreEqual(.4f * .65f, Alpha(Part(note, "BackBackLight")), 1e-6f);
        visuals.SetContrastOutline(true);
        Assert.AreEqual(.92f * .65f, Alpha(Part(note, "ContrastOutline")), 1e-6f);
        visuals.SetVeil(.35f);
        Assert.AreEqual(.85f * .65f, Alpha(Body(note)), 1e-6f);
        visuals.SetContrastOutline(false);
        Assert.IsTrue(note.transform.Find("BackBackLight").gameObject.activeSelf);

        SetVeil(note, 1f);
        foreach (var renderer in note.GetComponentsInChildren<MeshRenderer>(true))
            Assert.IsFalse(renderer.enabled, renderer.name);
        Assert.AreEqual(0f, Body(note).GetFloat("_ZWrite"));

        visuals.ResetForReuse();
        Assert.IsTrue(note.GetComponent<MeshRenderer>().enabled);
        Assert.AreEqual(.85f, Alpha(Body(note)), 1e-6f);
        Assert.AreEqual(.4f, Alpha(Part(note, "BackBackLight")), 1e-6f);
        Assert.IsFalse(visuals.ContrastOutlineShown, "元の付け外し状態を保つ");
    }

    [TestCase(0f, false)]
    [TestCase(.35f, false)]
    [TestCase(.5f, true)]
    [TestCase(.95f, true)]
    [TestCase(1f, true)]
    public void ArrivalArrowCountCracksFloorAndSimultaneousCuesUseTheSameBoundary(float veil, bool hidden)
    {
        var spawner = Spawner();
        int omens = 0;
        System.Action<CuttableNote> onOmen = _ => omens++;
        NoteTimingCue.GoldOmen += onOmen;
        try
        {
            Spawn(spawner, .75,
                new NoteData { time = 1000, x = -2, color = "gold", count = 3, direction = "up", veil = veil },
                new NoteData { time = 1000, x = 2, color = "blue", count = 1 });
            var note = spawner.LiveNotes[0];
            var other = spawner.LiveNotes[1];
            Assert.AreEqual(veil, note.Veil);
            Assert.AreEqual(veil, note.GetComponent<NoteVisuals>().Veil);
            Assert.AreEqual(!hidden, note.TimingCue.GhostRoot.activeSelf);
            Assert.AreEqual(!hidden, note.TimingCue.ApproachRoot.activeSelf);
            Assert.AreEqual(!hidden, note.transform.Find("Arrow").gameObject.activeSelf);
            Assert.AreEqual(!hidden, note.countLabel.gameObject.activeSelf);
            Assert.AreEqual(hidden ? 0f : 1f - veil, note.countLabel.alpha, 1e-6f);
            Assert.AreEqual(hidden ? 0 : 1, omens);
            Assert.AreEqual(hidden ? 1 : 2, spawner.FloorGuide.MarkerCount);

            var links = root.GetComponentsInChildren<SimultaneousNoteLink>();
            Assert.AreEqual(hidden ? 0 : 1, links.Length);
            if (!hidden)
            {
                // LineRenderer の色は 8 ビットで持つので 1/255 の丸めを許す。
                Assert.AreEqual(SimultaneousNoteLink.DefaultAlpha * (1f - veil),
                    links[0].Line.startColor.a, .5f / 255f + 1e-6f);
                foreach (var renderer in note.transform.Find("Arrow").GetComponentsInChildren<MeshRenderer>())
                    Assert.AreEqual(1f - veil, Alpha(renderer.sharedMaterial), 1e-6f);
            }

            var otherVisuals = other.GetComponent<NoteVisuals>();
            UpdateVisuals(otherVisuals);
            Color emission = Body(other).GetColor("_EmissionColor");
            float boost = hidden ? 1f : 1f + .22f * (1f - veil);
            Assert.AreEqual(otherVisuals.baseColor.b * otherVisuals.baseEmissionStrength * boost,
                emission.b, 1e-5f, "普通に見える相方も、隠れた組の位置を強調しない");

            int wrongSignals = 0;
            note.OnWrongDirection += _ => wrongSignals++;
            note.Cut(note.transform.position, Vector3.down * 8f);
            Assert.AreEqual(3, note.RemainingCuts, "方向の判定は変えない");
            Assert.AreEqual(hidden ? 0 : 1, wrongSignals);
            note.Arrow.Tick(.05f);
            if (!hidden)
                Assert.AreEqual(1f - veil, Alpha(PartOfArrow(note)), 1e-6f,
                    "向き違いの点灯も alpha を戻さない");

            note.Cut(note.transform.position, Vector3.up * 8f);
            Assert.AreEqual(2, note.RemainingCuts, "見えなくても正しい向きでは切れる");
            note.TickCracks(.3f);
            int visibleCracks = 0;
            foreach (Transform child in note.transform)
            {
                if (child.name != "Crack" || !child.gameObject.activeSelf) continue;
                visibleCracks++;
                Assert.AreEqual(1f - veil, Alpha(child.GetComponent<MeshRenderer>().sharedMaterial), 1e-6f);
            }
            Assert.AreEqual(hidden ? 0 : 1, visibleCracks);

            spawner.Tick(1.2);
            Assert.AreEqual(hidden ? 0 : 1, spawner.FloorGuide.LongProgressCount);
            if (hidden)
            {
                Assert.IsFalse(note.TimingCue.GhostRoot.activeSelf);
                Assert.IsFalse(note.TimingCue.ApproachRoot.activeSelf);
                Assert.IsFalse(note.TimingCue.OmenShown);
            }
        }
        finally { NoteTimingCue.GoldOmen -= onOmen; }
    }

    static Material PartOfArrow(CuttableNote note)
        => note.transform.Find("Arrow/Bars").GetComponent<MeshRenderer>().sharedMaterial;

    [Test]
    public void MildVeilScalesEveryFloorMarkerColorIncludingItsDarkBacking()
    {
        var spawner = Spawner();
        Spawn(spawner, .75, new NoteData { time = 1000, direction = "up" });
        var mesh = spawner.FloorGuide.GetComponent<MeshFilter>().sharedMesh;
        Color[] original = mesh.colors;
        spawner.LiveNotes[0].Veil = .35f;
        spawner.FloorGuide.Tick(spawner, .75);
        Color[] faded = mesh.colors;
        Assert.AreEqual(original.Length, faded.Length);
        // 最初の二つの四角形は、ノーツに属さない固定判定線。
        for (int i = 0; i < original.Length; i++)
        {
            Assert.AreEqual(original[i].r, faded[i].r);
            Assert.AreEqual(original[i].a * (i < 8 ? 1f : .65f), faded[i].a, 1e-6f);
        }
    }

    [Test]
    public void HiddenNotesDoNotMakeProtectionHolesButStillProduceCutBursts()
    {
        var spawner = Go("ProtectionOwner").AddComponent<NoteSpawner>();
        var feedback = GameplayCutFeedback.Create(spawner);
        var camera = Go("ProtectionCamera").AddComponent<Camera>();
        camera.transform.position = new Vector3(0, 0, -10);
        camera.pixelRect = new Rect(0, 0, 800, 400);
        var note = Note();
        note.HitTime = .1;
        feedback.Track(note);

        foreach (float veil in new[] { 0f, .35f, .5f, .95f, 1f, 0f })
        {
            SetVeil(note, veil);
            feedback.PrepareProtection(camera, 0);
            Assert.AreEqual(veil >= .5f ? 0 : 1, feedback.ProtectionCount, veil.ToString());
        }

        SetVeil(note, 1f);
        typeof(CuttableNote).GetProperty("IsCut").SetValue(note, true);
        note.NotifyJudgment(JudgmentTier.Perfect, Vector3.zero, Vector3.up * 8f);
        Assert.AreEqual(1, feedback.ActiveCount);
        Assert.IsTrue(feedback.GetComponent<MeshRenderer>().enabled);
        Assert.Greater(feedback.GetComponent<MeshFilter>().sharedMesh.vertexCount, 0);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void MemoryModeWorksBeforeAndAfterFeedbackCreationAndRestoresTheOriginalOrder(bool before)
    {
        var spawner = Spawner();
        spawner.MemoryVeilMode = before;
        Spawn(spawner, .75, new NoteData { time = 1000 });
        var feedback = spawner.GetComponentInChildren<GameplayCutFeedback>();
        var renderer = feedback.GetComponent<MeshRenderer>();
        int originalQueue = renderer.sharedMaterial.shader.renderQueue;
        Assert.AreEqual(before ? MemoryVeilLayers.AboveSortingOrder : 0, renderer.sortingOrder);
        Assert.AreEqual(before ? MemoryVeilLayers.AboveRenderQueue + 5 : originalQueue,
            renderer.sharedMaterial.renderQueue);

        spawner.MemoryVeilMode = false;
        Assert.AreEqual(0, renderer.sortingOrder);
        Assert.AreEqual(originalQueue, renderer.sharedMaterial.renderQueue);
        renderer.sortingOrder = 7;
        renderer.sharedMaterial.renderQueue = originalQueue + 1;
        spawner.MemoryVeilMode = true;
        Assert.AreEqual(MemoryVeilLayers.AboveSortingOrder, renderer.sortingOrder);
        Assert.AreEqual(MemoryVeilLayers.AboveRenderQueue + 5, renderer.sharedMaterial.renderQueue);
        spawner.MemoryVeilMode = false;
        Assert.AreEqual(7, renderer.sortingOrder);
        Assert.AreEqual(originalQueue + 1, renderer.sharedMaterial.renderQueue);
    }

    [Test]
    public void FragmentFadeFlashAndReusePreserveTheCopiedVisibility()
    {
        var note = Note();
        SetVeil(note, .95f);
        var piece = NoteFragmentPool.CreatePiece();
        piece.transform.SetParent(root.transform, false);
        piece.CopyMaterial(Body(note));
        piece.SetVeil(note.Veil);
        piece.Launch(null, Vector3.zero, Vector3.zero, false, 1f, .5f, 0f);
        var renderer = piece.GetComponent<MeshRenderer>();
        Assert.AreEqual(.85f * .05f, Alpha(renderer.sharedMaterial), 1e-6f);
        piece.Flash(Color.white, .3f);
        Assert.AreEqual(2.2f * .05f, renderer.sharedMaterial.GetColor("_EmissionColor").r, 1e-6f);
        piece.Step(.6f);
        Assert.AreEqual(.85f * .05f * .8f, Alpha(renderer.sharedMaterial), 1e-6f);

        SetVeil(note, 1f);
        piece.CopyMaterial(Body(note));
        piece.SetVeil(note.Veil);
        Assert.IsFalse(renderer.enabled);

        note.ResetForSpawn();
        note.GetComponent<NoteVisuals>().ResetForReuse();
        piece.PrepareForRent();
        piece.CopyMaterial(Body(note));
        piece.SetVeil(note.Veil);
        piece.Launch(null, Vector3.zero, Vector3.zero, false, 1f, .5f, 0f);
        Assert.IsTrue(renderer.enabled);
        Assert.AreEqual(.85f, Alpha(renderer.sharedMaterial), 1e-6f);
        piece.Flash(Color.white, .3f);
        Assert.AreEqual(2.2f, renderer.sharedMaterial.GetColor("_EmissionColor").r, 1e-6f);
    }
}
