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
        Assert.AreEqual(0, preview.VisibleNoteCount);
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
