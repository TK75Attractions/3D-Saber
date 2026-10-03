using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

// 実際のCut経路で方向・蓄積・完走・リセットを確認する。判定や曲時計には演出を持ち込まない。
public class CutFeelTests
{
    readonly List<Object> objects = new List<Object>();
    bool reduced;

    [SetUp] public void Setup() { reduced = DisplaySettings.ReducedEffects; DisplaySettings.SetReducedEffectsForTest(false); }
    [TearDown] public void Cleanup()
    {
        foreach (var piece in Object.FindObjectsByType<SlicePieceDecay>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (piece.name.StartsWith("CutFeelProbe")) Object.DestroyImmediate(piece.gameObject);
        foreach (var item in objects) if (item != null) Object.DestroyImmediate(item);
        objects.Clear(); DisplaySettings.SetReducedEffectsForTest(reduced);
    }

    CuttableNote Note(int count = 1)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube); go.name = "CutFeelProbe"; objects.Add(go);
        var note = go.AddComponent<CuttableNote>(); note.RequiredCutCount = note.RemainingCuts = count;
        return note;
    }

    [TestCase(1, 0)] [TestCase(-1, 0)] [TestCase(0, 1)] [TestCase(0, -1)]
    [TestCase(1, 1)] [TestCase(-1, 1)] [TestCase(1, -1)] [TestCase(-1, -1)]
    public void BothSlicesTravelWithSwingAndOpenAcrossItsPlane(float x, float y)
    {
        var note = Note(); Vector3 direction = new Vector3(x, y, 0).normalized;
        note.Cut(Vector3.zero, direction * 8);
        var pieces = Object.FindObjectsByType<SlicePieceDecay>(FindObjectsSortMode.None)
            .Where(p => p.name == "CutFeelProbe_piece").ToArray();
        Assert.AreEqual(2, pieces.Length);
        foreach (var piece in pieces)
        {
            piece.Step(.04f);
            Assert.Greater(Vector3.Dot(piece.transform.position, direction), .1f, "二片とも斬った先へ抜ける");
        }
        Vector3 normal = Vector3.Cross(direction, Vector3.forward);
        Assert.Less(Vector3.Dot(pieces[0].transform.position, normal) * Vector3.Dot(pieces[1].transform.position, normal), 0,
            "切断面の両側に開く");
    }

    [Test] public void LongCracksGrowThroughAcceptedCutsAndResetForReuse()
    {
        var note = Note(4);
        note.RequiredHand = SaberHand.Right;
        note.Cut(Vector3.zero, Vector3.right * 8, CutDirection.None, SaberHand.Left);
        Assert.AreEqual(0, note.transform.childCount, "違う手で傷を付けない");
        note.Cut(Vector3.zero, Vector3.right * 8, CutDirection.None, SaberHand.Right);
        var first = note.transform.Find("Crack"); Vector3 early = first.localScale;
        note.Cut(Vector3.zero, Vector3.right * 8, CutDirection.None, SaberHand.Right);
        note.Cut(Vector3.zero, Vector3.right * 8, CutDirection.None, SaberHand.Right);
        Assert.Greater(first.localScale.x, early.x); Assert.Greater(first.localScale.y, early.y);
        Assert.AreEqual(1, note.RemainingCuts); Assert.False(note.IsCut);
        Assert.Greater(Mathf.Abs(Vector3.Dot(first.up, Vector3.right)), .9f, "傷も振り方向に沿う");
        typeof(CuttableNote).GetMethod("ResetForSpawn", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(note, null);
        Assert.True(note.GetComponentsInChildren<Transform>(true).Where(t => t.name == "Crack").All(t => !t.gameObject.activeSelf));
    }

    [Test] public void LongFinalHitReleasesDirectionalDebrisOnlyAfterCompletion()
    {
        var note = Note(3);
        note.Cut(Vector3.zero, Vector3.up * 8); note.Cut(Vector3.zero, Vector3.up * 8);
        Assert.AreEqual(0, Object.FindObjectsByType<SlicePieceDecay>(FindObjectsSortMode.None).Count(p => p.name.StartsWith("CutFeelProbe")));
        note.Cut(Vector3.zero, Vector3.up * 8);
        var pieces = Object.FindObjectsByType<SlicePieceDecay>(FindObjectsSortMode.None).Where(p => p.name.StartsWith("CutFeelProbe")).ToArray();
        Assert.AreEqual(8, pieces.Length);
        foreach (var piece in pieces)
        {
            var before = piece.transform.position; piece.Step(.04f);
            Assert.Greater(Vector3.Dot(piece.transform.position - before, Vector3.up), 0);
        }
    }

    [TestCase(1, 0)] [TestCase(-1, 0)] [TestCase(0, 1)] [TestCase(0, -1)]
    public void SlashMeshPointsAndTravelsInTheCutDirection(float x, float y)
    {
        var root = new GameObject("CutFeelProbeOwner"); objects.Add(root);
        var owner = root.AddComponent<NoteSpawner>();
        var feedback = GameplayCutFeedback.Create(owner);
        var score = root.AddComponent<ScoreManager>(); score.Bind(owner);
        var note = Note(); feedback.Track(note);
        typeof(ScoreManager).GetMethod("HandleSpawned", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(score, new object[] { note });
        Vector3 direction = new Vector3(x, y, 0);
        note.Cut(Vector3.zero, direction * 8);
        var mesh = feedback.GetComponent<MeshFilter>().sharedMesh;
        Vector3[] before = mesh.vertices;
        feedback.Tick(.04f); Vector3[] after = mesh.vertices;
        Vector3 centerBefore = (before[0] + before[1] + before[2] + before[3]) * .25f;
        Vector3 centerAfter = (after[0] + after[1] + after[2] + after[3]) * .25f;
        Assert.Greater(Vector3.Dot(centerAfter - centerBefore, direction), .3f);
        Assert.Less((after[1] - after[2]).magnitude, (after[0] - after[3]).magnitude, "進行方向の先端が細い");
        feedback.Tick(.3f); Assert.AreEqual(0, feedback.ActiveCount);
    }

    [Test] public void LongPitchBuildsWithinFourSemitonesAndRestartsForTheNextNote()
    {
        float previous = 0;
        for (int i = 0; i < 7; i++)
        {
            float pitch = LongNoteCutSfx.PitchForCut(i, 8);
            Assert.Greater(pitch, previous); previous = pitch;
        }
        Assert.That(previous, Is.EqualTo(Mathf.Pow(2, 4f / 12f)).Within(.0001f));
        Assert.AreEqual(1, LongNoteCutSfx.PitchForCut(0, 8));
        Assert.AreEqual(1, LongNoteCutSfx.PitchForCut(0, 2));
        Assert.LessOrEqual(LongNoteCutSfx.PitchForCut(999, 4), 1.26f);
    }
}
