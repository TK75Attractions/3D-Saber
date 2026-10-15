using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Saber.ChartEditor;
using UnityEngine;

public class PlaybackReviewTests
{
    SaberChartDocument chart;
    SaberReviewRequest request;
    [SetUp] public void Setup()
    {
        chart = new SaberChartDocument { offsetMs = 250, displayLevel = 10 };
        chart.extraFields.Add(new SaberChartExtraField { key = "_comment", json = "\"preserved\"" });
        chart.notes.Add(new SaberChartNote { time = 1000, color = "blue", x = -1 });
        chart.notes.Add(new SaberChartNote { time = 2000, color = "red", x = 1 });
        chart.notes.Add(new SaberChartNote { time = 3000, type = "long", count = 3, lengthMs = 1400 });
        request = SaberPlaybackReviewData.Request(chart, "RushE", "e", "Assets/StreamingAssets/Songs/RushE/chart_e.json", new[] {
            new SaberReviewComment { fromSeconds = 1, toSeconds = 2.5f, instruction = "赤を右へ", noteIndices = new[] { 1 }, hasPoint = true, screenPoint = new Vector2(.7f,.3f) }
        });
    }
    SaberReviewPatch Patch(params SaberReviewOperation[] operations) => new SaberReviewPatch { songId = "RushE", difficulty = "e", baseHash = request.baseHash, operations = operations.ToList() };
    [Test] public void RangeUsesAudioClockOffsetAndIncludesLongTail()
    {
        CollectionAssert.AreEqual(new[] { 0 }, SaberPlaybackReviewData.NotesInRange(chart, 1.2f, 1.3f));
        CollectionAssert.AreEqual(new[] { 2 }, SaberPlaybackReviewData.NotesInRange(chart, 4.4f, 4.6f));
        Assert.IsEmpty(SaberPlaybackReviewData.NotesInRange(chart, 0, 1));
    }
    [Test] public void NoteNavigationSkipsSimultaneousNotesAndUsesAudioOffset()
    {
        chart.notes.Add(new SaberChartNote { time = 2000, color = "blue" });
        Assert.AreEqual(1.25f, SaberPlaybackReviewData.AdjacentNoteTime(chart, 0, 1));
        Assert.AreEqual(3.25f, SaberPlaybackReviewData.AdjacentNoteTime(chart, 2.25f, 1));
        Assert.AreEqual(1.25f, SaberPlaybackReviewData.AdjacentNoteTime(chart, 2.25f, -1));
        Assert.AreEqual(-1, SaberPlaybackReviewData.AdjacentNoteTime(chart, 0, -1));
        Assert.AreEqual(-1, SaberPlaybackReviewData.AdjacentNoteTime(chart, 4, 1));
    }
    [Test] public void ReviewValidationRejectsInvalidTimesTargetsAndDuplicateImageIds()
    {
        var c = request.comments[0];
        SaberPlaybackReviewData.ValidateComments(chart, request.comments, 10);
        c.frameSeconds = float.NaN;
        Assert.Throws<InvalidOperationException>(() => SaberPlaybackReviewData.ValidateComments(chart, request.comments, 10));
        c.frameSeconds = 1; c.noteIndices = new[] { -1 };
        Assert.Throws<InvalidOperationException>(() => SaberPlaybackReviewData.ValidateComments(chart, request.comments, 10));
        c.noteIndices = new[] { 0 }; c.toSeconds = 11;
        Assert.Throws<InvalidOperationException>(() => SaberPlaybackReviewData.ValidateComments(chart, request.comments, 10));
        c.toSeconds = 2; request.comments.Add(c);
        Assert.Throws<InvalidOperationException>(() => SaberPlaybackReviewData.ValidateComments(chart, request.comments, 10));
    }
    [Test] public void PromptOrdersReportsAndLinksEachExactFrame()
    {
        var early = new SaberReviewComment { fromSeconds = .2f, toSeconds = .4f, frameSeconds = .3f, instruction = "先頭を確認" };
        request.comments.Add(early);
        string prompt = SaberPlaybackReviewData.Prompt(request, "test-folder");
        Assert.Less(prompt.IndexOf("先頭を確認", StringComparison.Ordinal), prompt.IndexOf("赤を右へ", StringComparison.Ordinal));
        Assert.That(prompt, Does.Contain("frame-" + early.id + ".png (0.300秒)"));
        Assert.That(prompt, Does.Contain("個別ノーツの選択なし"));
        Assert.That(prompt, Does.Contain("曲: RushE / 難易度: e / 指摘: 2件"));
    }
    [Test] public void EditsUseOriginalIndicesAndPreserveOtherNotesAndMetadata()
    {
        string before = SaberChartUtility.ToJson(chart); var replacement = chart.notes[1].Clone(); replacement.x = 2;
        var result = SaberPlaybackReviewData.Apply(request, chart, Patch(
            new SaberReviewOperation { action = "delete", index = 0 },
            new SaberReviewOperation { action = "replace", index = 1, note = replacement },
            new SaberReviewOperation { action = "add", note = new SaberChartNote { time = 1500, x = -.5f, color = "blue" } }), 10);
        Assert.AreEqual(before, SaberChartUtility.ToJson(chart)); Assert.AreEqual(3, result.notes.Count);
        Assert.AreEqual(1500, result.notes[0].time); Assert.AreEqual(2, result.notes[1].x);
        Assert.AreEqual(3, result.notes[2].count); Assert.AreEqual(10, result.displayLevel);
        Assert.AreEqual("\"preserved\"", result.extraFields.Single().json); Assert.AreEqual(250, result.offsetMs);
    }
    [Test] public void StaleOrWrongTargetRepliesAreRejected()
    {
        var patch = Patch(new SaberReviewOperation { action = "delete", index = 0 });
        patch.songId = "Other"; Assert.Throws<InvalidOperationException>(() => SaberPlaybackReviewData.Apply(request, chart, patch, 10));
        patch.songId = "RushE"; patch.difficulty = "hard"; Assert.Throws<InvalidOperationException>(() => SaberPlaybackReviewData.Apply(request, chart, patch, 10));
        patch.difficulty = "e"; chart.notes[0].x = -2; Assert.Throws<InvalidOperationException>(() => SaberPlaybackReviewData.Apply(request, chart, patch, 10));
    }
    [TestCase(-1)] [TestCase(3)] public void OutOfRangeIndicesAreRejected(int index)
    { Assert.Throws<InvalidOperationException>(() => SaberPlaybackReviewData.Apply(request, chart, Patch(new SaberReviewOperation { action = "delete", index = index }), 10)); }
    [Test] public void DuplicateAndUnknownOperationsAreRejectedAtomically()
    {
        string before = SaberChartUtility.ToJson(chart);
        Assert.Throws<InvalidOperationException>(() => SaberPlaybackReviewData.Apply(request, chart, Patch(new SaberReviewOperation { action = "delete", index = 0 }, new SaberReviewOperation { action = "delete", index = 0 }), 10));
        Assert.Throws<InvalidOperationException>(() => SaberPlaybackReviewData.Apply(request, chart, Patch(new SaberReviewOperation { action = "execute", index = 0 }), 10));
        Assert.AreEqual(before, SaberChartUtility.ToJson(chart));
    }
    [Test] public void InvalidNotesAndLongsPastAudioEndAreRejected()
    {
        var note = chart.notes[2].Clone(); note.lengthMs = 10000;
        Assert.Throws<InvalidOperationException>(() => SaberPlaybackReviewData.Apply(request, chart, Patch(new SaberReviewOperation { action = "replace", index = 2, note = note }), 10));
        note.lengthMs = 500; note.x = float.NaN;
        Assert.Throws<InvalidOperationException>(() => SaberPlaybackReviewData.Apply(request, chart, Patch(new SaberReviewOperation { action = "replace", index = 2, note = note }), 10));
    }
    [Test] public void RequestSnapshotAndExportRoundTripKeepPreciseTargets()
    {
        chart.notes[1].x = 2; Assert.AreEqual(1, request.chart.notes[1].x);
        string directory = Path.Combine(Path.GetTempPath(), "SaberReviewTest-" + Guid.NewGuid().ToString("N"));
        try
        {
            SaberPlaybackReviewData.Export(directory, request);
            var saved = JsonUtility.FromJson<SaberReviewRequest>(File.ReadAllText(Path.Combine(directory, "request.json")));
            Assert.AreEqual(request.baseHash, SaberPlaybackReviewData.ChartHash(saved.chart));
            Assert.AreEqual(.7f, saved.comments[0].screenPoint.x); CollectionAssert.AreEqual(new[] { 1 }, saved.comments[0].noteIndices);
            Assert.That(File.ReadAllText(Path.Combine(directory, "prompt.txt")), Does.Contain("ノーツ[1]"));
            Assert.IsFalse(Directory.GetFiles(directory).Any(f => f.EndsWith(".wav")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Test] public void SimultaneousPositionsRemainCanonicalForTheNextRequest()
    {
        chart.notes[1].time = 1000;
        request = SaberPlaybackReviewData.Request(chart, "RushE", "e", "chart_e.json", Array.Empty<SaberReviewComment>());
        var moved = chart.notes[0].Clone(); moved.x = 2;
        var result = SaberPlaybackReviewData.Apply(request, chart, Patch(new SaberReviewOperation { action = "replace", index = 0, note = moved }), 10);
        Assert.AreEqual(1, result.notes[0].x); Assert.AreEqual(2, result.notes[1].x);
        var next = SaberPlaybackReviewData.Request(result, "RushE", "e", "chart_e.json", new[] { new SaberReviewComment { noteIndices = new[] { 1 } } });
        Assert.AreEqual(result.notes[1].x, next.chart.notes[next.comments[0].noteIndices[0]].x);
    }
}
