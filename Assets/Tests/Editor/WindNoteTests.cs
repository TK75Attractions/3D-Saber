using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

public class WindRulesTests
{
    [Test]
    public void FarNotesMoveFartherSoTheVisibleShiftMatchesTheChart()
    {
        // 奥(z=20)はカメラから 27、判定面はカメラから 7。奥では 27/7 倍動かすと、見た目は譜面の値になる。
        Assert.AreEqual(27f / 7f, WindRules.DepthScale(20f), 1e-6f);
        Assert.AreEqual(1f, WindRules.DepthScale(0f));
        Assert.AreEqual(1f, WindRules.DepthScale(-3f), "判定面より手前では大きくしない");
        Assert.AreEqual(1f, WindRules.DepthScale(float.NaN));
        Vector2 plain = WindRules.Offset(1.5f, -.4f, 0, .3f, 1, 1, .5);
        Vector2 far = WindRules.WorldOffset(1.5f, -.4f, 0, .3f, 1, 1, .5, 20f);
        Assert.AreEqual(plain.x * 27f / 7f, far.x, 1e-5f);
        Assert.AreEqual(plain.y * 27f / 7f, far.y, 1e-5f);
        Assert.AreEqual(Vector2.zero, WindRules.WorldOffset(1.5f, -.4f, 0, 0, .2, 1, .5, 4f), "流されは静かな区間で 0");
    }

    [TestCase(-10d)]
    [TestCase(-.01d)]
    [TestCase(0d)]
    [TestCase(.06d)]
    public void StillAndPastArrivalAreExactlyZero(double remaining)
    {
        Assert.AreEqual(Vector2.zero,
            WindRules.Offset(2, -1, 3, .8f, remaining, 2, .5));
    }

    [TestCase(-1d)]
    [TestCase(0d)]
    [TestCase(.2d)]
    [TestCase(.35d)]
    public void ShortApproachIsExactlyZero(double approach)
    {
        Assert.AreEqual(Vector2.zero,
            WindRules.Offset(2, -1, 3, 0, 2, approach, .5));
    }

    [Test]
    public void SpawnKeepsAuthoredOffsetAndAddsSwayWithoutRotation()
    {
        var offset = WindRules.Offset(1, -.5f, 5, .4f, 2, 2, .5);
        Assert.AreEqual(1.4f, offset.x, 1e-6f);
        Assert.AreEqual(-.5f, offset.y, 1e-6f);
        Assert.AreEqual(offset, WindRules.Offset(1, -.5f, 5, .4f, 3, 2, .5),
            "出現より前の値もp=1へ収める");
    }

    [TestCase(1f)]
    [TestCase(-1f)]
    public void PositiveSpinTurnsCounterclockwise(float sign)
    {
        // p=.5、e=.5。正のπなら途中で左へ90度回る。
        var offset = WindRules.Offset(1, 0, sign * Mathf.PI, 0, .85, 1.35, 0);
        Assert.AreEqual(0, offset.x, 1e-6f);
        Assert.AreEqual(sign * .5f, offset.y, 1e-6f);
    }

    [Test]
    public void SwayUsesTheSharedTwoBeatPhase()
    {
        Assert.AreEqual(.8f, WindRules.Offset(0, 0, 0, .8f, 2, 2, .5).x, 1e-6f);
        Assert.AreEqual(-.8f, WindRules.Offset(0, 0, 0, .8f, 2, 2, 1.5).x, 1e-6f);
        Assert.AreEqual(.8f, WindRules.Offset(0, 0, 0, .8f, 2, 2, 2.5).x, 1e-6f);
        Assert.AreEqual(0, WindRules.Offset(0, 0, 0, .8f, 2, 2, 0).x, 1e-6f);
        Assert.AreEqual(0, WindRules.Offset(0, 0, 0, .8f, 2, 2, 1).x, 1e-6f);
    }

    [Test]
    public void SwayKeepsItsAmplitudeUntilTheLastShortFade()
    {
        Assert.AreEqual(Vector2.zero, WindRules.Offset(2, -1, 3, 0, WindRules.CalmSeconds, 1, .5));
        foreach (double remaining in new[] { 1d, .6, .35, .25, .18 })
            Assert.AreEqual(.8f, WindRules.SwayOffset(.8f, remaining, 1, .5), 1e-6f);
        Assert.AreEqual(.4f, WindRules.SwayOffset(.8f, .12, 1, .5), 1e-6f);
        Assert.Greater(WindRules.SwayOffset(.8f, .061, 1, .5), 0f);
        Assert.AreEqual(0f, WindRules.SwayOffset(.8f, .06, 1, .5));
        Assert.AreEqual(0f, WindRules.SwayOffset(.8f, 1, .06, .5));
        Assert.AreEqual(.8f, WindRules.SwayOffset(.8f, .1, .1, .5), 1e-6f);
        // フェードの両端の傾きも 0 へ近づく。
        Assert.Less(.8f - WindRules.SwayOffset(.8f, .18 - 1e-5, 1, .5), 1e-6f);
        Assert.Less(WindRules.SwayOffset(.8f, .06 + 1e-5, 1, .5), 1e-6f);
    }

    [Test]
    public void EveryNonFiniteInputReturnsZero()
    {
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            for (int index = 0; index < 7; index++)
            {
                var values = new[] { 1d, .5d, .2d, .3d, 1d, 2d, .5d };
                values[index] = invalid;
                Assert.AreEqual(Vector2.zero, WindRules.Offset(
                    (float)values[0], (float)values[1], (float)values[2], (float)values[3],
                    values[4], values[5], values[6]), "引数 " + index);
            }
        }
        Assert.AreEqual(Vector2.zero,
            WindRules.Offset(float.MaxValue, 0, 0, float.MaxValue, 1, 1, .5),
            "計算結果がfloatの範囲を超えても非有限の座標を返さない");
    }
}

public class WindChartLoaderTests
{
    static NoteData Parse(NoteData note)
    {
        var chart = new ChartData();
        chart.notes.Add(note);
        var parsed = ChartLoader.Parse(JsonUtility.ToJson(chart));
        Assert.AreEqual(1, parsed.notes.Count, "風の壊れた値でノーツ自体を捨てない");
        return parsed.notes[0];
    }

    [Test]
    public void LoaderClampsWindWithoutChangingThePlayableNote()
    {
        var note = Parse(new NoteData {
            time = 450, x = .6f, y = -.7f, color = "blue", type = "long",
            direction = "up", count = 3, lengthMs = 800, veil = .4f,
            windX = 9, windY = -9, windSpin = 99, windSway = 2
        });
        Assert.AreEqual(3f, note.windX);
        Assert.AreEqual(-2f, note.windY);
        Assert.AreEqual(2f * Mathf.PI, note.windSpin, 1e-6f);
        Assert.AreEqual(1f, note.windSway);
        Assert.IsTrue(note.HasWind);
        Assert.AreEqual(450f, note.time);
        Assert.AreEqual(.6f, note.x);
        Assert.AreEqual(-.7f, note.y);
        Assert.AreEqual("blue", note.color);
        Assert.AreEqual("long", note.type);
        Assert.AreEqual("up", note.direction);
        Assert.AreEqual(3, note.count);
        Assert.AreEqual(800f, note.lengthMs);
        Assert.AreEqual(.4f, note.veil);

        note = Parse(new NoteData { windX = -9, windY = 9, windSpin = -99, windSway = -2 });
        Assert.AreEqual(-3f, note.windX);
        Assert.AreEqual(2f, note.windY);
        Assert.AreEqual(-2f * Mathf.PI, note.windSpin, 1e-6f);
        Assert.AreEqual(0f, note.windSway);
    }

    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    [TestCase(float.NegativeInfinity)]
    public void LoaderReplacesNonFiniteWindWithZero(float invalid)
    {
        var note = Parse(new NoteData {
            time = 1000, windX = invalid, windY = invalid,
            windSpin = invalid, windSway = invalid
        });
        Assert.AreEqual(0f, note.windX);
        Assert.AreEqual(0f, note.windY);
        Assert.AreEqual(0f, note.windSpin);
        Assert.AreEqual(0f, note.windSway);
        Assert.IsFalse(note.HasWind);

        note = Parse(new NoteData { windX = invalid, windY = .5f, windSway = .2f });
        Assert.AreEqual(0f, note.windX);
        Assert.AreEqual(.5f, note.windY);
        Assert.AreEqual(.2f, note.windSway);
        Assert.IsTrue(note.HasWind, "有効なほかの成分は残す");
    }

    [Test]
    public void MissingWindAndSpinAloneDoNotEnableMotion()
    {
        var legacy = ChartLoader.Parse(@"{""notes"":[{""time"":1000}]}").notes[0];
        Assert.AreEqual(0f, legacy.windX);
        Assert.AreEqual(0f, legacy.windY);
        Assert.AreEqual(0f, legacy.windSpin);
        Assert.AreEqual(0f, legacy.windSway);
        Assert.IsFalse(legacy.HasWind);

        var spinOnly = Parse(new NoteData { windSpin = 1 });
        Assert.AreEqual(1f, spinOnly.windSpin);
        Assert.IsFalse(spinOnly.HasWind);
        Assert.IsTrue(Parse(new NoteData { windX = .1f }).HasWind);
        Assert.IsTrue(Parse(new NoteData { windY = -.1f }).HasWind);
        Assert.IsTrue(Parse(new NoteData { windSway = .1f }).HasWind);
    }
}

public class WindNoteTests
{
    GameObject root;
    NoteSpawner spawner;
    bool projector, reduced;

    [SetUp]
    public void SetUp()
    {
        projector = DisplaySettings.ProjectorMode;
        reduced = DisplaySettings.ReducedEffects;
        DisplaySettings.SetProjectorModeForTest(false);
        DisplaySettings.SetReducedEffectsForTest(false);

        root = new GameObject("WindNoteTests");
        var prefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
        prefab.name = "WindNotePrefab";
        prefab.transform.SetParent(root.transform, false);
        prefab.AddComponent<CuttableNote>();
        prefab.SetActive(false);

        spawner = root.AddComponent<NoteSpawner>();
        spawner.notePrefab = prefab;
        spawner.noteRoot = root.transform;
        spawner.reuseNotes = false;
        spawner.simultaneousGuideEnabled = false;
        spawner.approachTime = 2;
        spawner.spawnZ = 20;
        spawner.judgeZ = 1.25f;
        spawner.SetExtraOffsetSeconds(.075);
        spawner.ConfigureFloorGuide(-3);
    }

    [TearDown]
    public void TearDown()
    {
        if (root != null) Object.DestroyImmediate(root);
        DisplaySettings.SetProjectorModeForTest(projector);
        DisplaySettings.SetReducedEffectsForTest(reduced);
    }

    static NoteData Wind(int cuts = 1) => new NoteData {
        time = 4000, x = -.6f, y = .7f,
        count = cuts, type = cuts > 1 ? "long" : "tap",
        windX = 1.2f, windY = -.4f, windSpin = 1.5f, windSway = .3f
    };

    static ChartData Chart(params NoteData[] notes)
    {
        var chart = new ChartData { bpm = 140, coordScale = 1.5f, offsetMs = 125 };
        chart.notes.AddRange(notes);
        return chart;
    }

    static void ExactXY(Vector2 expected, Vector3 actual)
    {
        Assert.AreEqual(expected.x, actual.x);
        Assert.AreEqual(expected.y, actual.y);
    }

    static void NearXY(Vector2 expected, Vector2 actual)
    {
        Assert.AreEqual(expected.x, actual.x, 1e-5f);
        Assert.AreEqual(expected.y, actual.y, 1e-5f);
    }

    void GhostsAtLanding(CuttableNote note)
    {
        Assert.NotNull(note.TimingCue);
        ExactXY(note.LandingPosition, note.TimingCue.GhostRoot.transform.position);
        ExactXY(note.LandingPosition, note.TimingCue.ApproachRoot.transform.position);
        Assert.AreEqual(spawner.judgeZ + note.TimingCue.ghostZBias,
            note.TimingCue.GhostRoot.transform.position.z);
        Assert.AreEqual(spawner.judgeZ + note.TimingCue.approachZBias,
            note.TimingCue.ApproachRoot.transform.position.z);
    }

    [TestCase(0d)]
    [TestCase(.17d)]
    [TestCase(1.8d)]
    public void SpawnEventAlreadySeesWindAndBothGhostsSeeLanding(double delay)
    {
        var data = Wind();
        var chart = Chart(data);
        spawner.SetChart(chart);
        double hit = spawner.EffectiveTime(data);
        double now = hit - spawner.approachTime + delay;
        Vector2 landing = new Vector2(data.x, data.y) * chart.coordScale;
        // 奥にいるぶん大きく動かす(見た目のずれを譜面の値にそろえる)。このコマの奥行きで倍率を決める。
        Vector2 expected = landing + WindRules.WorldOffset(
            data.windX, data.windY, data.windSpin, data.windSway,
            hit - now, spawner.approachTime,
            (now - spawner.TotalOffsetSeconds) * chart.bpm / 60.0,
            spawner.judgeZ + spawner.Speed * (float)(hit - now));
        bool observed = false;
        spawner.OnNoteSpawned += note =>
        {
            observed = true;
            NearXY(expected, note.VisualTransform.position);
            Assert.AreNotEqual(landing, (Vector2)note.VisualTransform.position);
            Assert.AreEqual(landing, note.LandingPosition);
            GhostsAtLanding(note);
        };

        spawner.Tick(now);
        Assert.IsTrue(observed);
        var live = spawner.LiveNotes[0];
        NearXY(expected, live.VisualTransform.position);
        Assert.AreEqual(spawner.judgeZ + spawner.Speed * (float)(hit - now),
            live.transform.position.z, 1e-5f);

        // 出現ちょうどの床の印は透明なので、少し進めて描画メッシュも検査する。
        spawner.Tick(now + .05);
        GhostsAtLanding(live);
        Assert.AreEqual(1, spawner.FloorGuide.MarkerCount);
        var vertices = spawner.FloorGuide.GetComponent<MeshFilter>().sharedMesh.vertices;
        Vector3 center = spawner.FloorGuide.transform.TransformPoint((vertices[8] + vertices[10]) * .5f);
        Assert.AreEqual(landing.x, center.x, 1e-5f);
        Assert.AreEqual(live.transform.position.z, center.z, 1e-5f);
    }

    [TestCase(1)]
    [TestCase(4)]
    public void IntermediateMotionCalmsExactlyAndStaysStillThroughLingerAndMiss(int cuts)
    {
        var data = Wind(cuts);
        var chart = Chart(data);
        spawner.SetChart(chart);
        double hit = spawner.EffectiveTime(data);
        double now = hit - (spawner.approachTime + WindRules.CalmSeconds) * .5;
        spawner.Tick(now);
        var note = spawner.LiveNotes[0];
        double beat = (now - spawner.TotalOffsetSeconds) * chart.bpm / 60.0;
        NearXY(note.LandingPosition + WindRules.WorldOffset(
            data.windX, data.windY, data.windSpin, data.windSway,
            hit - now, spawner.approachTime, beat, note.transform.position.z), note.VisualTransform.position);
        Assert.AreNotEqual(note.LandingPosition, (Vector2)note.transform.position);
        GhostsAtLanding(note);

        foreach (double remaining in new[] { WindRules.CalmSeconds, .2, .12, .061, WindRules.StillSeconds, 0, -.3 })
        {
            spawner.Tick(hit - remaining);
            ExactXY(note.LandingPosition, note.transform.position);
            Assert.AreEqual(spawner.ComputeNoteZ(note, remaining, spawner.Speed),
                note.transform.position.z, 1e-5f);
            GhostsAtLanding(note);
        }

        double after = spawner.LateWindowFor(note) + spawner.missGrace + .02;
        spawner.Tick(hit + after);
        Assert.IsTrue(note.IsMissed);
        ExactXY(note.LandingPosition, note.transform.position);
        Assert.AreEqual(spawner.ComputeNoteZ(note, -after, spawner.Speed),
            note.transform.position.z, 1e-5f);
    }

    [TestCase(-.15d)]
    [TestCase(0d)]
    [TestCase(.12d)]
    [TestCase(.25d)]
    public void JudgmentWindowsAndPreJudgeFrameSkipsAlwaysUseLanding(double latency)
    {
        Assert.LessOrEqual(
            JudgmentTierHelper.EarlyBadSeconds
                - PhoneSaberJudgeLatencySettings.MinimumAfterPredictionMs / 1000.0,
            WindRules.CalmSeconds);

        foreach (int cuts in new[] { 1, 4 })
        {
            var data = Wind(cuts);
            spawner.SetChart(Chart(data));
            spawner.JudgeLatencySeconds = latency;
            double hit = spawner.EffectiveTime(data);
            spawner.Tick(hit - spawner.approachTime + .05);
            var note = spawner.LiveNotes[0];
            Assert.AreNotEqual(note.LandingPosition, (Vector2)note.transform.position);
            float previousZ = note.transform.position.z;

            // Tickを挟まず、実際の本編と同じ判定前の入口へ飛ぶ。
            double firstJudgeTime = hit + latency - spawner.earlyJudgeWindow * .5;
            spawner.RefreshJudgmentWindows(firstJudgeTime);
            Assert.IsTrue(note.IsJudgeable);
            ExactXY(note.LandingPosition, note.transform.position);
            Assert.AreEqual(previousZ, note.transform.position.z, "Zの更新順は変えない");

            double early = spawner.earlyJudgeWindow;
            double late = spawner.LateWindowFor(note);
            for (int sample = 0; sample <= 20; sample++)
            {
                double error = -early + 1e-6 + (early + late - 2e-6) * sample / 20;
                spawner.Tick(hit + latency + error);
                Assert.IsTrue(note.IsJudgeable);
                ExactXY(note.LandingPosition, note.transform.position);
            }
        }
    }

    [Test]
    public void TempoMapAndTotalOffsetAreSharedAndAppliedOnce()
    {
        var chart = Chart(
            new NoteData { time = 4000, x = -1, windSway = 1 },
            new NoteData { time = 4250, x = 1, windSway = 1 });
        chart.beatZeroMs = 250;
        chart.editorTempoMap.Add(new ChartTempoPoint { beat = 0, timeMs = 250 });
        chart.editorTempoMap.Add(new ChartTempoPoint { beat = 4, timeMs = 2250 });
        chart.editorTempoMap.Add(new ChartTempoPoint { beat = 8, timeMs = 3250 });
        spawner.SetChart(chart);
        Assert.IsTrue(spawner.HasWindNotes);
        var field = typeof(NoteSpawner).GetField("windTempoMap",
            BindingFlags.Instance | BindingFlags.NonPublic);
        object map = field.GetValue(spawner);
        Assert.NotNull(map);

        // 曲時計2.5秒 - 合計OFFSET .2秒 = 譜面2300ms = 4.2拍。
        const double now = 2.5;
        spawner.Tick(now);
        Assert.AreEqual(2, spawner.AliveCount);
        foreach (var note in spawner.LiveNotes)
        {
            float sway = (float)Math.Sin(Math.PI * 4.2) * WindRules.DepthScale(note.transform.position.z);
            ExactXY(note.LandingPosition, note.transform.position);
            NearXY(note.LandingPosition + new Vector2(sway, 0), note.VisualTransform.position);
        }
        spawner.Tick(now + .05);
        Assert.AreSame(map, field.GetValue(spawner), "Tickでは拍の地図を作り直さない");

        spawner.SetChart(Chart(new NoteData { time = 4000 }));
        Assert.IsFalse(spawner.HasWindNotes);
        Assert.IsNull(field.GetValue(spawner), "通常譜面には風用の地図を確保しない");
    }

    [Test]
    public void WindlessNotesKeepTheExistingXYBehavior()
    {
        var data = new NoteData { time = 4000, x = -.6f, y = .7f };
        var chart = Chart(data);
        spawner.SetChart(chart);
        double hit = spawner.EffectiveTime(data);
        spawner.Tick(hit - 1);
        var note = spawner.LiveNotes[0];
        Assert.IsFalse(note.HasWind);
        ExactXY(new Vector2(data.x, data.y) * chart.coordScale, note.transform.position);

        note.transform.position = new Vector3(2, -1, note.transform.position.z);
        spawner.Tick(hit - .5);
        ExactXY(new Vector2(2, -1), note.transform.position);
        Assert.AreEqual(spawner.judgeZ + spawner.Speed * .5f, note.transform.position.z);
        ExactXY(new Vector2(2, -1), note.TimingCue.GhostRoot.transform.position);
    }

    [Test]
    public void ResetClearsEveryWindValueAndReinitializesGhostAnchors()
    {
        var data = Wind();
        spawner.SetChart(Chart(data));
        spawner.Tick(spawner.EffectiveTime(data) - 1);
        var note = spawner.LiveNotes[0];
        var cue = note.TimingCue;

        note.ResetForSpawn();
        Assert.IsFalse(note.HasWind);
        Assert.AreEqual(Vector2.zero, note.LandingPosition);
        Assert.AreEqual(0f, note.WindX);
        Assert.AreEqual(0f, note.WindY);
        Assert.AreEqual(0f, note.WindSpin);
        Assert.AreEqual(0f, note.WindSway);
        Assert.AreEqual(Vector3.zero, note.VisualTransform.localPosition);

        var landing = new Vector2(.4f, -.8f);
        note.ConfigureWind(new NoteData(), landing);
        note.transform.position = new Vector3(landing.x, landing.y, spawner.spawnZ);
        cue.Initialize(note, spawner.judgeZ);
        Assert.IsFalse(note.HasWind);
        GhostsAtLanding(note);

        landing = new Vector2(-.8f, .4f);
        note.ConfigureWind(data, landing);
        note.transform.position = new Vector3(2, 1, spawner.spawnZ);
        cue.Initialize(note, spawner.judgeZ);
        GhostsAtLanding(note);
        cue.Tick(.6, spawner.approachTime, spawner.earlyJudgeWindow, spawner.judgeWindow);
        GhostsAtLanding(note);
    }

    [TestCase(1, "none")]
    [TestCase(4, "up")]
    public void VisualsSwayInsideTheWindowWhileJudgmentAndGuidesStayAtLanding(int cuts, string direction)
    {
        var data = Wind(cuts);
        data.direction = direction;
        var chart = Chart(data);
        chart.bpm = 60;
        chart.beatZeroMs = 3300;
        spawner.JudgeLatencySeconds = -.15;
        spawner.SetChart(chart);
        double hit = spawner.EffectiveTime(data);
        spawner.Tick(hit - .8);
        var note = spawner.LiveNotes[0];
        Assert.AreNotSame(note.transform, note.VisualTransform);
        Assert.IsFalse(note.GetComponent<MeshRenderer>().enabled);
        Assert.IsTrue(note.BodyRenderer.enabled);

        foreach (double remaining in new[] { .35, .249, .18, .12, .061, .06, 0d })
        {
            double now = hit - remaining;
            spawner.Tick(now);
            ExactXY(note.LandingPosition, note.transform.position);
            GhostsAtLanding(note);
            if (remaining > .06)
            {
                double beat = (now - spawner.TotalOffsetSeconds) - 3.3;
                float x = WindRules.SwayOffset(data.windSway, remaining, spawner.approachTime, beat)
                    * WindRules.DepthScale(note.transform.position.z);
                Assert.Greater(Mathf.Abs(x), 1e-6f);
                NearXY(note.LandingPosition + new Vector2(x, 0), note.VisualTransform.position);
            }
            else
            {
                Assert.AreEqual(Vector3.zero, note.VisualTransform.localPosition);
                ExactXY(note.LandingPosition, note.VisualTransform.position);
            }
            if (remaining == .249) Assert.IsTrue(note.IsJudgeable);
            if (note.Arrow != null) Assert.AreSame(note.VisualTransform, note.Arrow.transform.parent);
            if (note.countLabel != null)
            {
                Assert.AreSame(note.VisualTransform, note.countLabel.GetComponent<FollowTransformWorldOffset>().target);
                Assert.AreEqual(note.VisualTransform.position + LongNoteCountStyle.WorldOffset, note.countLabel.transform.position);
            }
        }

        // Tickを挟まない判定前の更新も、今の拍・前のZで見た目を進める。
        spawner.Tick(hit - .8);
        float previousZ = note.transform.position.z;
        spawner.RefreshJudgmentWindows(hit - .12);
        Assert.IsTrue(note.IsJudgeable);
        ExactXY(note.LandingPosition, note.transform.position);
        Assert.AreEqual(previousZ, note.transform.position.z);
        float expected = WindRules.SwayOffset(data.windSway, .12, spawner.approachTime, .58)
            * WindRules.DepthScale(previousZ);
        NearXY(note.LandingPosition + new Vector2(expected, 0), note.VisualTransform.position);
        if (cuts > 1)
        {
            note.Cut(note.transform.position, Vector3.up * 5f, CutDirection.Up);
            Assert.AreEqual(cuts - 1, note.RemainingCuts);
            var crack = note.VisualTransform.Find("Crack");
            Assert.NotNull(crack);
            NearXY(note.VisualTransform.position, crack.position);
        }
        spawner.RefreshJudgmentWindows(hit - WindRules.StillSeconds);
        Assert.AreEqual(Vector3.zero, note.VisualTransform.localPosition);
    }

    [Test]
    public void EarlyCutKeepsLogicalHitPointAndCreatesSlicesAndLightAtTheVisibleBody()
    {
        var data = Wind();
        var chart = Chart(data);
        chart.bpm = 60;
        chart.beatZeroMs = 3300;
        spawner.JudgeLatencySeconds = -.15;
        spawner.SetChart(chart);
        spawner.Tick(spawner.EffectiveTime(data) - .2);
        var note = spawner.LiveNotes[0];
        Assert.IsTrue(note.IsJudgeable);
        Vector3 logical = note.transform.position;
        Vector3 visible = note.VisualTransform.position;
        Assert.Greater((visible - logical).sqrMagnitude, .01f);
        // 破片を調べるまで本体を残す。破片はfinallyで必ず片付ける。
        note.IsPooled = true;
        bool observed = false;
        note.OnCut += (n, point, velocity) =>
        {
            observed = true;
            Assert.AreEqual(logical, n.transform.position);
            Assert.AreEqual(logical, point);
            n.NotifyJudgment(JudgmentTier.Great, point, velocity);
        };
        try
        {
            note.Cut(logical, Vector3.right * 5f);
            Assert.IsTrue(observed);
            foreach (string name in new[] { "firstPiece", "secondPiece" })
            {
                var piece = (SlicePieceDecay)typeof(CuttableNote).GetField(name,
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(note);
                Assert.NotNull(piece);
                Assert.AreEqual(visible, piece.transform.position);
            }
            var feedback = root.GetComponentInChildren<GameplayCutFeedback>();
            Assert.AreEqual(1, feedback.ActiveCount);
            var bursts = (Array)typeof(GameplayCutFeedback).GetField("bursts",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(feedback);
            object burst = bursts.GetValue(0);
            var position = (Vector3)burst.GetType().GetField("position").GetValue(burst);
            NearXY(visible, position);
        }
        finally
        {
            foreach (string name in new[] { "firstPiece", "secondPiece" })
            {
                var piece = (SlicePieceDecay)typeof(CuttableNote).GetField(name,
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(note);
                if (piece != null) Object.DestroyImmediate(piece.gameObject);
            }
        }
    }

    [Test]
    public void ReducedEffectsAndMemoryVeilDoNotAlterWindMotion()
    {
        var data = Wind();
        data.veil = .95f;
        spawner.SetChart(Chart(data));
        double now = spawner.EffectiveTime(data) - 1;
        spawner.Tick(now);
        var note = spawner.LiveNotes[0];
        Vector3 ordinary = note.transform.position;
        Vector3 visible = note.VisualTransform.position;
        Assert.AreNotEqual(note.LandingPosition, (Vector2)ordinary);

        DisplaySettings.SetReducedEffectsForTest(true);
        spawner.Tick(now);
        Assert.AreEqual(ordinary, note.transform.position);
        Assert.AreEqual(visible, note.VisualTransform.position);
        var visuals = note.GetComponent<NoteVisuals>();
        visuals.SetVeil(1f);
        Assert.IsFalse(note.BodyRenderer.enabled);
        visuals.SetVeil(.95f);
        Assert.IsTrue(note.BodyRenderer.enabled);
        Assert.IsFalse(note.GetComponent<MeshRenderer>().enabled, "根元の本体を二重に描画しない");
        Assert.AreEqual(.95f, note.Veil);
        Assert.AreEqual(.95f, note.GetComponent<NoteVisuals>().Veil);
        Assert.IsFalse(note.TimingCue.GhostRoot.activeSelf);
        Assert.IsFalse(note.TimingCue.ApproachRoot.activeSelf);
    }
}
