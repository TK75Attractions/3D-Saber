using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

// 88: 拍の原点・変速・巻き取りと、既存の点滅上限を一緒に確かめる。
public class StageFeverBeatMotionTests
{
    [Test]
    public void MotionUsesContinuousBeatsAcrossTempoChanges()
    {
        var root = new GameObject("FeverBeatMotion");
        try
        {
            var chart = new ChartData {
                bpm = 120, beatZeroMs = 1000, offsetMs = 200,
                editorTempoMap = new List<ChartTempoPoint> {
                    new ChartTempoPoint { beat = 0, timeMs = 1000 },
                    new ChartTempoPoint { beat = 4, timeMs = 3000 },
                    new ChartTempoPoint { beat = 8, timeMs = 4000 },
                    new ChartTempoPoint { beat = 12, timeMs = 6000 }
                }
            };
            var timeline = new StagePerformanceTimeline {
                sections = new[] { new StagePerformanceTimeline.Section {
                    startSeconds = 0, endSeconds = 6.2, intensity = 1
                } }
            };
            var show = root.AddComponent<StageFeverShow>();
            show.Setup(StageTheme.PulseArray, -2.5f, timeline, chart);
            float spacing = 44f / StageFeverShow.PostsPerSide;
            Assert.AreEqual(48f, show.PostZAt(1.2, 0), 1e-4f, "OFFSET は一度だけ加える");
            Assert.AreEqual(spacing, show.PostZAt(1.2, 0) - show.PostZAt(1.7, 0), 1e-4f);
            Assert.AreEqual(spacing, show.PostZAt(3.2, 0) - show.PostZAt(3.45, 0), 1e-4f);
            Assert.AreEqual(spacing, show.PostZAt(4.2, 0) - show.PostZAt(4.7, 0), 1e-4f);
            Assert.AreEqual(show.PostZAt(3.2 - 1e-6, 0), show.PostZAt(3.2 + 1e-6, 0), 1e-3f);
            Assert.AreEqual(show.LaserSwayAt(3.2 - 1e-6, 0, 1), show.LaserSwayAt(3.2 + 1e-6, 0, 1), 1e-4f);
            Assert.AreEqual(.2f, show.LaserSwayAt(1.7, 0, 1), 1e-4f);
            Assert.AreEqual(-.2f, show.LaserSwayAt(2.7, 0, 1), 1e-4f);
            Assert.AreEqual(show.LaserSwayAt(1.2, 1, 1), show.LaserSwayAt(3.2, 1, 1), 1e-4f);
            Assert.AreEqual(show.LaserSwayAt(3.2, 1, 1), show.LaserSwayAt(4.2, 1, 1), 1e-4f, "変速しても4拍で1往復");

            var retracting = show.LaserAt(6.4);
            Assert.AreEqual(6.2, retracting.poseTime);
            Assert.AreEqual(show.LaserSwayAt(6.2, 1, -1), show.LaserSwayAt(retracting.poseTime, 1, -1));
            var flashes = show.FlashBeatTimes();
            Assert.Greater(flashes.Length, 2);
            for (int i = 1; i < flashes.Length; i++)
                Assert.GreaterOrEqual(flashes[i] - flashes[i - 1], StageFeverShow.CueSpacing - 1e-6);
        }
        finally { Object.DestroyImmediate(root); }
    }

    [Test]
    public void MissingTempoMapUsesBpmAndBeatOrigin()
    {
        var root = new GameObject("FeverFixedTempo");
        try
        {
            var show = root.AddComponent<StageFeverShow>();
            show.Setup(StageTheme.PulseArray, -2.5f, null,
                new ChartData { bpm = 60, beatZeroMs = 500, offsetMs = 250 });
            Assert.AreEqual(48f, show.PostZAt(.75, 0), 1e-4f);
            Assert.AreEqual(44f / StageFeverShow.PostsPerSide,
                show.PostZAt(.75, 0) - show.PostZAt(1.75, 0), 1e-4f);
            Assert.AreEqual(show.LaserSwayAt(.75, 2, -1), show.LaserSwayAt(4.75, 2, -1), 1e-4f);
        }
        finally { Object.DestroyImmediate(root); }
    }
}
