using NUnit.Framework;
using Saber.ChartEditor;

public class SaberChartDocumentTests
{
    private const float Epsilon = 0.001f;

    [Test]
    public void JsonRoundTrip_PreservesDocumentAndNoteFields()
    {
        var source = new SaberChartDocument
        {
            bpm = 150f,
            coordScale = 0.75f,
            offsetMs = -32.5f,
            notes =
            {
                new SaberChartNote
                {
                    beat = 3.5f,
                    time = 1400f,
                    x = -1.25f,
                    y = 0.75f,
                    type = SaberChartUtility.TypeDirection,
                    color = SaberChartUtility.ColorRed,
                    direction = "upright",
                    count = 1,
                },
            },
        };

        string json = SaberChartUtility.ToJson(source, false);
        SaberChartDocument restored = SaberChartUtility.FromJson(json);

        Assert.AreEqual(150f, restored.bpm, Epsilon);
        Assert.AreEqual(0.75f, restored.coordScale, Epsilon);
        Assert.AreEqual(-32.5f, restored.offsetMs, Epsilon);
        Assert.AreEqual(1, restored.notes.Count);

        SaberChartNote note = restored.notes[0];
        Assert.AreEqual(3.5f, note.beat, Epsilon);
        Assert.AreEqual(1400f, note.time, Epsilon);
        Assert.AreEqual(-1.25f, note.x, Epsilon);
        Assert.AreEqual(0.75f, note.y, Epsilon);
        Assert.AreEqual(SaberChartUtility.TypeDirection, note.type);
        Assert.AreEqual(SaberChartUtility.ColorRed, note.color);
        Assert.AreEqual("upright", note.direction);
        Assert.AreEqual(1, note.count);
    }

    [Test]
    public void EstimateBeatZeroMs_UsesMedianOfExistingTimeBeatDifferences()
    {
        // 実装仕様: 差のばらつきが 50ms 以内なら中央値を原点に採用する。
        // (50ms を超える不一致は beat を参考値とみなし 0 へフォールバック。別テストで確認)
        var document = new SaberChartDocument
        {
            bpm = 120f,
            notes =
            {
                // time = beat * 500ms + 約250ms(±10msの揺れ)
                new SaberChartNote { beat = 4f, time = 2240f },
                new SaberChartNote { beat = 8f, time = 4250f },
                new SaberChartNote { beat = 12f, time = 6260f },
            },
        };

        Assert.AreEqual(250f, SaberChartUtility.EstimateBeatZeroMs(document), Epsilon);
    }

    [Test]
    public void EstimateBeatZeroMs_InconsistentBeatMetadataFallsBackToSongStart()
    {
        var document = new SaberChartDocument
        {
            bpm = 120f,
            notes =
            {
                new SaberChartNote { beat = 2f, time = 3000f },
                new SaberChartNote { beat = 4f, time = 5000f },
                new SaberChartNote { beat = 6f, time = 7000f },
            },
        };

        Assert.AreEqual(0f, SaberChartUtility.EstimateBeatZeroMs(document), Epsilon);
    }

    [Test]
    public void RecalculateBeatsFromTimes_DoesNotChangeRuntimeTime()
    {
        var document = new SaberChartDocument
        {
            bpm = 120f,
            notes = { new SaberChartNote { beat = 99f, time = 2250f } },
        };

        SaberChartUtility.RecalculateBeatsFromTimes(document, 250f);

        Assert.AreEqual(4f, document.notes[0].beat, Epsilon);
        Assert.AreEqual(2250f, document.notes[0].time, Epsilon);
    }

    [Test]
    public void FromJson_InvalidJsonThrowsInsteadOfReturningEmptyChart()
    {
        Assert.Throws<System.FormatException>(() => SaberChartUtility.FromJson("{ broken json"));
    }

    [Test]
    public void ToJson_DoesNotNormalizeOrReorderSourceDocumentInPlace()
    {
        var first = new SaberChartNote { time = 2000f, type = "tap", count = 3 };
        var second = new SaberChartNote { time = 1000f, type = "tap", count = 1 };
        var document = new SaberChartDocument { notes = { first, second } };

        SaberChartUtility.ToJson(document, false);

        Assert.AreSame(first, document.notes[0]);
        Assert.AreEqual("tap", first.type);
        Assert.AreEqual(3, first.count);
    }

    [TestCase(4, 1f)]
    [TestCase(8, 0.5f)]
    [TestCase(16, 0.25f)]
    [TestCase(32, 0.125f)]
    public void SnapStep_ReturnsMusicalSubdivision(int denominator, float expectedStep)
    {
        Assert.AreEqual(expectedStep, SaberChartUtility.SnapStep(denominator), Epsilon);
    }

    [Test]
    public void QuantizeBeat_SnapsToNearestSubdivisionAndClampsBeforeStart()
    {
        Assert.AreEqual(1.125f, SaberChartUtility.QuantizeBeat(1.18f, 32), Epsilon);
        Assert.AreEqual(1.25f, SaberChartUtility.QuantizeBeat(1.19f, 32), Epsilon);
        Assert.AreEqual(0f, SaberChartUtility.QuantizeBeat(-0.3f, 16), Epsilon);
    }

    [Test]
    public void CoordinateAndLane_DefaultEightLanesRoundTrip()
    {
        for (int lane = 0; lane < SaberChartUtility.DefaultLaneCount; lane++)
        {
            float coordinate = SaberChartUtility.CoordinateForLane(
                lane,
                SaberChartUtility.DefaultLaneCount,
                SaberChartUtility.DefaultXMin,
                SaberChartUtility.DefaultXMax);

            int restoredLane = SaberChartUtility.LaneForCoordinate(
                coordinate,
                SaberChartUtility.DefaultLaneCount,
                SaberChartUtility.DefaultXMin,
                SaberChartUtility.DefaultXMax);

            Assert.AreEqual(lane, restoredLane, $"lane {lane} の往復に失敗");
        }

        Assert.AreEqual(
            SaberChartUtility.DefaultXMin,
            SaberChartUtility.CoordinateForLane(-10, 8, SaberChartUtility.DefaultXMin, SaberChartUtility.DefaultXMax),
            Epsilon);
        Assert.AreEqual(
            SaberChartUtility.DefaultXMax,
            SaberChartUtility.CoordinateForLane(99, 8, SaberChartUtility.DefaultXMin, SaberChartUtility.DefaultXMax),
            Epsilon);
    }

    [Test]
    public void JsonRoundTrip_PreservesLongColorDirectionAndCount()
    {
        var source = new SaberChartDocument
        {
            notes =
            {
                new SaberChartNote
                {
                    beat = 6f,
                    time = 3000f,
                    x = 1.5f,
                    y = -0.5f,
                    type = SaberChartUtility.TypeLong,
                    color = SaberChartUtility.ColorBlue,
                    direction = "downleft",
                    count = 7,
                },
            },
        };

        SaberChartNote restored = SaberChartUtility.FromJson(
            SaberChartUtility.ToJson(source, false)).notes[0];

        Assert.AreEqual(SaberChartUtility.TypeLong, restored.type);
        Assert.AreEqual(SaberChartUtility.ColorBlue, restored.color);
        Assert.AreEqual("downleft", restored.direction);
        Assert.AreEqual(7, restored.count);
    }

    [Test]
    public void SortNotes_OrdersByTimeThenPosition()
    {
        var late = new SaberChartNote { time = 3000f, x = 0f, y = 0f };
        var sameTimeRight = new SaberChartNote { time = 2000f, x = 1f, y = -1f };
        var sameTimeLeftHigh = new SaberChartNote { time = 2000f, x = -1f, y = 1f };
        var sameTimeLeftLow = new SaberChartNote { time = 2000f, x = -1f, y = -1f };
        var early = new SaberChartNote { time = 1000f, x = 0f, y = 0f };
        var document = new SaberChartDocument
        {
            notes = { late, sameTimeRight, sameTimeLeftHigh, early, sameTimeLeftLow },
        };

        SaberChartUtility.SortNotes(document);

        CollectionAssert.AreEqual(
            new[] { early, sameTimeLeftLow, sameTimeLeftHigh, sameTimeRight, late },
            document.notes);
    }

    [Test]
    public void HasNoteAt_DetectsDuplicateWithinToleranceAndCanIgnoreEditedNote()
    {
        var existing = new SaberChartNote { beat = 4f, x = -0.5f, y = 1.25f };
        var document = new SaberChartDocument { notes = { existing } };

        Assert.IsTrue(SaberChartUtility.HasNoteAt(document, 4f, -0.5f, 1.25f));
        Assert.IsTrue(SaberChartUtility.HasNoteAt(
            document,
            4.00005f,
            -0.50005f,
            1.25005f));
        Assert.IsFalse(SaberChartUtility.HasNoteAt(document, 4.01f, -0.5f, 1.25f));
        Assert.IsFalse(SaberChartUtility.HasNoteAt(document, 4f, -0.5f, 1.25f, existing));
    }

    [Test]
    public void ProjectRelativeAssetPath_HandlesWindowsBackslashesAndCase()
    {
        // FileUtil.GetProjectRelativePath が「\」区切りで空文字を返す不具合の回帰防止。
        const string dataPath = "C:/Proj/Assets";
        Assert.AreEqual(
            "Assets/StreamingAssets/Songs/ElDorado/audio.mp3",
            SaberChartUtility.ProjectRelativeAssetPath(
                "C:\\Proj\\Assets\\StreamingAssets\\Songs\\ElDorado\\audio.mp3", dataPath));
        Assert.AreEqual(
            "Assets/a.json",
            SaberChartUtility.ProjectRelativeAssetPath("c:/proj/assets/a.json", dataPath),
            "Windowsでは大文字小文字を区別しない");
        Assert.IsNull(
            SaberChartUtility.ProjectRelativeAssetPath("C:/Other/Assets/a.json", dataPath),
            "プロジェクト外は null");
    }

    [Test]
    public void ProjectRelativeAssetPath_WorksWithRealProjectDataPath()
    {
        // 実際の(日本語を含む)プロジェクトパスでも変換できること
        string absolute = System.IO.Path.Combine(
            UnityEngine.Application.dataPath, "StreamingAssets", "Songs", "ElDorado", "audio.mp3");
        Assert.AreEqual(
            "Assets/StreamingAssets/Songs/ElDorado/audio.mp3",
            SaberChartUtility.ProjectRelativeAssetPath(absolute));
    }

    [Test]
    public void Normalize_PreservesRuntimeMeaningOfLegacyInconsistentNotes()
    {
        var document = new SaberChartDocument
        {
            notes =
            {
                new SaberChartNote { time = 1000f, type = "tap", count = 4, direction = "none" },
                new SaberChartNote { time = 2000f, type = "tap", count = 1, direction = "up" },
            },
        };

        SaberChartUtility.Normalize(document);

        Assert.AreEqual(SaberChartUtility.TypeLong, document.notes[0].type);
        Assert.AreEqual(4, document.notes[0].count);
        Assert.AreEqual(SaberChartUtility.TypeDirection, document.notes[1].type);
        Assert.AreEqual("up", document.notes[1].direction);
    }

    [Test]
    public void FileRoundTrip_KeepsNegativeTimeAndBeatWithPositiveOffset()
    {
        // スマホ版の書き出しと同じ形。OFFSET +500 なので、本編では音源の 0.1 秒と 0.3 秒に届く。
        const string json = "{\"bpm\":120,\"coordScale\":1,\"offsetMs\":500,\"beatZeroMs\":0,\"displayLevel\":0,\"timeSignatures\":[],\"notes\":[" +
            "{\"time\":-400,\"beat\":-0.8,\"x\":-1,\"y\":0,\"type\":\"tap\",\"color\":\"blue\",\"direction\":\"none\",\"count\":1,\"lengthMs\":0}," +
            "{\"time\":-200,\"beat\":-0.4,\"x\":1,\"y\":0,\"type\":\"tap\",\"color\":\"red\",\"direction\":\"none\",\"count\":1,\"lengthMs\":0}]}";

        // エディターの読込と書き出しだけを通したファイルを本編が読む(別のノーツを直して保存したときや、
        // 曲の設定を他の難易度へ写したときに、触っていないノーツが通る経路)。
        ChartData chart = ChartLoader.Parse(SaberChartUtility.ToFileJson(SaberChartUtility.FromJson(json)));

        Assert.AreEqual(2, chart.notes.Count);
        Assert.AreEqual(-400f, chart.notes[0].time, Epsilon);
        Assert.AreEqual(-0.8f, chart.notes[0].beat, Epsilon);
        Assert.AreEqual(-200f, chart.notes[1].time, Epsilon);
        Assert.AreEqual(-0.4f, chart.notes[1].beat, Epsilon);
        // 本編の到着(NoteSpawner.EffectiveTime)は time + offsetMs。2つを 0.5 秒の同時押しにまとめない。
        Assert.AreEqual(100f, chart.notes[0].time + chart.offsetMs, Epsilon);
        Assert.AreEqual(300f, chart.notes[1].time + chart.offsetMs, Epsilon);
    }

    [Test]
    public void FileRoundTrip_KeepsNegativeTimeWithNegativeOriginAndTempoMap()
    {
        // 原点 -500ms と OFFSET +500 で、拍 0 が音源の頭。拍 0.5 のノーツは time が負でも拍は正。
        const string json = "{\"bpm\":120,\"offsetMs\":500,\"beatZeroMs\":-500," +
            "\"editorTempoMap\":[{\"beat\":0,\"timeMs\":-500},{\"beat\":8,\"timeMs\":3500}]," +
            "\"notes\":[{\"time\":-250,\"beat\":0.5,\"x\":0,\"y\":0},{\"time\":1500,\"beat\":4,\"x\":0,\"y\":0}]}";

        SaberChartDocument document = SaberChartUtility.FromJson(json);
        ChartData chart = ChartLoader.Parse(SaberChartUtility.ToFileJson(document));

        Assert.AreEqual(-500f, chart.beatZeroMs, Epsilon);
        Assert.AreEqual(2, chart.editorTempoMap.Count);
        Assert.AreEqual(-500f, chart.editorTempoMap[0].timeMs, Epsilon);
        Assert.AreEqual(-250f, chart.notes[0].time, Epsilon);
        Assert.AreEqual(0.5f, chart.notes[0].beat, Epsilon);
        Assert.AreEqual(250f, chart.notes[0].time + chart.offsetMs, Epsilon);
        Assert.IsTrue(SaberChartUtility.BeatsFollowSingleGrid(document),
            "time だけ 0 に寄せて拍を残すと、テンポの変わる譜面と取り違える");
    }

    [Test]
    public void Normalize_KeepsNegativeTimesButRepairsNonFiniteValues()
    {
        var document = new SaberChartDocument
        {
            notes =
            {
                new SaberChartNote { time = float.NaN, beat = float.PositiveInfinity, x = 1f },
                new SaberChartNote { time = -400f, beat = -0.8f },
            },
        };

        SaberChartUtility.Normalize(document);

        Assert.AreEqual(-400f, document.notes[0].time, Epsilon);
        Assert.AreEqual(-0.8f, document.notes[0].beat, Epsilon);
        Assert.AreEqual(0f, document.notes[1].time, Epsilon, "数でない時刻は従来どおり 0 に直す");
        Assert.AreEqual(0f, document.notes[1].beat, Epsilon);
    }
    [Test]
    public void FileRoundTrip_DoesNotTurnExplicitNullIntoRuntimeNote()
    {
        const string json = "{\"offsetMs\":1000,\"notes\":[null," +
            "{\"time\":2000,\"x\":1,\"y\":0,\"color\":\"blue\"}]}";
        var before = ChartLoader.Parse(json);
        Assert.AreEqual(1, before.notes.Count, "本編直接読込は明示nullを除く");

        var document = SaberChartUtility.FromJson(json);
        document.displayLevel = 3; // ノーツに触れない編集でも保存経路を通る。
        var after = ChartLoader.Parse(SaberChartUtility.ToFileJson(document));

        Assert.AreEqual(1, after.notes.Count, "保存だけで1秒地点にノーツを増やさない");
        Assert.AreEqual(2000f, after.notes[0].time);
        Assert.AreEqual(1f, after.notes[0].x);
        Assert.AreEqual("blue", after.notes[0].color);
        Assert.AreEqual(3000f, after.notes[0].time + after.offsetMs);
        Assert.AreEqual(3, after.displayLevel);
    }

    [Test]
    public void FileRoundTrip_KeepsRealZeroNoteAndNestedNullOrFalseMetadata()
    {
        const string json = "{\"metadata\":{\"notes\":[null],\"enabled\":false},\"notes\":[" +
            "null,{\"time\":0,\"x\":0,\"y\":0,\"color\":\"red\"}," +
            "{\"time\":2000,\"x\":1,\"y\":0,\"color\":\"blue\"},null]}";
        var document = SaberChartUtility.FromJson(json);
        document.displayLevel = 3;
        string saved = SaberChartUtility.ToFileJson(document);
        var after = ChartLoader.Parse(saved);

        Assert.AreEqual(2, after.notes.Count);
        Assert.AreEqual(0f, after.notes[0].time, "中央の0秒ノーツ自体は正規データ");
        Assert.AreEqual(0f, after.notes[0].x);
        Assert.AreEqual(0f, after.notes[0].y);
        Assert.AreEqual("red", after.notes[0].color);
        Assert.AreEqual(2000f, after.notes[1].time);
        Assert.AreEqual("blue", after.notes[1].color);
        Assert.AreEqual("{\"notes\": [null], \"enabled\": false}",
            document.extraFields.Find(field => field.key == "metadata").json);
        var reloaded = SaberChartUtility.FromJson(saved);
        Assert.AreEqual(document.extraFields.Find(field => field.key == "metadata").json,
            reloaded.extraFields.Find(field => field.key == "metadata").json);
    }

    [Test]
    public void FileRoundTrip_NullRemovalKeepsNegativeTimeAndBeat()
    {
        const string json = "{\"offsetMs\":500,\"notes\":[null," +
            "{\"time\":-400,\"beat\":-0.8,\"x\":1,\"color\":\"blue\"},null]}";
        var after = ChartLoader.Parse(SaberChartUtility.ToFileJson(SaberChartUtility.FromJson(json)));
        Assert.AreEqual(1, after.notes.Count);
        Assert.AreEqual(-400f, after.notes[0].time, Epsilon);
        Assert.AreEqual(-0.8f, after.notes[0].beat, Epsilon);
        Assert.AreEqual(100f, after.notes[0].time + after.offsetMs, Epsilon);
    }

    [Test]
    public void FromJson_LeavesDuplicateNotesKeysAsBefore()
    {
        // 重複キーは別の仕様。異なる配列の null の位置で正規ノーツを消さない。
        const string json = "{\"notes\":[null],\"notes\":[{\"time\":0,\"x\":0,\"y\":0,\"color\":\"red\"}]}";
        Assert.AreEqual(1, SaberChartUtility.FromJson(json).notes.Count);
    }
}
