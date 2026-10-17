using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Saber.ChartEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

public class ChartPlaybackPreviewTests
{
    SaberChartPlaybackPreview preview;
    SaberChartDocument document;
    float approach;
    bool hadApproach;

    [SetUp]
    public void SetUp()
    {
        hadApproach = PlayerPrefs.HasKey("noteApproachTime");
        approach = GameSession.NoteApproachTime;
        GameSession.NoteApproachTime = 2;
        document = new SaberChartDocument { coordScale = 2, offsetMs = 250 };
        document.notes.Add(new SaberChartNote { time = 2000, x = -1, y = .5f, color = "red" });
        document.notes.Add(new SaberChartNote { time = 2000, x = 1, y = -.5f, color = "blue", type = "direction", direction = "up" });
        document.notes.Add(new SaberChartNote { time = 4000, x = 0, count = 3, lengthMs = 1400, type = "long", color = "gold" });
        preview = new SaberChartPlaybackPreview();
    }

    [TearDown]
    public void TearDown()
    {
        preview?.Dispose();
        GameSession.NoteApproachTime = approach;
        if (!hadApproach) GameSession.ResetNoteApproachTime();
    }

    [Test]
    public void SeekingUsesChartTimeOffsetAndCoordinatesWithoutMutatingDocument()
    {
        string before = SaberChartUtility.ToJson(document, false);
        preview.Tick(document, 1.25);
        Assert.AreEqual(2, preview.VisibleNoteCount);
        var note = Notes().Single(n => n.RequiredHand == SaberHand.Right);
        Assert.AreEqual(new Vector3(-2, 1, 10), note.transform.localPosition);
        preview.Tick(document, 2.25);
        Assert.AreEqual(0, note.transform.localPosition.z, .0001);
        preview.Tick(document, 8);
        Assert.AreEqual(0, preview.VisibleNoteCount);
        preview.Tick(document, 1.25);
        Assert.AreEqual(new Vector3(-2, 1, 10), Notes().Single(n => n.RequiredHand == SaberHand.Right).transform.localPosition);
        Assert.AreEqual(before, SaberChartUtility.ToJson(document, false));
    }

    [Test]
    public void UnsavedEditsDeletionAndDocumentReplacementRefreshImmediately()
    {
        preview.Tick(document, 1.25);
        document.notes[0].color = "gold";
        document.notes[0].x = .4f;
        preview.Tick(document, 1.25);
        var gold = Notes().Single(n => n.IsGold);
        Assert.AreEqual(.8f, gold.transform.localPosition.x, .001);
        Assert.AreEqual(UISkinPalette.NoteGold, gold.GetComponent<NoteVisuals>().baseColor);
        document.notes.RemoveAt(0);
        preview.Tick(document, 1.25);
        Assert.AreEqual(1, preview.VisibleNoteCount);
        preview.Tick(new SaberChartDocument(), 1.25);
        Assert.AreEqual(0, preview.VisibleNoteCount);
    }

    [Test]
    public void LongCutsAndRewindAreDeterministic()
    {
        preview.Tick(document, 4.25);
        var note = Notes().Single();
        Assert.AreEqual(2, note.RemainingCuts);
        var label = preview.WorldRoot.GetComponentInChildren<TMPro.TextMeshPro>();
        Assert.AreEqual("2", label.text);
        Assert.Greater(label.textInfo.meshInfo[0].vertexCount, 0, "初回シークでも残数の描画メッシュを生成する");
        Assert.AreEqual(0, note.transform.localPosition.z, .001);
        preview.Tick(document, 4.95);
        Assert.AreEqual(1, note.RemainingCuts);
        Assert.AreEqual(-.5f, note.transform.localPosition.z, .001);
        preview.Tick(document, 4.95);
        Assert.AreEqual(1, note.RemainingCuts);
        preview.Tick(document, 3.25);
        Assert.AreEqual(3, note.RemainingCuts);
        Assert.AreEqual(10, note.transform.localPosition.z, .001);
        preview.Tick(document, 5.8);
        Assert.AreEqual(1, preview.VisibleCutCount, "最後の振り(5.65秒)のあとは2片が飛んでいる");
        preview.Tick(document, 6.8);
        Assert.AreEqual(0, preview.VisibleNoteCount, "2片の寿命(本編と同じ1.1秒)が過ぎたら消える");
    }

    [Test]
    public void PreviewIsIsolatedAndDisposalReleasesOwnedResources()
    {
        var scene = SceneManager.GetActiveScene();
        bool dirty = scene.isDirty;
        preview.Tick(document, 1.25);
        var root = preview.WorldRoot;
        Assert.True(EditorSceneManager.IsPreviewScene(root.scene));
        Assert.AreNotEqual(scene, root.scene);
        Assert.AreEqual(0, root.GetComponentsInChildren<Collider>(true).Length);
        Assert.True(Notes().All(n => !n.enabled && !n.IsJudgeable));
        var materials = root.GetComponentsInChildren<Renderer>(true).Select(r => r.sharedMaterial).Where(m => m != null).Distinct().ToArray();
        var meshes = root.GetComponentsInChildren<NoteMeshBatch>().SelectMany(b => b.GetComponentsInChildren<MeshFilter>()).Select(f => f.sharedMesh).Where(m => m != null && !EditorUtility.IsPersistent(m) && (m.hideFlags & HideFlags.DontSave) != 0).ToArray();
        preview.Dispose();
        preview.Dispose();
        Assert.True(root == null);
        Assert.True(materials.All(m => m == null), "生成した素材が残っている: " + string.Join(", ", materials.Where(m => m != null).Select(m => m.name)));
        Assert.True(meshes.All(m => m == null), "結合したメッシュが残っている");
        Assert.AreEqual(dirty, scene.isDirty);
    }

    [Test]
    public void DirectionAndSimultaneousLinksFollowSeekingAndTimeEdits()
    {
        preview.Tick(document, 1.25);
        var direction = Notes().Single(n => n.RequiredDirection == CutDirection.Up);
        Assert.NotNull(direction.transform.Find("Arrow"));
        var links = preview.WorldRoot.GetComponentsInChildren<SimultaneousNoteLink>();
        Assert.AreEqual(1, links.Length);
        Assert.AreEqual(10, links[0].Line.GetPosition(0).z, .001);
        preview.Tick(document, 2);
        Assert.AreEqual(2.5f, links[0].Line.GetPosition(0).z, .001);
        document.notes[0].time = 2500;
        preview.Tick(document, 2);
        Assert.AreEqual(0, preview.WorldRoot.GetComponentsInChildren<SimultaneousNoteLink>().Length);
    }

    [Test]
    public void RendersRealFramesAndChangesWithPlayback()
    {
        string output = Environment.GetEnvironmentVariable("SABER_PREVIEW_CAPTURE");
        Texture2D first = Capture(1.25);
        Texture2D second = Capture(2.1);
        try
        {
            var pixels = first.GetPixels32();
            Assert.Greater(pixels.Count(c => c.r > c.g * 1.3f && c.r > 80), 30, "赤ノーツの描画");
            Assert.Greater(pixels.Count(c => c.b > c.r * 1.4f && c.b > 80), 30, "青ノーツの描画");
            Assert.False(first.GetPixels32().SequenceEqual(second.GetPixels32()), "再生時刻で画面が変わる");
            if (!string.IsNullOrEmpty(output))
            {
                Directory.CreateDirectory(output);
                File.WriteAllBytes(Path.Combine(output, "approaching.png"), first.EncodeToPNG());
                File.WriteAllBytes(Path.Combine(output, "near-hit.png"), second.EncodeToPNG());
                var longFrame = Capture(4.4);
                try { File.WriteAllBytes(Path.Combine(output, "long-note.png"), longFrame.EncodeToPNG()); }
                finally { Object.DestroyImmediate(longFrame); }
            }
        }
        finally { Object.DestroyImmediate(first); Object.DestroyImmediate(second); }
    }

    // ---- Perfect で切る(判定時刻ちょうどで本編と同じ2片・光の輪・ひび) ----

    CuttableNote Body(SaberHand hand) =>
        preview.WorldRoot.GetComponentsInChildren<CuttableNote>(true).Single(n => n.RequiredHand == hand);

    SlicePieceDecay[] Pieces(string name) =>
        preview.WorldRoot.GetComponentsInChildren<SlicePieceDecay>(true).Where(p => p.name == name && p.gameObject.activeSelf).ToArray();

    static int ActiveCracks(CuttableNote note) =>
        note.GetComponentsInChildren<Transform>(true).Count(t => t.name == "Crack" && t.gameObject.activeSelf);

    [Test]
    public void PerfectCutSlicesTheNoteAtTheHitTimeAndSeekingBackRestoresIt()
    {
        preview.Tick(document, 2.2);
        var red = Body(SaberHand.Right);
        Assert.True(red.gameObject.activeSelf);
        Assert.AreEqual(0, Pieces("PreviewSlice").Length);
        Assert.AreEqual(0, preview.VisibleCutCount);

        preview.Tick(document, 2.3);
        Assert.AreEqual(2, preview.VisibleCutCount, "赤と青は同時刻(2.25秒)なので両方切れる");
        Assert.False(red.gameObject.activeSelf, "切った瞬間から本体は隠す");
        var slices = Pieces("PreviewSlice");
        Assert.AreEqual(4, slices.Length);
        var redSlices = slices.Where(s => Vector3.Distance(s.transform.position, new Vector3(-2, 1, 0)) < 1.5f).ToArray();
        Assert.AreEqual(2, redSlices.Length, "赤の2片は赤の着地点の近くにある");
        Assert.Greater(Vector3.Distance(redSlices[0].transform.position, redSlices[1].transform.position), .05f, "2片は切断面から逆向きに開く");
        Assert.True(redSlices.All(s => s.GetComponent<MeshFilter>().sharedMesh.vertexCount > 0));
        Assert.True(redSlices.All(s => s.IsFlashing), "Perfect は切断片の縁が光る");
        var feedback = preview.WorldRoot.GetComponentInChildren<GameplayCutFeedback>();
        Assert.AreEqual(2, feedback.ActiveCount, "Perfect の光の輪");
        Assert.Greater(feedback.GetComponent<MeshFilter>().sharedMesh.vertexCount, 0);

        preview.Tick(document, 2.2);
        Assert.True(red.gameObject.activeSelf, "巻き戻せば本体に戻る");
        Assert.AreEqual(0, Pieces("PreviewSlice").Length);
        Assert.AreEqual(0, feedback.ActiveCount);
        preview.Tick(document, 3.4);
        Assert.True(red == null, "2片の寿命 1.1 秒が過ぎたらノーツごと消える");
        Assert.AreEqual(0, preview.VisibleCutCount);
    }

    [Test]
    public void PerfectCutLooksTheSameAfterSeekingAwayAndBack()
    {
        preview.Tick(document, 2.6);
        var before = Pieces("PreviewSlice").Select(p => (p.transform.position, p.transform.rotation)).ToArray();
        Assert.AreEqual(4, before.Length);
        preview.Tick(document, 1.25);
        preview.Tick(document, 3.0);
        preview.Tick(document, 2.6);
        var after = Pieces("PreviewSlice").Select(p => (p.transform.position, p.transform.rotation)).ToArray();
        Assert.AreEqual(before.Length, after.Length);
        for (int i = 0; i < before.Length; i++)
        {
            Assert.Less(Vector3.Distance(before[i].position, after[i].position), .0005f, "同じ時刻なら同じ場所");
            Assert.Less(Quaternion.Angle(before[i].rotation, after[i].rotation), .05f, "同じ時刻なら同じ向き");
        }
    }

    [Test]
    public void LongNoteCracksEachCutAndShattersOnTheLastOne()
    {
        preview.Tick(document, 4.3);   // 判定 4.25 秒・3回・1.4 秒のロング: 1打目のあと
        var note = Notes().Single();
        Assert.AreEqual(2, note.RemainingCuts);
        Assert.AreEqual(1, ActiveCracks(note), "1打目のひび");
        preview.Tick(document, 5.0);   // 2打目(4.95 秒)のあと
        Assert.AreEqual(2, ActiveCracks(note), "2打目でひびが増える");
        Assert.True(note.gameObject.activeSelf);
        preview.Tick(document, 5.7);   // 最後の振り(5.65 秒)のあと
        Assert.False(note.gameObject.activeSelf, "最後の振りで本体は砕ける");
        Assert.AreEqual(2, Pieces("PreviewSlice").Length);
        Assert.AreEqual(6, Pieces("PreviewDebris").Length, "ロングの最後は細片も散る(本編と同じ6つ)");
        Assert.False(preview.WorldRoot.GetComponentInChildren<TMPro.TextMeshPro>(true).gameObject.activeSelf, "残数は消える");
        preview.Tick(document, 4.3);
        Assert.True(note.gameObject.activeSelf, "巻き戻せば本体とひびに戻る");
        Assert.AreEqual(1, ActiveCracks(note));
        Assert.AreEqual(0, Pieces("PreviewDebris").Length);
    }

    [Test]
    public void TurningOffPerfectCutsKeepsTheOldFadeOut()
    {
        preview.ShowPerfectCuts = false;
        preview.Tick(document, 2.3);
        var red = Body(SaberHand.Right);
        Assert.True(red.gameObject.activeSelf);
        Assert.Less(red.transform.localScale.x, .8f, "判定時刻のあとは縮んで消える(従来)");
        Assert.AreEqual(0, Pieces("PreviewSlice").Length);
        Assert.AreEqual(0, preview.VisibleCutCount);
        Assert.AreEqual(0, ActiveCracks(red));
        preview.Tick(document, 2.4);
        Assert.AreEqual(0, Notes().Count(n => n.RequiredHand == SaberHand.Right), "0.12 秒で消える");
    }

    [Test]
    public void DisposalReleasesSliceDebrisAndAccentResources()
    {
        preview.Tick(document, 5.7);
        var pieces = preview.WorldRoot.GetComponentsInChildren<SlicePieceDecay>(true);
        Assert.AreEqual(8, pieces.Length, "2片 + 細片6つ");
        var materials = pieces.Select(p => p.GetComponent<MeshRenderer>().sharedMaterial).Where(m => m != null).Distinct().ToArray();
        var meshes = pieces.Select(p => p.GetComponent<MeshFilter>().sharedMesh)
            .Where(m => m != null && !EditorUtility.IsPersistent(m) && (m.hideFlags & HideFlags.DontSave) != 0).Distinct().ToArray();
        Assert.AreEqual(8, materials.Length, "片ごとに本体の材質を複製する");
        Assert.AreEqual(2, meshes.Length, "生成するのは2片のメッシュだけ(細片は組み込みの立方体)");
        var feedback = preview.WorldRoot.GetComponentInChildren<GameplayCutFeedback>();
        var accentMaterial = feedback.GetComponent<MeshRenderer>().sharedMaterial;
        var accentMesh = feedback.GetComponent<MeshFilter>().sharedMesh;
        Assert.NotNull(accentMaterial);
        Assert.NotNull(accentMesh);
        preview.Dispose();
        Assert.True(materials.All(m => m == null), "片の材質が残っている");
        Assert.True(meshes.All(m => m == null), "片のメッシュが残っている");
        Assert.True(accentMaterial == null && accentMesh == null, "光の輪の素材が残っている");
    }

    Texture2D Capture(double time)
    {
        preview.Tick(document, time);
        var texture = (RenderTexture)preview.Render(new Rect(0, 0, 960, 540));
        Assert.NotNull(texture);
        var previous = RenderTexture.active;
        try
        {
            RenderTexture.active = texture;
            var image = new Texture2D(texture.width, texture.height, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
            image.Apply();
            return image;
        }
        finally { RenderTexture.active = previous; }
    }

    CuttableNote[] Notes() => preview.WorldRoot.GetComponentsInChildren<CuttableNote>();
}
