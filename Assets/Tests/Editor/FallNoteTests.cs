using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

public class FallNoteTests
{
    GameObject root, prefab;
    NoteSpawner spawner;

    [TearDown]
    public void TearDown()
    {
        if (root != null) Object.DestroyImmediate(root);
        if (prefab != null) Object.DestroyImmediate(prefab);
    }

    [TestCase(.5)]
    [TestCase(1.0)]
    [TestCase(4.0)]
    public void FallsStraightAtConstantSpeedAndStopsExactlyAtArrival(double approach)
    {
        var landing = new Vector2(-1.2f, .7f);
        Vector3 first = FallRules.VisualPosition(landing, .3f, 3.2f, approach, approach);
        Assert.AreEqual(new Vector3(-1.2f, 3.9f, .3f), first);
        for (int i = 0; i < 4; i++)
        {
            var a = FallRules.VisualPosition(landing, .3f, 3.2f, approach * (4 - i) / 4, approach);
            var b = FallRules.VisualPosition(landing, .3f, 3.2f, approach * (3 - i) / 4, approach);
            Assert.AreEqual(.8f, a.y - b.y, 1e-5f);
            Assert.AreEqual(a.x, b.x);
            Assert.AreEqual(a.z, b.z);
        }
        Assert.AreEqual(new Vector3(landing.x, landing.y, .3f),
            FallRules.VisualPosition(landing, .3f, 3.2f, 0, approach));
        Assert.AreEqual(0, FallRules.Offset(3.2f, -2, approach));
        Assert.AreEqual(3.2f, FallRules.Offset(3.2f, approach + 2, approach));
    }

    [Test]
    public void MissingAndDisabledSettingsStayNormalAndInvalidHeightUsesDefault()
    {
        Assert.IsFalse(FallRules.Enabled(ChartLoader.Parse("{\"bpm\":120,\"notes\":[]}").fall));
        Assert.IsFalse(FallRules.Enabled(ChartLoader.Parse(
            "{\"bpm\":120,\"fall\":{\"enabled\":false,\"height\":3},\"notes\":[]}").fall));
        var data = ChartLoader.Parse(
            "{\"bpm\":120,\"fall\":{\"enabled\":true},\"notes\":[]}").fall;
        Assert.IsTrue(data.enabled);
        Assert.AreEqual(FallRules.DefaultHeight, data.height);
        Assert.AreEqual(FallRules.DefaultHeight,
            FallRules.Normalize(new FallChartData { enabled = true, height = float.NaN }).height);
        Assert.AreEqual(0, FallRules.Offset(3.2f, double.NaN, 1));
        Assert.AreEqual(0, FallRules.Offset(3.2f, 1, 0));
    }

    [Test]
    public void PetalsFadeOutBeforeTheLastPointTwoSeconds()
    {
        Assert.AreEqual(0, FallRules.PetalAlpha(1, 1));
        Assert.Greater(FallRules.PetalAlpha(.5, 1), 0);
        Assert.Greater(FallRules.PetalAlpha(.21, 1), 0);
        Assert.Less(FallRules.PetalAlpha(.21, 1), FallRules.PetalAlpha(.32, 1));
        foreach (double remaining in new[] { .2, .1, 0, -1 })
            Assert.AreEqual(0, FallRules.PetalAlpha(remaining, 1));
    }

    [Test]
    public void FUsesMixedDirectionsAndTheSharedSecretTable()
    {
        string path = Path.Combine(Application.streamingAssetsPath, "Songs", "Epilogue", "chart_f.json");
        Assert.IsTrue(File.Exists(path), "先に gen_fall.py --write を実行する。フォールバックで通さない");
        var hard = ChartLoader.LoadFromStreamingAssets("Epilogue", "Hard");
        var fall = ChartLoader.LoadFromStreamingAssets("Epilogue", "F");
        MixedSecretChartAssertions.Check("Epilogue", fall, "f", 61334.68, 81, 232);
        Assert.AreEqual(90, fall.notes.Count(n => n.IsDirection));
        Assert.AreEqual(9, fall.notes.Count(n => n.IsLong));
        Assert.AreEqual(17, fall.notes.Count(n => n.color == "gold"));
        AssertMixedGeometry(fall);
        AssertRatio(fall, 0, 96, .10, .20);
        AssertRatio(fall, 96, 196, .30, .50);
        AssertRatio(fall, 196, 244, .60, .70);
        AssertRatio(fall, 244, 268, .60, .70);
        AssertRatio(fall, 268, 304, 0, 0);
        Assert.IsFalse(fall.notes[0].fall);
        Assert.IsFalse(fall.notes.Any(n => n.HasOvertake));
        var clock = new ChartTempoMap(fall.bpm, fall.beatZeroMs, fall.editorTempoMap);
        Assert.IsNotEmpty(fall.editorTempoMap, "加速後の小節線も音に合わせる");
        foreach (var note in fall.notes)
            // 譜面の時刻は混合元の値、拍表は pulse-map から作るので 0.1ms 未満の丸めの差が出る。小節線には十分小さい。
            Assert.AreEqual(note.time, clock.TimeAt(note.beat), .5, "小節線の拍表");
        Assert.AreEqual(hard.bpm, fall.bpm);
        Assert.AreEqual(hard.coordScale, fall.coordScale);
        Assert.AreEqual(hard.offsetMs, fall.offsetMs);
        Assert.AreEqual(8, fall.displayLevel);
        Assert.IsTrue(fall.fall.enabled);
        Assert.AreEqual(2.4f, fall.fall.height);
        Assert.IsFalse(FallRules.Enabled(hard.fall));
        Assert.IsFalse(fall.notes.Any(n => n.HasWind));
        Assert.IsEmpty(fall.scroll);

        var secret = RushESecretState.For("Epilogue");
        Assert.AreSame(RushESecretState.EpilogueFall, secret);
        Assert.AreEqual("F", secret.Difficulty);
        Assert.AreSame(secret, RushESecretState.ForDifficulty(" f "));
        Assert.AreEqual(RushESecretState.Sakura, GameHUDSkin.DifficultyColor("F"));
        Assert.AreEqual(RushESecretState.Sakura, GameStartCountdown.AccentForDifficulty("f"));
        var state = new RushESecretState();
        state.Select("Epilogue");
        for (int i = 0; i < 10; i++) Assert.IsTrue(state.TryHit(i * .2));
        Assert.IsTrue(state.Reveal());
        Assert.IsTrue(state.Finish());
        Assert.IsTrue(state.Unlocked);
    }

    [Test]
    public void JudgmentRootAndCuesStayAtLandingWhileTheBodyFalls()
    {
        MakeSpawner();
        var chart = OneNote(true);
        spawner.SetChart(chart);
        spawner.Tick(0);
        var note = spawner.LiveNotes.Single();
        Assert.AreEqual("FallVisual", note.VisualTransform.name);
        Assert.AreEqual(2.4f, note.VisualTransform.position.y - note.LandingPosition.y, 1e-5f);
        AssertLanding(note);
        spawner.Tick(.5);
        Assert.AreEqual(1.2f, note.VisualTransform.position.y - note.LandingPosition.y, 1e-5f);
        Assert.AreEqual(3, spawner.FallPetalCount);

        // 判定だけを進めるコマでも根元は着地点。見た目の落下は止めない。
        double early = 1 - spawner.earlyJudgeWindow * .5;
        spawner.RefreshJudgmentWindows(early);
        Assert.IsTrue(note.IsJudgeable);
        AssertLanding(note);
        Assert.Greater(note.VisualTransform.position.y, note.LandingPosition.y);
        spawner.Tick(.81);
        Assert.AreEqual(0, spawner.FallPetalCount);
        spawner.Tick(1);
        Assert.AreEqual(note.transform.position, note.VisualTransform.position);
        AssertLanding(note);
    }

    [Test]
    public void LongsStayAtLandingWithoutNearbyTextAndNormalChartsKeepTheirDepth()
    {
        MakeSpawner();
        var chart = OneNote(true);
        chart.notes[0].type = "long";
        chart.notes[0].count = 3;
        chart.notes[0].lengthMs = 900;
        spawner.SetChart(chart);
        spawner.Tick(0);
        var note = spawner.LiveNotes.Single();
        Assert.NotNull(note.countLabel);
        Assert.IsFalse(note.countLabel.gameObject.activeSelf);
        spawner.Tick(1.6);
        Assert.IsTrue(note.IsJudgeable);
        AssertLanding(note);
        Assert.AreEqual(note.transform.position, note.VisualTransform.position);
        Assert.IsFalse(note.countLabel.gameObject.activeSelf);

        spawner.SetChart(OneNote(false));
        spawner.Tick(.5);
        var normal = spawner.LiveNotes.Single();
        Assert.IsFalse(spawner.HasFall);
        Assert.AreSame(normal.transform, normal.VisualTransform);
        Assert.AreEqual(spawner.judgeZ + spawner.Speed * .5f, normal.transform.position.z, 1e-5f);
        Assert.AreEqual(normal.LandingPosition.y, normal.transform.position.y);
        spawner.Tick(1);
        Assert.AreEqual(spawner.judgeZ, normal.transform.position.z, 1e-5f);
    }

    [Test]
    public void JudgeLineExistsOnlyDuringFallEvenWithoutPetals()
    {
        MakeSpawner();
        spawner.SetChart(OneNote(true));
        spawner.Tick(-.5);
        Assert.AreEqual(0, spawner.FallPetalCount);
        Assert.IsTrue(spawner.FallLineVisible);
        var line = root.transform.Find("FallPetals/FallJudgeLine");
        Assert.NotNull(line);
        var mesh = line.GetComponent<MeshFilter>().sharedMesh;
        var points = mesh.vertices.Select(v => line.TransformPoint(v)).ToArray();
        Assert.AreEqual(FallRules.LineY, points.Average(v => v.y), 1e-6);
        Assert.IsTrue(points.All(v => Mathf.Abs(v.z - spawner.judgeZ) < 1e-6));
        Assert.AreEqual(FallRules.LineThickness, points.Max(v => v.y) - points.Min(v => v.y), 1e-6);
        Assert.IsTrue(mesh.colors.All(c => c.a > 0 && c.a <= .25f && c.maxColorComponent <= 1f));
        Assert.IsEmpty(line.GetComponentsInChildren<TMPro.TMP_Text>());

        spawner.SetChart(OneNote(false));
        Assert.IsFalse(spawner.FallLineVisible, "通常譜面へ替えた瞬間に消す");
        spawner.Tick(.5);
        Assert.IsFalse(spawner.FallLineVisible);
        spawner.SetChart(OneNote(true));
        spawner.Tick(-.5);
        Assert.IsTrue(spawner.FallLineVisible);
        spawner.enabled = false;
        // EditMode では Unity が OnDisable を呼ばないので、再生中に Unity が呼ぶのと同じ入口を呼ぶ。
        typeof(NoteSpawner).GetMethod("OnDisable", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Invoke(spawner, null);
        Assert.IsFalse(spawner.FallLineVisible, "Spawner停止時も消す");
    }

    [Test]
    public void FallChartSchedulesBarLines()
    {
        MakeSpawner();
        var bars = root.AddComponent<BarLineSpawner>();
        bars.barLinePrefab = prefab;
        bars.SetChart(OneNote(true));
        bars.Tick(0);
        Assert.Greater(bars.AliveCount, 0);
        Assert.Greater(bars.NextIndex, 0);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void FallDisablesEveryWindComponentBeforeSpawning(bool fall)
    {
        MakeSpawner();
        var chart = OneNote(fall);
        var source = chart.notes[0];
        source.windX = .4f;
        source.windY = -.2f;
        source.windSpin = .3f;
        source.windSway = .5f;
        spawner.SetChart(chart);
        spawner.Tick(.5);
        var note = spawner.LiveNotes.Single();
        Assert.AreEqual(!fall, spawner.HasWindNotes);
        Assert.AreEqual(!fall, note.HasWind);
        Assert.AreEqual(fall ? 0f : .4f, note.WindX);
        Assert.AreEqual(fall ? 0f : -.2f, note.WindY);
        Assert.AreEqual(fall ? 0f : .3f, note.WindSpin);
        Assert.AreEqual(fall ? 0f : .5f, note.WindSway);
        if (fall)
        {
            Assert.IsFalse(source.HasWind);
            Assert.IsNull(note.transform.Find("WindVisual"));
            Assert.AreEqual("FallVisual", note.VisualTransform.name);
            Assert.AreSame(note.transform, note.VisualTransform.parent);
            Assert.AreSame(note.VisualTransform, note.BodyRenderer.transform);
            Assert.IsTrue(note.BodyRenderer.enabled);
            AssertLanding(note);
            Assert.AreEqual(1.2f, note.VisualTransform.position.y - note.LandingPosition.y, 1e-5f);
        }
    }

    [Test]
    public void PetalRgbFollowsLightingDuringPlayPauseAndEndWithoutChangingAlpha()
    {
        MakeSpawner();
        var chart = OneNote(true);
        chart.dark.Add(new DarkPointData { time = -1000, level = 1, ramp = 1000 });
        spawner.SetChart(chart);
        var mode = DarkMode.Create(root.transform, chart, spawner, null, null, null, null);
        try
        {
            spawner.Tick(.5);
            var note = spawner.LiveNotes.Single();
            var petals = root.GetComponentInChildren<FallPetals>();
            var mesh = petals.GetComponent<MeshFilter>().sharedMesh;
            var bright = mesh.colors;
            var vertices = mesh.vertices;
            var triangles = mesh.triangles;
            Assert.AreEqual(3, petals.PetalCount);
            Assert.IsNotEmpty(bright);
            Assert.AreEqual(new Color(1f, .76f, .87f, .45f), bright[0]);
            Vector3 position = note.transform.position;
            bool judgeable = note.IsJudgeable;
            var far = new BladeSample(new Vector2(30, 30), new Vector2(31, 30));
            mode.BladeOverride = _ => far;
            mode.Tick(.5, refreshNotes: false);
            spawner.Tick(.5);
            AssertPetalColors(bright, mesh.colors, DarkRules.NoteMinimum);

            mode.BladeOverride = _ => default(BladeSample);
            mode.Tick(.5);
            AssertPetalColors(bright, mesh.colors, 1f);
            mode.BladeOverride = _ => far;
            mode.Tick(.5);
            AssertPetalColors(bright, mesh.colors, DarkRules.NoteMinimum);
            mode.EndPlay();
            AssertPetalColors(bright, mesh.colors, 1f);
            CollectionAssert.AreEqual(vertices, mesh.vertices);
            CollectionAssert.AreEqual(triangles, mesh.triangles);
            Assert.AreEqual(position, note.transform.position);
            Assert.AreEqual(judgeable, note.IsJudgeable);
            Assert.IsFalse(note.IsMissed);
            Assert.AreEqual(3, petals.PetalCount);
        }
        finally { mode.EndPlay(); }
    }

    [TestCase(true)]
    [TestCase(false)]
    public void OneFrameLateSpawnAlreadyHasItsCorrectVisualPosition(bool falling)
    {
        MakeSpawner();
        var chart = OneNote(true);
        chart.notes[0].fall = falling;
        spawner.SetChart(chart);
        double now = 1.0 / 30;
        spawner.Tick(now);
        var note = spawner.LiveNotes.Single();
        Assert.AreEqual(1 - now, note.HitTime - now, 1e-8);
        if (falling)
        {
            AssertLanding(note);
            Assert.AreEqual(FallRules.LineY + 2.4f * (float)(1 - now),
                note.VisualTransform.position.y, 1e-5f);
        }
        else
        {
            Assert.AreSame(note.transform, note.VisualTransform);
            Assert.AreEqual(spawner.judgeZ + spawner.Speed * (float)(1 - now),
                note.transform.position.z, 1e-5f);
        }
    }

    static void AssertPetalColors(Color[] bright, Color[] actual, float brightness)
    {
        Assert.AreEqual(bright.Length, actual.Length);
        for (int i = 0; i < bright.Length; i++)
        {
            Assert.AreEqual(bright[i].r * brightness, actual[i].r, 1e-6f);
            Assert.AreEqual(bright[i].g * brightness, actual[i].g, 1e-6f);
            Assert.AreEqual(bright[i].b * brightness, actual[i].b, 1e-6f);
            Assert.AreEqual(bright[i].a, actual[i].a, 1e-6f);
        }
    }

    [Test]
    public void FrontNotesInFallStillDisableWindScrollAndOvertakeForTheWholeChart()
    {
        MakeSpawner();
        var chart = OneNote(true);
        chart.notes[0].fall = false;
        chart.notes[0].windX = .4f;
        chart.notes[0].approachScale = 2f;
        chart.scroll.Add(new ScrollSpeedData { time = 0, speed = 2f });
        spawner.SetChart(chart);
        Assert.IsFalse(spawner.HasWindNotes);
        Assert.IsFalse(spawner.HasScroll);
        Assert.IsFalse(spawner.HasOvertakeNotes);
        spawner.Tick(.5);
        var note = spawner.LiveNotes.Single();
        Assert.IsFalse(note.IsFall);
        Assert.AreSame(note.transform, note.VisualTransform);
        Assert.AreEqual(spawner.judgeZ + spawner.Speed * .5f, note.transform.position.z, 1e-5f);
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void OnlyOptedInNotesUseFallAndNormalLongsKeepAllTheirVisuals(bool chartFall, bool noteFall)
    {
        MakeSpawner();
        var chart = OneNote(chartFall);
        var source = chart.notes[0];
        source.fall = noteFall;
        source.count = 3;
        source.type = "long";
        source.lengthMs = 900;
        source.y = chartFall && noteFall ? FallRules.LineY : .65f;
        // JSONを往復しても、ノーツの指定と省略時の既定値を保つ。
        chart = ChartLoader.Parse(JsonUtility.ToJson(chart));
        Assert.AreEqual(noteFall, chart.notes[0].fall);
        Assert.IsFalse(ChartLoader.Parse("{\"bpm\":120,\"notes\":[{\"time\":1000}]}").notes[0].fall);
        spawner.SetChart(chart);
        spawner.Tick(.5);
        var note = spawner.LiveNotes.Single();
        Assert.AreEqual(chartFall && noteFall, note.IsFall);
        if (note.IsFall)
        {
            AssertLanding(note);
            Assert.AreEqual(1.2f, note.VisualTransform.position.y - source.y, 1e-5f);
        }
        else
        {
            Assert.AreSame(note.transform, note.VisualTransform);
            Assert.AreSame(note.transform, note.BodyRenderer.transform);
            Assert.AreEqual(new Vector2(source.x, source.y), (Vector2)note.transform.position);
            Assert.AreEqual(spawner.judgeZ + spawner.Speed * .5f, note.transform.position.z, 1e-5f);
            Assert.NotNull(note.TimingCue);
            Assert.AreEqual(note.LandingPosition, (Vector2)note.TimingCue.GhostRoot.transform.position);
            Assert.IsTrue(note.countLabel.gameObject.activeSelf);
            Assert.AreEqual("3", note.countLabel.text);
            Assert.AreEqual(.8f * (1f + .9f / spawner.secondsPerLongCut),
                note.transform.localScale.z, 1e-5f);
            Assert.AreEqual(0, spawner.FallPetalCount);
            spawner.Tick(1);
            Assert.AreEqual(spawner.judgeZ, note.transform.position.z, 1e-5f);
            spawner.Tick(1.45);
            Assert.AreEqual(spawner.judgeZ - spawner.longLingerDriftZ * .5f,
                note.transform.position.z, 1e-5f);
        }
    }

    [Test]
    public void PoolKeysSeparateFallFromNormalInsideTheSameChart()
    {
        MakeSpawner();
        spawner.SetChart(OneNote(true));
        var key = typeof(NoteSpawner).GetMethod("Key",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var normal = new NoteData { color = "red" };
        var falling = new NoteData { color = "red", fall = true };
        object normalKey = key.Invoke(spawner, new object[] { normal });
        object fallKey = key.Invoke(spawner, new object[] { falling });
        Assert.AreNotEqual(normalKey, fallKey);
        spawner.SetChart(OneNote(false));
        Assert.AreEqual(normalKey, key.Invoke(spawner, new object[] { normal }));
        Assert.AreEqual(normalKey, key.Invoke(spawner, new object[] { falling }));
    }

    [Test]
    public void MixedChartProducesPetalsOnlyAroundFallingNotes()
    {
        MakeSpawner();
        var chart = OneNote(true);
        chart.notes.Add(new NoteData { time = 1000, x = 1.5f, y = .8f, color = "blue" });
        spawner.SetChart(chart);
        spawner.Tick(.5);
        Assert.AreEqual(2, spawner.AliveCount);
        Assert.AreEqual(1, spawner.LiveNotes.Count(n => n.IsFall));
        Assert.AreEqual(3, spawner.FallPetalCount);
        var plain = spawner.LiveNotes.Single(n => !n.IsFall);
        Assert.AreEqual(.8f, plain.transform.position.y);
        Assert.Greater(plain.transform.position.z, spawner.judgeZ);
    }

    static void AssertRatio(ChartData chart, float start, float end, double minimum, double maximum)
    {
        var notes = chart.notes.Where(n => n.beat >= start && n.beat < end).ToList();
        Assert.IsNotEmpty(notes);
        double ratio = notes.Count(n => n.fall) / (double)notes.Count;
        Assert.That(ratio, Is.InRange(minimum, maximum), "拍" + start + ".." + end);
    }

    static void AssertMixedGeometry(ChartData chart)
    {
        const double approach = 1;
        float gap = FallRules.MinimumNoteGap;
        double End(NoteData n) => n.TimeSeconds + n.LingerSeconds(.7f)
            + (n.LingerSeconds(.7f) > 0 ? .24 : 0);
        foreach (var n in chart.notes.Where(n => n.fall))
        {
            Assert.AreEqual(FallRules.LineY, n.y * chart.coordScale, 1e-6);
            Assert.LessOrEqual(Mathf.Abs(n.x * chart.coordScale), 2.4f);
        }
        for (int i = 0; i < chart.notes.Count; i++)
        {
            var a = chart.notes[i];
            foreach (var b in chart.notes.Skip(i + 1))
            {
                if (b.time - a.time <= 10)
                    Assert.AreEqual(a.fall, b.fall, "同時組: " + a.time);
                bool separate = false;
                if (a.fall && b.fall)
                {
                    double end = System.Math.Min(End(a), End(b));
                    double start = b.TimeSeconds - approach;
                    double dy = chart.fall.height * (System.Math.Max(0, b.TimeSeconds - end)
                        - System.Math.Max(0, a.TimeSeconds - end)) / approach;
                    separate = start <= end && dy < gap - 1e-5;
                }
                else if (a.fall || b.fall)
                {
                    var falling = a.fall ? a : b;
                    var normal = a.fall ? b : a;
                    float y = normal.y * chart.coordScale;
                    separate = normal.TimeSeconds <= End(falling)
                        && End(normal) >= falling.TimeSeconds - approach
                        && y > FallRules.LineY - gap
                        && y < FallRules.LineY + chart.fall.height + gap;
                }
                if (separate)
                    Assert.GreaterOrEqual(Mathf.Abs(a.x - b.x) * chart.coordScale,
                        gap - 1e-5f, "落下の柱・横間隔: " + a.time + "/" + b.time);
            }
        }
    }

    void MakeSpawner()
    {
        root = new GameObject("FallTest");
        prefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
        prefab.name = "FallTestPrefab";
        prefab.transform.localScale = Vector3.one * .8f;
        prefab.SetActive(false);
        spawner = root.AddComponent<NoteSpawner>();
        spawner.notePrefab = prefab;
        spawner.noteRoot = root.transform;
        spawner.approachTime = 1f;
        spawner.reuseNotes = false;
        spawner.simultaneousGuideEnabled = false;
    }

    static ChartData OneNote(bool fall)
    {
        var chart = new ChartData
        {
            bpm = 120,
            fall = new FallChartData { enabled = fall, height = 2.4f }
        };
        chart.notes.Add(new NoteData { time = 1000, x = -.7f, y = fall ? FallRules.LineY : .3f, color = "red", fall = fall });
        return chart;
    }

    void AssertLanding(CuttableNote note)
    {
        Assert.AreEqual(note.LandingPosition, (Vector2)note.transform.position);
        Assert.AreEqual(spawner.judgeZ, note.transform.position.z);
        Assert.NotNull(note.TimingCue);
        Assert.AreEqual(note.LandingPosition, (Vector2)note.TimingCue.GhostRoot.transform.position);
        Assert.AreEqual(note.LandingPosition, (Vector2)note.TimingCue.ApproachRoot.transform.position);
    }
}
