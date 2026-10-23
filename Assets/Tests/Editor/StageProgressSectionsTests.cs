using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

public class StageProgressSectionsTests
{
    static StagePerformanceTimeline.Section Section(double start, double end, float intensity = 1f)
        => new StagePerformanceTimeline.Section { startSeconds = start, endSeconds = end, intensity = intensity };

    [Test]
    public void ClipsBothEndsAndMergesOverlapsWithoutChangingTheSource()
    {
        var source = new[] { Section(90, 130), Section(18, 25), Section(8, 12, .65f),
            Section(11, 20), Section(25, 30), Section(31, 35, .649f), Section(0, 9), Section(120, 140) };
        var timeline = new StagePerformanceTimeline { sections = source };
        var ranges = SongProgressSections.RangesFor(timeline, 10, 110);
        Assert.AreEqual(2, ranges.Length);
        Assert.AreEqual(0f, ranges[0].x); Assert.AreEqual(.2f, ranges[0].y, 1e-6f);
        Assert.AreEqual(.8f, ranges[1].x, 1e-6f); Assert.AreEqual(1f, ranges[1].y);
        Assert.AreSame(source, timeline.sections);
        Assert.AreEqual(90, source[0].startSeconds);
        Assert.AreEqual(130, source[0].endSeconds);
    }

    [Test]
    public void UsesResolvedPlayRangeAndRejectsInvalidSections()
    {
        var timeline = new StagePerformanceTimeline {
            playStartSeconds = 5, musicEndSeconds = 105,
            sections = new[] { Section(0, 5), Section(5, 55, .65f), Section(105, 120),
                null, Section(-1, 10), Section(30, 20), Section(10, 20, float.NaN),
                Section(10, 20, float.PositiveInfinity), Section(double.NaN, 20),
                Section(10, double.PositiveInfinity) }
        };
        var range = timeline.ResolvePlayRange(120, true);
        var marks = SongProgressSections.RangesFor(timeline, range.Start, range.End);
        Assert.AreEqual(1, marks.Length);
        Assert.AreEqual(new Vector2(0f, .5f), marks[0]);
        range = timeline.ResolvePlayRange(120, false);
        marks = SongProgressSections.RangesFor(timeline, range.Start, range.End);
        Assert.AreEqual(2, marks.Length);
        Assert.AreEqual(55f / 120f, marks[0].y, 1e-6f);
        Assert.AreEqual(105f / 120f, marks[1].x, 1e-6f);
        Assert.AreEqual(1f, marks[1].y);
        Assert.IsEmpty(SongProgressSections.RangesFor(timeline, 5, 5));
        Assert.IsEmpty(SongProgressSections.RangesFor(timeline, 10, 5));
        Assert.IsEmpty(SongProgressSections.RangesFor(timeline, double.NaN, 120));
        Assert.IsEmpty(SongProgressSections.RangesFor(timeline, 0, double.PositiveInfinity));
        Assert.IsEmpty(SongProgressSections.RangesFor(null, 0, 120));
        Assert.IsEmpty(SongProgressSections.RangesFor(new StagePerformanceTimeline { sections = null }, 0, 120));
    }

    [Test]
    public void MeshTracksRangeChangesAndProjectorMarksAreOpaque()
    {
        bool projector = DisplaySettings.ProjectorMode;
        var go = new GameObject("ProgressSectionsTest", typeof(RectTransform), typeof(SongProgressSections));
        try
        {
            var graphic = go.GetComponent<SongProgressSections>();
            graphic.rectTransform.pivot = Vector2.zero;
            graphic.rectTransform.sizeDelta = new Vector2(100, 4);
            graphic.SetTimeline(new StagePerformanceTimeline { sections = new[] { Section(20, 40) } }, 0, 100);
            AssertSpan(graphic, 20, 40);
            graphic.SetRange(10, 60);
            AssertSpan(graphic, 20, 60);
            DisplaySettings.SetProjectorModeForTest(false); graphic.RefreshStyle();
            Assert.AreEqual(.45f, graphic.color.a);
            DisplaySettings.SetProjectorModeForTest(true); graphic.RefreshStyle();
            Assert.AreEqual(1f, graphic.color.a);
            Assert.AreEqual(216f / 255f, graphic.color.r);
            graphic.SetTimeline(null, 0, 100);
            using (var vh = Mesh(graphic)) Assert.AreEqual(0, vh.currentVertCount);
        }
        finally { Object.DestroyImmediate(go); DisplaySettings.SetProjectorModeForTest(projector); }
    }

    static VertexHelper Mesh(SongProgressSections graphic)
    {
        var vh = new VertexHelper();
        typeof(SongProgressSections).GetMethod("OnPopulateMesh", BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { typeof(VertexHelper) }, null)
            .Invoke(graphic, new object[] { vh });
        return vh;
    }

    static void AssertSpan(SongProgressSections graphic, float left, float right)
    {
        using (var vh = Mesh(graphic))
        {
            Assert.AreEqual(4, vh.currentVertCount);
            var vertex = new UIVertex();
            // 位置は float の計算なので、ぴったりではなく 0.001 まで比べる。
            vh.PopulateUIVertex(ref vertex, 0);
            Assert.That(Vector3.Distance(new Vector3(left, 0, 0), vertex.position), Is.LessThan(.001f), vertex.position.ToString("F4"));
            vh.PopulateUIVertex(ref vertex, 2);
            Assert.That(Vector3.Distance(new Vector3(right, 4, 0), vertex.position), Is.LessThan(.001f), vertex.position.ToString("F4"));
        }
    }
}
