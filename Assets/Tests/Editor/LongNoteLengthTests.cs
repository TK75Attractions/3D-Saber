using System.Collections.Generic;
using NUnit.Framework;
using Saber.ChartEditor;
using UnityEngine;

// ロングノーツの「長さ直接指定(lengthMs)」の検証。
// ゲーム側(NoteSpawner の滞留・判定窓)とエディタ側(実効長さ・正規化)の両方をカバーする。
public class LongNoteLengthTests
{
    private readonly List<GameObject> created = new List<GameObject>();

    [TearDown]
    public void TearDown()
    {
        foreach (var go in created)
        {
            if (go != null) Object.DestroyImmediate(go);
        }
        created.Clear();
    }

    private NoteSpawner MakeSpawner()
    {
        var go = new GameObject("spawner");
        created.Add(go);
        return go.AddComponent<NoteSpawner>(); // 既定: judgeWindow=0.27, secondsPerLongCut=0.7, longLingerDriftZ=1
    }

    private CuttableNote MakeNote(int count, float overrideLingerSeconds = 0f)
    {
        var go = new GameObject("note");
        created.Add(go);
        var note = go.AddComponent<CuttableNote>();
        note.RequiredCutCount = count;
        note.RemainingCuts = count;
        note.OverrideLingerSeconds = overrideLingerSeconds;
        return note;
    }

    // ---- ゲーム側: 滞留時間 ----

    [Test]
    public void LingerSecondsFor_Legacy_UsesCountTimesSecondsPerCut()
    {
        var spawner = MakeSpawner();
        Assert.AreEqual(1.4f, spawner.LingerSecondsFor(MakeNote(3)), 1e-4f, "(3-1)×0.7s");
        Assert.AreEqual(0f, spawner.LingerSecondsFor(MakeNote(1)), 1e-4f, "タップは滞留しない");
    }

    [Test]
    public void LingerSecondsFor_Override_TakesPriority()
    {
        var spawner = MakeSpawner();
        Assert.AreEqual(2.5f, spawner.LingerSecondsFor(MakeNote(3, 2.5f)), 1e-4f, "lengthMs 指定が優先");
        Assert.AreEqual(0.3f, spawner.LingerSecondsFor(MakeNote(5, 0.3f)), 1e-4f, "回数より短くもできる");
    }

    [Test]
    public void LateWindowFor_UsesEffectiveLinger()
    {
        var spawner = MakeSpawner();
        float baseWindow = spawner.LateWindowFor(MakeNote(1));
        Assert.AreEqual(baseWindow + 1.4f, spawner.LateWindowFor(MakeNote(3)), 1e-4f, "自動長さ");
        Assert.AreEqual(baseWindow + 2.0f, spawner.LateWindowFor(MakeNote(3, 2.0f)), 1e-4f, "指定長さ");
    }

    [Test]
    public void ComputeNoteZ_LingersForOverrideDuration()
    {
        var spawner = MakeSpawner();
        var note = MakeNote(2, 2.0f); // 滞留2秒(自動なら0.7秒のところ)
        float speed = 10f;

        // 滞留中(1秒経過=進捗50%): judgeZ から longLingerDriftZ×0.5 だけ下がる
        float during = spawner.ComputeNoteZ(note, -1.0, speed);
        float atEnd = spawner.ComputeNoteZ(note, -2.0, speed);
        float after = spawner.ComputeNoteZ(note, -2.5, speed);

        float legacyDuring = spawner.ComputeNoteZ(MakeNote(2), -1.0, speed);

        Assert.AreEqual(atEnd, during * 2f, 1e-3f, "滞留は線形(0.5→1.0)");
        Assert.Less(after, atEnd, "滞留終了後は流れ去る");
        Assert.Less(legacyDuring, during, "自動(0.7s)ならとっくに滞留を終えて流れている");
    }

    // ---- エディタ側: 実効長さと正規化 ----

    [Test]
    public void EffectiveLongLengthMs_AutoAndManual()
    {
        Assert.AreEqual(0f, SaberChartUtility.EffectiveLongLengthMs(
            new SaberChartNote { type = SaberChartUtility.TypeTap, count = 1 }), 1e-3f);
        Assert.AreEqual(1400f, SaberChartUtility.EffectiveLongLengthMs(
            new SaberChartNote { type = SaberChartUtility.TypeLong, count = 3 }), 1e-3f, "自動 = (3-1)×700ms");
        Assert.AreEqual(2000f, SaberChartUtility.EffectiveLongLengthMs(
            new SaberChartNote { type = SaberChartUtility.TypeLong, count = 3, lengthMs = 2000f }), 1e-3f);
    }

    [Test]
    public void Normalize_ResetsLengthForNonLong_AndClampsInvalid()
    {
        var document = new SaberChartDocument
        {
            notes =
            {
                new SaberChartNote { time = 1000f, type = "tap", count = 1, lengthMs = 1234f },
                new SaberChartNote { time = 2000f, type = "long", count = 3, lengthMs = -50f },
                new SaberChartNote { time = 3000f, type = "long", count = 2, lengthMs = 1800f },
            },
        };

        SaberChartUtility.Normalize(document);

        Assert.AreEqual(0f, document.notes[0].lengthMs, 1e-3f, "tap の長さ指定は破棄");
        Assert.AreEqual(0f, document.notes[1].lengthMs, 1e-3f, "負値は自動(0)へ");
        Assert.AreEqual(1800f, document.notes[2].lengthMs, 1e-3f, "正しい指定は維持");
    }

    [Test]
    public void JsonRoundTrip_PreservesLengthMs()
    {
        var source = new SaberChartDocument
        {
            notes =
            {
                new SaberChartNote
                {
                    time = 3000f,
                    type = SaberChartUtility.TypeLong,
                    count = 2,
                    lengthMs = 2400f,
                },
            },
        };

        SaberChartNote restored = SaberChartUtility.FromJson(
            SaberChartUtility.ToJson(source, false)).notes[0];

        Assert.AreEqual(2400f, restored.lengthMs, 1e-3f);
    }

    // ---- 本編スキーマ(ChartData)も lengthMs を受け取れる ----

    [Test]
    public void ChartData_ParsesLengthMs()
    {
        string json = "{ \"bpm\": 100, \"notes\": [ " +
            "{ \"time\": 1000, \"type\": \"long\", \"count\": 2, \"lengthMs\": 1800 }, " +
            "{ \"time\": 2000, \"type\": \"tap\", \"count\": 1 } ] }";
        ChartData chart = JsonUtility.FromJson<ChartData>(json);
        Assert.AreEqual(1800f, chart.notes[0].lengthMs, 1e-3f);
        Assert.AreEqual(0f, chart.notes[1].lengthMs, 1e-3f, "未指定は 0(自動)");
    }
}
