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
    [Test] public void TimingRequestKeepsEveryArrivalAndDescribesAudioOffsetConversion()
    {
        string before = SaberChartUtility.ToJson(chart);
        var comment = SaberPlaybackReviewData.TimingComment(chart, new[] { 2.125f, .75f, 1.5f, 1.5f }, 10);
        CollectionAssert.AreEqual(new[] { .75f, 1.5f, 2.125f }, comment.requestedNoteSeconds);
        Assert.AreEqual(.5f, comment.fromSeconds); Assert.AreEqual(2.375f, comment.toSeconds);
        Assert.IsFalse(comment.hasPoint); Assert.IsEmpty(comment.noteIndices);
        request.comments.Clear(); request.comments.Add(comment);
        var roundTrip = JsonUtility.FromJson<SaberReviewRequest>(JsonUtility.ToJson(request));
        SaberPlaybackReviewData.ValidateComments(chart, roundTrip.comments, 10);
        CollectionAssert.AreEqual(comment.requestedNoteSeconds, roundTrip.comments[0].requestedNoteSeconds);
        string prompt = SaberPlaybackReviewData.Prompt(roundTrip, "review");
        Assert.That(prompt, Does.Contain("0.750, 1.500, 2.125"));
        Assert.That(prompt, Does.Contain("秒 × 1000 − chart.offsetMs"));
        Assert.That(prompt, Does.Contain("クリック位置は配置座標の指定ではありません"));
        Assert.That(prompt, Does.Not.Contain("区間と画像から対象を確認"));
        Assert.AreEqual(before, SaberChartUtility.ToJson(chart));
        var patch = Patch(comment.requestedNoteSeconds.Select(t => new SaberReviewOperation {
            action = "add", note = new SaberChartNote { time = t * 1000 - chart.offsetMs, x = -1, color = "blue" }
        }).ToArray());
        var result = SaberPlaybackReviewData.Apply(request, chart, patch, 10);
        Assert.AreEqual(chart.notes.Count + 3, result.notes.Count);
        foreach (float time in comment.requestedNoteSeconds)
            Assert.IsTrue(result.notes.Any(n => Mathf.Abs((n.time + result.offsetMs) / 1000 - time) < .0001f));
    }
    [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)] [TestCase(-1f)] [TestCase(.1f)] [TestCase(11f)]
    public void TimingRequestsRejectUnrepresentableArrivalTimes(float time)
    {
        Assert.Throws<InvalidOperationException>(() => SaberPlaybackReviewData.TimingComment(chart, new[] { time }, 10));
        request.comments[0].requestedNoteSeconds = new[] { time };
        Assert.Throws<InvalidOperationException>(() => SaberPlaybackReviewData.ValidateComments(chart, request.comments, 10));
    }
    [Test] public void TimingRequestsCannotHideArrivalsOutsideTheirCommentRange()
    {
        var comment = SaberPlaybackReviewData.TimingComment(chart, new[] { 1f, 3f }, 10);
        comment.toSeconds = 2;
        Assert.Throws<InvalidOperationException>(() => SaberPlaybackReviewData.ValidateComments(chart, new System.Collections.Generic.List<SaberReviewComment> { comment }, 10));
        Assert.Throws<InvalidOperationException>(() => SaberPlaybackReviewData.TimingComment(chart, Array.Empty<float>(), 10));
    }
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
    [Test] public void BeatSnapUsesOffsetBeatOriginAndVariableTempoWithoutChangingNotes()
    {
        chart.beatZeroMs = 100;
        string before = SaberChartUtility.ToJson(chart);
        Assert.AreEqual(.85f, SaberPlaybackReviewData.SnapAudioTime(chart, .83f, 1, 10), .0001f);
        Assert.AreEqual(1.1f, SaberPlaybackReviewData.SnapAudioTime(chart, 1.03f, 2, 10), .0001f);
        Assert.AreEqual(before, SaberChartUtility.ToJson(chart));
        chart.tempoMap.Add(new SaberChartTempoPoint { beat = 0, timeMs = 100 });
        chart.tempoMap.Add(new SaberChartTempoPoint { beat = 4, timeMs = 2100 });
        chart.tempoMap.Add(new SaberChartTempoPoint { beat = 8, timeMs = 6100 });
        Assert.AreEqual(3.35f, SaberPlaybackReviewData.SnapAudioTime(chart, 3.36f, 1, 10), .0001f);
        Assert.AreEqual(0, SaberPlaybackReviewData.SnapAudioTime(chart, -2, 4, 10));
        Assert.AreEqual(10, SaberPlaybackReviewData.SnapAudioTime(chart, 100, 4, 10));
    }
    [Test] public void TimelinePickingDistinguishesHandsAndLongBodiesAndPrefersNearbyStarts()
    {
        Assert.AreEqual(1, SaberPlaybackReviewData.PickTimelineNote(chart, 2.26f, "red", .03f));
        Assert.AreEqual(-1, SaberPlaybackReviewData.PickTimelineNote(chart, 2.26f, "blue", .03f));
        Assert.AreEqual(2, SaberPlaybackReviewData.PickTimelineNote(chart, 4, "red", .03f));
        Assert.AreEqual(-1, SaberPlaybackReviewData.PickTimelineNote(chart, 5, "red", .03f));
        chart.notes.Add(new SaberChartNote { time = 4000, color = "red" });
        Assert.AreEqual(3, SaberPlaybackReviewData.PickTimelineNote(chart, 4.25f, "red", .03f));
    }
    [Test] public void ChangeNavigationIncludesBothOldAndNewTimesAndLongEnds()
    {
        var moved = chart.notes[2].Clone(); moved.time = 5000; moved.lengthMs = 2000;
        var range = SaberPlaybackReviewData.ChangeRange(chart, new SaberReviewOperation { action = "replace", index = 2, note = moved });
        Assert.AreEqual(3.25f, range.x); Assert.AreEqual(7.25f, range.y);
        range = SaberPlaybackReviewData.ChangeRange(chart, new SaberReviewOperation { action = "delete", index = 2 });
        Assert.AreEqual(4.65f, range.y, .0001f);
        range = SaberPlaybackReviewData.ChangeRange(chart, new SaberReviewOperation { action = "add", note = moved });
        Assert.AreEqual(5.25f, range.x); Assert.AreEqual(7.25f, range.y);
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
