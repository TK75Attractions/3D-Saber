using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

// Manic Sonic の裏譜面 R(反転)の決まり(ReverseRules)。鏡・門・予告音・角度・入力の写し・接近時間。
public class ReverseRulesTests
{
    // Manic Sonic のテンポ表(180 → 160 → 180 → 60 → 180 → 約 200 BPM)。
    internal static ChartTempoMap ManicTempo() => new ChartTempoMap(180, 0, new List<ChartTempoPoint>
    {
        new ChartTempoPoint { beat = 0, timeMs = 0 }, new ChartTempoPoint { beat = 171, timeMs = 57000 },
        new ChartTempoPoint { beat = 199, timeMs = 67500 }, new ChartTempoPoint { beat = 379, timeMs = 127500 },
        new ChartTempoPoint { beat = 395, timeMs = 143500 }, new ChartTempoPoint { beat = 403, timeMs = 146166.6667f },
        new ChartTempoPoint { beat = 420.6175f, timeMs = 151451.9274f },
    });

    internal static ReverseChartData Turns()
    {
        var data = new ReverseChartData { enabled = true };
        data.turns.Add(new ReverseTurnData { time = 127500, endTime = 131500, degrees = 180 });
        data.turns.Add(new ReverseTurnData { time = 149466.6734f, endTime = 151266.677f, degrees = 360 });
        data.creaks.Add(new ReverseCreakData { time = 124500, degrees = 3 });
        data.creaks.Add(new ReverseCreakData { time = 125166.6667f, degrees = -3 });
        data.creaks.Add(new ReverseCreakData { time = 125833.3333f, degrees = 3 });
        return data;
    }

    [TearDown]
    public void TearDown() => DisplaySettings.ResetReducedEffectsCacheForTest();

    [TestCase(CutDirection.None, CutDirection.None)]
    [TestCase(CutDirection.Up, CutDirection.Up)]
    [TestCase(CutDirection.Down, CutDirection.Down)]
    [TestCase(CutDirection.Left, CutDirection.Right)]
    [TestCase(CutDirection.Right, CutDirection.Left)]
    [TestCase(CutDirection.UpLeft, CutDirection.UpRight)]
    [TestCase(CutDirection.UpRight, CutDirection.UpLeft)]
    [TestCase(CutDirection.DownLeft, CutDirection.DownRight)]
    [TestCase(CutDirection.DownRight, CutDirection.DownLeft)]
    public void MirrorFlipsOnlyTheSidewaysPartOfArrows(CutDirection from, CutDirection to)
    {
        Assert.AreEqual(to, ReverseRules.MirrorDirection(from));
        Assert.AreEqual(from, ReverseRules.MirrorDirection(ReverseRules.MirrorDirection(from)));
        Assert.AreEqual(ReverseRules.DirectionName(to), ReverseRules.MirrorDirection(ReverseRules.DirectionName(from)));
    }

    [TestCase(CutDirection.None, CutDirection.None)]
    [TestCase(CutDirection.Up, CutDirection.Down)]
    [TestCase(CutDirection.Left, CutDirection.Right)]
    [TestCase(CutDirection.UpLeft, CutDirection.DownRight)]
    [TestCase(CutDirection.UpRight, CutDirection.DownLeft)]
    public void HalfTurnPointsArrowsTheOtherWay(CutDirection from, CutDirection to)
    {
        Assert.AreEqual(to, ReverseRules.TurnDirection(from));
        Assert.AreEqual(from, ReverseRules.TurnDirection(to));
        Vector2 a = CutDirectionHelper.ToVector(from), b = CutDirectionHelper.ToVector(to);
        Assert.AreEqual(-a.x, b.x, 1e-6);
        Assert.AreEqual(-a.y, b.y, 1e-6);
    }

    [Test]
    public void MirrorNotesShowTheReflectionUntilTheGateAndSlideIntoPlace()
    {
        Assert.AreEqual(1.0, ReverseRules.CrossTime(1.5), 1e-12, "門を通るのは届く 0.5 秒前");
        foreach (double remaining in new[] { 1.5, .9, .5 })
        {
            Assert.AreEqual(-2.2f, ReverseRules.MirrorOffsetX(1.1f, remaining), 1e-6, "門より奥は x が −2x ずれる " + remaining);
            Assert.AreEqual(-1f, ReverseRules.MirrorScaleX(remaining), 1e-6, "拡縮 −1(矢印も鏡)");
        }
        foreach (double remaining in new[] { .42, .3, 0, -.2 })
        {
            Assert.AreEqual(0f, ReverseRules.MirrorOffsetX(1.1f, remaining), 1e-6, "0.08 秒で本当の位置 " + remaining);
            Assert.AreEqual(1f, ReverseRules.MirrorScaleX(remaining), 1e-6);
        }
        float previous = float.NegativeInfinity;
        for (double remaining = .5; remaining >= .42; remaining -= .002)
        {
            float scale = ReverseRules.MirrorScaleX(remaining);
            Assert.GreaterOrEqual(Mathf.Abs(scale), ReverseRules.MinMirrorScale, "拡縮は 0 にしない");
            Assert.GreaterOrEqual(scale, previous - 1e-6f, "押しつぶれて戻る(逆戻りしない)");
            previous = scale;
        }
        Assert.AreEqual(ReverseRules.MirrorBlend(.46), ReverseRules.MirrorBlend(.46), "残り時間だけの関数");
    }

    static ReverseGateData FirstGate()
    {
        var gate = new ReverseGateData { time = 5000, endTime = 10000 };
        gate.on.Add(new ReverseSpanData { time = 5333.3333f, endTime = 10000 });
        return gate;
    }

    static ReverseGateData SecondGate()
    {
        var gate = new ReverseGateData { time = 21333.3333f, endTime = 42000 };
        gate.on.Add(new ReverseSpanData { time = 26000, endTime = 31333.3333f });
        gate.on.Add(new ReverseSpanData { time = 36666.6667f, endTime = 42000 });
        return gate;
    }

    [Test]
    public void GatesArriveStopTurnIntoMirrorsAndBreak()
    {
        var gate = FirstGate();
        Assert.AreEqual(1.0, ReverseRules.ArriveSeconds(1.5), 1e-12, "接近時間 − 0.5 秒前から奥に見える");
        Assert.AreEqual(ReverseRules.GatePhase.None, ReverseRules.GateState(gate, 3.9, 1.5), "止まる前の見える前");
        Assert.AreEqual(ReverseRules.GatePhase.Arriving, ReverseRules.GateState(gate, 4.5, 1.5), "奥から流れてくる");
        Assert.AreEqual(ReverseRules.GatePhase.Pass, ReverseRules.GateState(gate, 5.1, 1.5), "素通し");
        Assert.AreEqual(ReverseRules.GatePhase.Mirror, ReverseRules.GateState(gate, 5.4, 1.5), "鏡");
        Assert.AreEqual(ReverseRules.GatePhase.Mirror, ReverseRules.GateState(gate, 9.99, 1.5));
        Assert.AreEqual(ReverseRules.GatePhase.Broken, ReverseRules.GateState(gate, 10.1, 1.5), "割れた後(破片が飛ぶ間)");
        Assert.AreEqual(ReverseRules.GatePhase.None, ReverseRules.GateState(gate, 10.7, 1.5));

        Assert.AreEqual(0f, ReverseRules.GateFlip(gate, 5.2));
        Assert.AreEqual(90f, ReverseRules.GateFlip(gate, 5.3333333 + ReverseRules.FlipSeconds / 2), .5f, "0.33 秒で裏返る");
        Assert.AreEqual(180f, ReverseRules.GateFlip(gate, 5.7));
        Assert.AreEqual(180f, ReverseRules.GateFlip(gate, 10.2), "割れる時に鏡が終わるなら鏡のまま割れる");
        Assert.AreEqual(0f, ReverseRules.MirrorTint(new ReverseChartData { gates = { gate } }, 5.1));
        Assert.AreEqual(1f, ReverseRules.MirrorTint(new ReverseChartData { gates = { gate } }, 6), 1e-6, "鏡の間は床の線を左赤・右青");
        Assert.AreEqual(0f, ReverseRules.MirrorTint(new ReverseChartData { gates = { gate } }, 10.2), "割れたら戻す");

        var second = SecondGate();
        Assert.AreEqual(180f, ReverseRules.GateFlip(second, 31.0));
        float back = ReverseRules.GateFlip(second, 31.3333333 + .1);
        Assert.Greater(back, 0f);
        Assert.Less(back, 180f, "素通しへ 0.33 秒で戻る");
        Assert.AreEqual(0f, ReverseRules.GateFlip(second, 31.8));
        Assert.AreEqual(ReverseRules.GatePhase.Pass, ReverseRules.GateState(second, 33, 1.5), "拍の土台の句は素通し");
        Assert.Greater(ReverseRules.GateFlip(second, 36.75), 0f, "応答の句で再び鏡に");
        Assert.IsTrue(ReverseRules.MirrorOn(new ReverseChartData { gates = { second } }, 28000));
        Assert.IsFalse(ReverseRules.MirrorOn(new ReverseChartData { gates = { second } }, 33000));
    }

    [Test]
    public void CuesAreTheThreeBeatsBeforeTheMirrorBeatOnTheTempoMap()
    {
        var tempo = ManicTempo();
        var first = ReverseRules.CueTimesMs(tempo, 5333.3333);
        Assert.AreEqual(3, first.Length);
        Assert.AreEqual(4333.333, first[0], .01, "3拍前");
        Assert.AreEqual(4666.667, first[1], .01, "2拍前");
        Assert.AreEqual(5000.0, first[2], .01, "1拍前");
        var late = ReverseRules.CueTimesMs(tempo, 101833.3333);
        Assert.AreEqual(100833.333, late[0], .01);
        Assert.AreEqual(101500.0, late[2], .01);
        // 拍 403 より後は約 200 BPM(0.3 秒/拍)。180 BPM の 0.333 秒ではない。
        double on = tempo.TimeAt(410);
        var fast = ReverseRules.CueTimesMs(tempo, on);
        Assert.AreEqual(on - 900, fast[0], .05);
        Assert.AreEqual(300, fast[2] - fast[1], .05);
    }

    [Test]
    public void TheWorldTurnsOnceAndThenAllTheWayRoundWithCreaksBefore()
    {
        var data = Turns();
        Assert.AreEqual(0f, ReverseRules.TurnAngle(data, 120, false), "127.5 まで 0");
        Assert.AreEqual(0f, ReverseRules.TurnAngle(data, 127.5, false), 1e-4);
        Assert.AreEqual(90f, ReverseRules.TurnAngle(data, 129.5, false), 1e-3, "なめらかに回る(真ん中で 90°)");
        Assert.AreEqual(180f, ReverseRules.TurnAngle(data, 131.5, false), 1e-4, "131.5 以後 180");
        Assert.AreEqual(180f, ReverseRules.TurnAngle(data, 140, false), 1e-4);
        Assert.AreEqual(360f, ReverseRules.TurnAngle(data, 151.3, false), 1e-4, "151.267 以後 360");
        Assert.AreEqual(360f, ReverseRules.TurnAngle(data, 155, false), 1e-4);
        float previous = ReverseRules.TurnAngle(data, 126.2, false);
        for (double t = 126.21; t <= 152.0; t += .01)
        {
            float angle = ReverseRules.TurnAngle(data, t, false);
            Assert.GreaterOrEqual(angle, previous - 1e-4f, "逆戻りしない " + t);
            Assert.LessOrEqual(angle - previous, 2f, "つながっている(1 コマで跳ばない) " + t);
            previous = angle;
        }
        Assert.AreEqual(3f, ReverseRules.TurnAngle(data, 124.55, false), 1e-3, "きしみは +3°");
        Assert.AreEqual(-3f, ReverseRules.TurnAngle(data, 125.2166667, false), 1e-2, "−3°");
        Assert.AreEqual(0f, ReverseRules.TurnAngle(data, 124.75, false), 1e-4, "0.25 秒で戻る");
        Assert.AreEqual(0f, ReverseRules.TurnAngle(data, 124.49, false), 1e-4);

        // F3: 回る動きを見せず、真ん中で段にする。きしみも出さない。
        Assert.AreEqual(0f, ReverseRules.TurnAngle(data, 129.4, true));
        Assert.AreEqual(180f, ReverseRules.TurnAngle(data, 129.6, true));
        Assert.AreEqual(0f, ReverseRules.TurnAngle(data, 124.55, true));
        Assert.AreEqual(180f, ReverseRules.TurnAngle(data, 150.3, true));
        Assert.AreEqual(360f, ReverseRules.TurnAngle(data, 150.4, true));
        Assert.IsTrue(ReverseRules.UpsideDown(180f));
        Assert.IsFalse(ReverseRules.UpsideDown(360f));
        Assert.AreEqual(0f, ReverseRules.Wrap(360f));
        Assert.AreEqual(0f, ReverseRules.Wrap(-720f));
    }

    [Test]
    public void ReducedEffectsDarkenOnlyAroundTheMiddleOfEachTurn()
    {
        var data = Turns();
        Assert.AreEqual(ReverseRules.ReducedDimAlpha, ReverseRules.ReducedDim(data, 129.5), 1e-6);
        Assert.AreEqual(ReverseRules.ReducedDimAlpha, ReverseRules.ReducedDim(data, 129.4), 1e-6);
        Assert.Greater(ReverseRules.ReducedDim(data, 129.27), 0f);
        Assert.AreEqual(0f, ReverseRules.ReducedDim(data, 129.2));
        Assert.AreEqual(0f, ReverseRules.ReducedDim(data, 129.8));
        Assert.AreEqual(ReverseRules.ReducedDimAlpha, ReverseRules.ReducedDim(data, (149.4666734 + 151.266677) / 2), 1e-6);
    }

    [Test]
    public void InputTurnsTheOppositeWayAboutTheBandCenter()
    {
        var p = new Vector2(1.2f, .3f);
        Assert.AreEqual(p, ReverseRules.InputRotate(p, 0f), "0° は恒等");
        Assert.AreEqual(p, ReverseRules.InputRotate(p, 360f));
        Vector2 half = ReverseRules.InputRotate(p, 180f);
        Assert.AreEqual(-1.2f, half.x, 1e-6, "180° は (x, y) → (−x, −0.3 − y)");
        Assert.AreEqual(-.6f, half.y, 1e-6);
        Assert.AreEqual(p, ReverseRules.InputRotate(half, 180f), "180° の写しは自分自身が逆");
        foreach (float degrees in new[] { 3f, 37f, 90f, 180f, 270f })
        {
            Vector2 pivot = ReverseRules.InputRotate(ReverseRules.Pivot, degrees);
            Assert.AreEqual(ReverseRules.Pivot.x, pivot.x, 1e-5, "軸は動かない " + degrees);
            Assert.AreEqual(ReverseRules.Pivot.y, pivot.y, 1e-5);
            Vector2 back = ReverseRules.InputRotate(ReverseRules.InputRotate(p, degrees), -degrees);
            Assert.AreEqual(p.x, back.x, 1e-4);
            Assert.AreEqual(p.y, back.y, 1e-4);
        }
        // 絵を左回りに 90° 回すので、入力は右回りに 90°(軸の右の点は軸の下へ)。
        Vector2 quarter = ReverseRules.InputRotate(ReverseRules.Pivot + Vector2.right, 90f);
        Assert.AreEqual(0f, quarter.x, 1e-5);
        Assert.AreEqual(-1.15f, quarter.y, 1e-5);
        var v3 = ReverseRules.InputRotate(new Vector3(1.2f, .3f, 4f), 180f);
        Assert.AreEqual(4f, v3.z, "奥行きは変えない");
    }

    // 2026-10-28: 下限 1.5 秒をやめ、設定の速さ(3段 2.0・1.0・0.8 秒、既定 1.0 秒)のまま。上限 2.0 秒だけ残す。
    [TestCase(.5, .5)]
    [TestCase(.8, .8)]
    [TestCase(1.0, 1.0)]
    [TestCase(1.8, 1.8)]
    [TestCase(2.0, 2.0)]
    [TestCase(4.0, 2.0)]
    [TestCase(0.0, 1.0)]
    [TestCase(-1.0, 1.0)]
    [TestCase(double.NaN, 1.0)]
    public void ApproachFollowsTheSettingUpToTwoSeconds(double requested, double expected)
    {
        Assert.AreEqual(expected, ReverseRules.EffectiveApproach(requested), 1e-12);
    }

    [Test]
    public void TheDefaultSpeedIsNotSlowedDown()
    {
        Assert.AreEqual(GameSession.NoteApproachTimeDefault, (float)ReverseRules.DefaultApproach, 1e-6, "壊れた設定は既定の速さ");
        foreach (float seconds in NoteSpeedPreset.Seconds)
            Assert.AreEqual(seconds, (float)ReverseRules.EffectiveApproach(seconds), 1e-6, "設定の段はそのまま: " + seconds);
    }

    [Test]
    public void TheFinalHitIsTheLastBeatOfTheSong()
    {
        var turn = Turns().turns[1];
        Assert.AreEqual(151451.93, ReverseRules.FinalHitMs(turn, ManicTempo()), .05, "151.45 秒(曲の最後の拍)");
        Assert.AreEqual(turn.endTime, ReverseRules.FinalHitMs(turn, new ChartTempoMap(180, 0, null)), 1e-3, "テンポ表の点が無ければ回り終わり");
    }
}

// ChartLoader が reverse とノーツの mirror を読み、壊れた値を収めること。ほかの譜面は今までどおり。
public class ReverseLoaderTests
{
    [Test]
    public void LoaderReadsReverseSortsSpansAndDropsMirrorsWithoutAGate()
    {
        // swell・gap・pieces は v1.35 の逆再生の項目(2026-10-28 に外した)。古い譜面に残っていても読み飛ばす。
        string json = @"{""bpm"":180,""notes"":[
            {""time"":6333.3,""x"":-1.05,""y"":-0.15,""color"":""gold"",""mirror"":true,""swell"":true},
            {""time"":4000,""x"":1,""y"":0,""color"":""red"",""mirror"":true},
            {""time"":7000,""x"":1,""y"":0,""color"":""red"",""type"":""long"",""count"":2,""lengthMs"":700,""mirror"":true}],
            ""reverse"":{""enabled"":true,""gap"":9,
              ""pieces"":[{""time"":10666.7,""endTime"":21333.3,""count"":9,""gap"":0.5,""spin"":0},
                          {""time"":0,""endTime"":5333.3,""count"":4,""gap"":0.6,""spin"":0},
                          {""time"":30,""endTime"":10,""count"":2}],
              ""gates"":[{""time"":5000,""endTime"":10000,""on"":[{""time"":5333.3,""endTime"":12000},{""time"":3000,""endTime"":4000}]}],
              ""turns"":[{""time"":149466.7,""endTime"":151266.7,""degrees"":360},{""time"":127500,""endTime"":131500,""degrees"":180}],
              ""creaks"":[{""time"":124500,""degrees"":40}]}}";
        var chart = ChartLoader.Parse(json);
        var reverse = chart.reverse;
        Assert.IsTrue(reverse.enabled);
        Assert.AreEqual(1, reverse.gates[0].on.Count, "門の時間の外の鏡は捨てる");
        Assert.AreEqual(5333.3f, reverse.gates[0].on[0].time, 1e-3);
        Assert.AreEqual(10000f, reverse.gates[0].on[0].endTime, 1e-3, "鏡は門の時間の中へ収める");
        Assert.AreEqual(127500f, reverse.turns[0].time, "回転も時刻順");
        Assert.AreEqual(ReverseRules.MaxCreakDegrees, reverse.creaks[0].degrees, 1e-6);

        var gold = chart.notes.Single(n => Math.Abs(n.time - 6333.3f) < .1f);
        Assert.IsTrue(gold.mirror, "門を通る時刻(5833.3)が鏡の区間にある");
        Assert.IsFalse(chart.notes.Single(n => Math.Abs(n.time - 4000) < .1f).mirror, "門を通る時刻が鏡の区間の外なら鏡を外す");
        Assert.IsFalse(chart.notes.Single(n => n.IsLong).mirror, "ロングは鏡にしない");
    }

    [Test]
    public void ChartsWithoutReverseStayOrdinary()
    {
        var plain = ChartLoader.Parse(@"{""bpm"":120,""notes"":[{""time"":1000,""x"":0,""y"":0,""mirror"":true}]}");
        Assert.IsFalse(plain.reverse.enabled, "reverse の無い譜面は無効");
        Assert.IsFalse(plain.notes[0].mirror, "門の無い譜面で鏡にしない");
        CollectionAssert.IsEmpty(plain.reverse.gates);
        var broken = ChartLoader.Parse(@"{""bpm"":120,""notes"":[],""reverse"":{""enabled"":true,""turns"":[{""time"":5,""endTime"":1,""degrees"":180}]}}");
        Assert.AreEqual(broken.reverse.turns[0].time, broken.reverse.turns[0].endTime, "終わりが頭より前の回転は、その場で切り替える");
        foreach (var difficulty in new[] { "Normal", "Hard" })
        {
            var chart = ChartLoader.LoadFromStreamingAssets("ManicSonic", difficulty);
            Assert.IsFalse(chart.reverse.enabled, difficulty);
            Assert.IsFalse(chart.notes.Any(n => n.mirror), difficulty);
        }
    }
}

// 天地返しの投影(ReverseRules.ViewMatrix と WorldTurn)。カメラはテストが作る(ほかのテストが残した MainCamera を拾わない)。
// カメラは本番の Game シーンと同じ(GamePlayManager の useSlightTopDownView: 高さ 2.35・下向き 12°、FOV 60・near 0.3・far 1000、16:9)。
public class ReverseProjectionTests
{
    readonly List<GameObject> created = new List<GameObject>();
    Camera view;
    static readonly Vector3 Pivot = new Vector3(0f, -.15f, 0f);
    static readonly Vector3[] Band =
    {
        new Vector3(-1.9f, -.6f, 0f), new Vector3(1.9f, -.6f, 0f), new Vector3(-1.9f, .3f, 0f), new Vector3(1.9f, .3f, 0f),
        new Vector3(-.6f, .3f, 0f), new Vector3(1.2f, -.15f, 0f), new Vector3(0f, .3f, 0f),
    };
    // 帯の外(剣を大きく振った所)。
    static readonly Vector3[] Outside =
    {
        new Vector3(3f, 2f, 0f), new Vector3(-4f, -2.5f, 0f), new Vector3(5f, 1f, 0f), new Vector3(-2.5f, 2.8f, 0f),
    };

    // 本番の Game シーンのカメラ(テストでも同じ値で作る)。
    internal static Camera MakeGameCamera(List<GameObject> created, string name)
    {
        var go = new GameObject(name);
        created.Add(go);
        var camera = go.AddComponent<Camera>();
        camera.fieldOfView = 60f;
        camera.nearClipPlane = .3f;
        camera.farClipPlane = 1000f;
        camera.pixelRect = new Rect(0, 0, 1600, 900);
        // ヘッドレス環境では aspect が pixelRect に追従しないため明示する。
        camera.aspect = 1600f / 900f;
        camera.transform.SetPositionAndRotation(new Vector3(0f, 2.35f, -7f), Quaternion.Euler(12f, 0f, 0f));
        camera.ResetProjectionMatrix();
        return camera;
    }

    [SetUp]
    public void SetUp()
    {
        view = MakeGameCamera(created, "ReverseProjectionCamera");
    }

    [TearDown]
    public void TearDown()
    {
        if (WorldTurn.Active != null) WorldTurn.Active.Dispose();
        foreach (var go in created) if (go != null) Object.DestroyImmediate(go);
        created.Clear();
        DisplaySettings.ResetReducedEffectsCacheForTest();
    }

    Matrix4x4 Turned(float degrees)
    {
        view.ResetProjectionMatrix();
        Matrix4x4 projection = view.projectionMatrix;
        Vector2 pivot = ReverseRules.PivotNdc(projection, view.worldToCameraMatrix, Pivot);
        return ReverseRules.ViewMatrix(projection, pivot, degrees, view.aspect);
    }

    static void AssertMatrix(Matrix4x4 expected, Matrix4x4 actual, float tolerance)
    {
        for (int i = 0; i < 16; i++) Assert.AreEqual(expected[i], actual[i], tolerance, "要素 " + i);
    }

    [Test]
    public void ZeroDegreesKeepsTheDefaultProjection()
    {
        Matrix4x4 projection = view.projectionMatrix;
        AssertMatrix(projection, Turned(0f), 0f);
        AssertMatrix(projection, Turned(360f), 0f);
    }

    [Test]
    public void HalfTurnKeepsTheAxisAndMirrorsTheBandThroughIt()
    {
        Vector3 axis = view.WorldToViewportPoint(Pivot);
        Assert.That(axis.y, Is.InRange(.36f, .40f), "軸は画面の下から約 37.8%(ノーツの帯の真ん中)");
        var before = Band.Select(p => view.WorldToViewportPoint(p)).ToArray();
        view.projectionMatrix = Turned(180f);
        Vector3 turnedAxis = view.WorldToViewportPoint(Pivot);
        Assert.AreEqual(axis.x, turnedAxis.x, 1e-4, "軸の点は動かない");
        Assert.AreEqual(axis.y, turnedAxis.y, 1e-4);
        for (int i = 0; i < Band.Length; i++)
        {
            Vector3 after = view.WorldToViewportPoint(Band[i]);
            Assert.AreEqual(2 * axis.x - before[i].x, after.x, .005, "軸について反対側 " + Band[i]);
            Assert.AreEqual(2 * axis.y - before[i].y, after.y, .005);
        }
    }

    // 剣の入力の写し(WorldTurn.MapInput)。写した剣は、回した投影で、回す前の手と同じ画面の位置に出る。帯の中も外も、
    // 逆さ(180°)の間も回っている途中も。ワールドの長さで見ても、写した点を回した投影で見た画面の位置から回す前の平面へ戻すと元の点。
    [TestCase(180f)]
    [TestCase(90f)]
    [TestCase(30f)]
    [TestCase(3f)]
    public void TurnedInputAppearsWhereTheHandIs(float degrees)
    {
        var turn = new WorldTurn(ReverseRulesTests.Turns(), () => double.NaN, 0, 0f, view);
        foreach (var point in Band.Concat(Outside))
        {
            view.ResetProjectionMatrix();
            Vector3 before = view.WorldToViewportPoint(point);
            Vector3 mapped = WorldTurn.MapInput(point, degrees);
            Assert.AreEqual(point.z, mapped.z, "同じ平面(fixedZ)の上");
            view.projectionMatrix = Turned(degrees);
            Vector3 after = view.WorldToViewportPoint(mapped);
            Assert.AreEqual(before.x, after.x, 5e-4, "写した剣は回す前と同じ画面の位置 " + point);
            Assert.AreEqual(before.y, after.y, 5e-4, "写した剣は回す前と同じ画面の位置 " + point);
            Vector3 screen = view.WorldToScreenPoint(mapped);
            view.ResetProjectionMatrix();
            Assert.IsTrue(MouseSaberInput.TryProjectToPlane(screen, 0f, view, out Vector3 seen));
            Assert.Less(Vector2.Distance(seen, point), .005f, "ワールドの長さで " + point);
        }
        turn.Dispose();
    }

    // 前の近似(ワールドの XY で逆に回すだけ)は、本番のカメラでは帯の端で 0.03 を超え、帯の外では大きくずれる(だから使わない)。
    [Test]
    public void TheWorldRotationAloneIsNotEnoughForTheGameCamera()
    {
        var turn = new WorldTurn(ReverseRulesTests.Turns(), () => double.NaN, 0, 0f, view);
        float WorldError(Vector3 point, Vector3 mapped)
        {
            view.projectionMatrix = Turned(180f);
            Vector3 screen = view.WorldToScreenPoint(mapped);
            view.ResetProjectionMatrix();
            Assert.IsTrue(MouseSaberInput.TryProjectToPlane(screen, 0f, view, out Vector3 seen));
            return Vector2.Distance(seen, point);
        }
        var edge = new Vector3(1.9f, .3f, 0f);
        var outside = new Vector3(3f, 2f, 0f);
        Assert.Greater(WorldError(edge, ReverseRules.InputRotate(edge, 180f)), .03f, "近似は帯の端で 0.03 を超える");
        Assert.Greater(WorldError(outside, ReverseRules.InputRotate(outside, 180f)), .3f, "帯の外ではもっとずれる");
        Assert.Less(WorldError(edge, WorldTurn.MapInput(edge, 180f)), .005f, "正確な写し");
        Assert.Less(WorldError(outside, WorldTurn.MapInput(outside, 180f)), .005f);
        turn.Dispose();
        Assert.AreEqual(ReverseRules.InputRotate(edge, 180f), WorldTurn.MapInput(edge, 180f), "天地返しが無いときは近似");
    }

    // 一時停止中も、止まった曲の時刻で投影を作り直す(止まっている間に画面の比が変わっても、古い比のまま残らない)。
    [Test]
    public void PausedApplyFollowsANewAspect()
    {
        // Turned はカメラの投影を一度戻すので、比べる行列は Apply の前に作る。
        Matrix4x4 wide = Turned(180f);
        var turn = new WorldTurn(ReverseRulesTests.Turns(), () => 135, 0, 0f, view);
        turn.Apply(135);
        AssertMatrix(wide, view.projectionMatrix, 1e-6f);
        view.aspect = 4f / 3f;
        Matrix4x4 narrow = Turned(180f);
        turn.Apply(135);
        AssertMatrix(narrow, view.projectionMatrix, 1e-6f);
        Assert.AreNotEqual(wide.m00, narrow.m00, "比が変わったら行列も変わる");
        Vector3 axis = view.WorldToViewportPoint(Pivot);
        view.ResetProjectionMatrix();
        Vector3 upright = view.WorldToViewportPoint(Pivot);
        Assert.AreEqual(upright.x, axis.x, 1e-4, "新しい比でも軸は動かない");
        Assert.AreEqual(upright.y, axis.y, 1e-4);
        turn.Dispose();
    }

    [Test]
    public void MouseRayUnderTheTurnedViewHitsTheWorldPointDrawnThere()
    {
        view.projectionMatrix = Turned(180f);
        var world = new Vector3(1.3f, .2f, 0f);
        Vector3 screen = view.WorldToScreenPoint(world);
        Assert.IsTrue(MouseSaberInput.TryProjectToPlane(screen, 0f, view, out Vector3 hit));
        Assert.AreEqual(world.x, hit.x, 1e-3, "マウスは写さなくても正しい(投影ごと回る)");
        Assert.AreEqual(world.y, hit.y, 1e-3);
    }

    [Test]
    public void WorldTurnFollowsTheSongClockAndAlwaysRestoresTheProjection()
    {
        // Turned はカメラの投影を一度戻すので、比べる行列は先に作っておく。
        Matrix4x4 expectedTurned = Turned(180f);
        view.ResetProjectionMatrix();
        Matrix4x4 normal = view.projectionMatrix;
        double clock = 100;
        var turn = new WorldTurn(ReverseRulesTests.Turns(), () => clock, 0, 0f, view);
        Assert.AreSame(turn, WorldTurn.Active);
        turn.Apply(100);
        Assert.IsFalse(turn.ProjectionApplied, "0° のときは触らない");
        AssertMatrix(normal, view.projectionMatrix, 0f);
        Assert.AreEqual(0f, WorldTurn.InputDegrees);

        clock = 135;
        Assert.AreEqual(180f, WorldTurn.InputDegrees, 1e-4, "剣の入力は同じ曲の時計の角度");
        turn.Apply(135);
        Assert.IsTrue(turn.ProjectionApplied);
        Assert.AreEqual(180f, WorldTurn.CurrentDegrees, 1e-4);
        AssertMatrix(expectedTurned, view.projectionMatrix, 1e-6f);
        clock = double.NaN;
        Assert.AreEqual(0f, WorldTurn.InputDegrees, "時計が無ければ回さない");

        turn.Apply(152);
        Assert.IsFalse(turn.ProjectionApplied, "一回転して元に戻ったら既定の投影");
        AssertMatrix(normal, view.projectionMatrix, 1e-6f);

        turn.Apply(135);
        turn.Dispose();
        Assert.IsTrue(turn.Disposed);
        Assert.IsNull(WorldTurn.Active, "入力の写しも止める");
        Assert.AreEqual(0f, WorldTurn.InputDegrees);
        AssertMatrix(normal, view.projectionMatrix, 1e-6f);
    }

    [Test]
    public void ReducedEffectsSwitchTheWorldInTheDark()
    {
        DisplaySettings.SetReducedEffectsForTest(true);
        var turn = new WorldTurn(ReverseRulesTests.Turns(), () => 0, 0, 0f, view);
        turn.Apply(129.4);
        Assert.AreEqual(0f, turn.Degrees, "回る動きを見せない");
        Assert.AreEqual(ReverseRules.ReducedDimAlpha, turn.DimAlpha, 1e-6, "暗い間に切り替える");
        var dim = GameObject.Find("ReverseTurnDim");
        Assert.NotNull(dim);
        Assert.IsTrue(dim.activeSelf);
        turn.Apply(129.6);
        Assert.AreEqual(180f, turn.Degrees);
        turn.Apply(130.2);
        Assert.AreEqual(0f, turn.DimAlpha);
        Assert.IsFalse(dim.activeSelf);
        turn.Apply(124.55);
        Assert.AreEqual(0f, turn.Degrees, "きしみも出さない");
        turn.Dispose();
        Assert.IsTrue(dim == null, "暗転の幕も片付ける");
    }
}

// 鏡門の鏡像(CuttableNote.Reverse・NoteSpawner.Reverse)。プールの無い EditMode で毎回作るノーツを使う。
public class ReverseNoteTests
{
    GameObject root;
    NoteSpawner spawner;
    ScoreManager score;

    [SetUp]
    public void SetUp()
    {
        DisplaySettings.SetReducedEffectsForTest(false);
        root = new GameObject("ReverseNoteTests");
        var prefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
        prefab.transform.SetParent(root.transform, false);
        prefab.AddComponent<CuttableNote>();
        prefab.SetActive(false);
        spawner = root.AddComponent<NoteSpawner>();
        spawner.notePrefab = prefab;
        spawner.noteRoot = root.transform;
        spawner.reuseNotes = false;
        spawner.approachTime = 1.5f;
        spawner.ConfigureFloorGuide(-3f);
        score = root.AddComponent<ScoreManager>();
        score.Reset();
        score.Bind(spawner);
    }

    [TearDown]
    public void TearDown()
    {
        if (root != null)
            foreach (var note in root.GetComponentsInChildren<CuttableNote>(true))
                if (note.TimingCue != null)
                {
                    if (note.TimingCue.GhostRoot != null) Object.DestroyImmediate(note.TimingCue.GhostRoot);
                    if (note.TimingCue.ApproachRoot != null) Object.DestroyImmediate(note.TimingCue.ApproachRoot);
                }
        if (root != null) Object.DestroyImmediate(root);
        foreach (var piece in Object.FindObjectsByType<SlicePieceDecay>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (piece != null) Object.DestroyImmediate(piece.gameObject);
        foreach (var link in Object.FindObjectsByType<SimultaneousNoteLink>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (link != null) Object.DestroyImmediate(link.gameObject);
        DisplaySettings.ResetReducedEffectsCacheForTest();
    }

    // 門の前のふつうのノーツ、門(4〜9 秒、鏡は 5 秒から)の鏡のノーツ2つ、ロング1つ。
    internal static ChartData Chart()
    {
        var chart = new ChartData { bpm = 180 };
        chart.reverse.enabled = true;
        var gate = new ReverseGateData { time = 4000, endTime = 9000 };
        gate.on.Add(new ReverseSpanData { time = 5000, endTime = 9000 });
        chart.reverse.gates.Add(gate);
        chart.notes.Add(new NoteData { time = 2000, x = -1.2f, y = -.15f, color = "blue" });
        chart.notes.Add(new NoteData { time = 6500, x = -1.1f, y = .1f, color = "red", mirror = true });
        chart.notes.Add(new NoteData { time = 7000, x = 1.1f, y = -.15f, color = "blue", type = "direction", direction = "left", mirror = true });
        chart.notes.Add(new NoteData { time = 8000, x = .6f, y = -.6f, color = "red", type = "long", count = 2, lengthMs = 700 });
        chart.reverse = ReverseRules.Normalize(chart.reverse, chart.notes);
        return chart;
    }

    CuttableNote Live(double hit) => spawner.LiveNotes.FirstOrDefault(n => n != null && Math.Abs(n.HitTime - hit) < 1e-6);

    [Test]
    public void MirrorNotesKeepTheirRootOnTheTrueLandingAndShowTheReflectionUntilTheGate()
    {
        spawner.SetChart(Chart());
        spawner.Tick(5.01);
        var note = Live(6.5);
        Assert.NotNull(note);
        Assert.IsTrue(note.HasReverse);
        Assert.IsTrue(note.IsMirror);
        Assert.AreNotSame(note.transform, note.VisualTransform, "見た目の子へ移す");
        Assert.AreSame(note.VisualTransform, note.BodyRenderer.transform, "本体の描画先は見た目の子");
        Assert.AreEqual(-1.1f, note.transform.position.x, 1e-5, "根元は出現から本当の着地点");
        Assert.AreEqual(.1f, note.transform.position.y, 1e-5);
        Assert.AreEqual(1.1f, note.VisualTransform.position.x, 1e-4, "門より奥は本当の位置の左右の鏡像");
        Assert.AreEqual(-1f, note.VisualTransform.localScale.x, 1e-6, "x の拡縮が −1");
        Assert.AreEqual(-1.1f, note.TimingCue.GhostRoot.transform.position.x, 1e-4, "着地の枠は本当の x");

        spawner.Tick(5.9);
        Assert.AreEqual(-1.1f, note.transform.position.x, 1e-5);
        Assert.AreEqual(1.1f, note.VisualTransform.position.x, 1e-4, "門を通る 0.5 秒前までは鏡像のまま");

        var arrow = Live(7.0);
        Assert.NotNull(arrow);
        Assert.Less(arrow.Arrow.transform.lossyScale.x, 0f, "矢印も鏡");

        spawner.Tick(6.2);
        Assert.AreEqual(-1.1f, note.VisualTransform.position.x, 1e-4, "門を通ったら 0.08 秒で本当の位置");
        Assert.AreEqual(1f, note.VisualTransform.localScale.x, 1e-6);
        Assert.AreEqual(-1.1f, note.transform.position.x, 1e-5);
        spawner.Tick(6.6);
        Assert.Greater(arrow.Arrow.transform.lossyScale.x, 0f, "門を通った矢印は本当の向き");
        Assert.AreEqual(CutDirection.Left, arrow.RequiredDirection, "判定の向きは最初から本当の向き");
    }

    [Test]
    public void SkippingFramesPastTheGateStillPutsTheNoteInPlace()
    {
        spawner.SetChart(Chart());
        spawner.Tick(5.01);
        var note = Live(6.5);
        spawner.RefreshJudgmentWindows(6.3);
        Assert.AreEqual(-1.1f, note.VisualTransform.position.x, 1e-4, "判定の前の更新でも、残り時間だけで本当の位置");
        Assert.AreEqual(1f, note.VisualTransform.localScale.x, 1e-6);
    }

    [Test]
    public void FloorMarksAreOnTheTrueSideAndFaintUntilTheGate()
    {
        spawner.SetChart(Chart());
        spawner.Tick(5.01);
        spawner.Tick(5.4);
        var note = Live(6.5);
        float z = note.transform.position.z;
        var mesh = spawner.FloorGuide.GetComponent<MeshFilter>().sharedMesh;
        var vertices = mesh.vertices;
        var colors = mesh.colors;
        var marks = Enumerable.Range(0, vertices.Length).Where(i => Mathf.Abs(vertices[i].z - z) < .4f).ToList();
        Assert.IsNotEmpty(marks, "床の印");
        Assert.AreEqual(-1.1f, marks.Average(i => vertices[i].x), .05, "床の印は本当の x");
        Assert.LessOrEqual(marks.Max(i => colors[i].a), ReverseRules.FloorMarkBeforeCross + 1e-4f, "門を通るまでは 0.5 倍の濃さ");

        spawner.Tick(6.2);
        z = note.transform.position.z;
        mesh = spawner.FloorGuide.GetComponent<MeshFilter>().sharedMesh;
        vertices = mesh.vertices;
        colors = mesh.colors;
        marks = Enumerable.Range(0, vertices.Length).Where(i => Mathf.Abs(vertices[i].z - z) < .4f).ToList();
        Assert.Greater(marks.Max(i => colors[i].a), .9f, "通ったら等倍");
    }

    // 2026-10-28: 逆再生(2片)を外したので、鏡でないノーツは見た目の子を持たないふつうのノーツ。
    [Test]
    public void PlainNotesInTheReverseChartAreOrdinaryNotes()
    {
        spawner.SetChart(Chart());
        spawner.Tick(.51);
        var note = Live(2.0);
        Assert.NotNull(note);
        Assert.IsFalse(note.HasReverse, "鏡でないノーツは R の見た目を使わない");
        Assert.IsFalse(note.IsMirror);
        Assert.AreSame(note.transform, note.VisualTransform, "見た目は根元のまま");
        Assert.AreSame(note.GetComponent<MeshRenderer>(), note.BodyRenderer, "本体は根元の立方体");
        Assert.IsNull(note.transform.Find("ReverseVisual"), "見た目の子を作らない");
        spawner.Tick(1.9);
        Assert.AreEqual(-1.2f, note.VisualTransform.position.x, 1e-5);
    }

    // 設定の既定の速さ(接近 1.0 秒)でも、門(届く 0.5 秒前)より奥では鏡像の側に見える。
    [Test]
    public void AtTheDefaultSpeedMirrorNotesStillShowTheReflectionBeforeTheGate()
    {
        spawner.approachTime = (float)ReverseRules.EffectiveApproach(GameSession.NoteApproachTimeDefault);
        Assert.AreEqual(1f, spawner.approachTime, 1e-6, "R でも既定の速さのまま");
        spawner.SetChart(Chart());
        spawner.Tick(5.51);
        var note = Live(6.5);
        Assert.NotNull(note, "接近 1.0 秒なので 5.5 秒に出る");
        Assert.AreEqual(1.1f, note.VisualTransform.position.x, 1e-4, "出てから門までは鏡像の側");
        Assert.AreEqual(-1.1f, note.transform.position.x, 1e-5, "根元は本当の位置");
        spawner.Tick(5.95);
        Assert.AreEqual(1.1f, note.VisualTransform.position.x, 1e-4, "門を通る 0.5 秒前までは鏡像のまま");
        spawner.Tick(6.1);
        Assert.AreEqual(-1.1f, note.VisualTransform.position.x, 1e-4, "門を通ったら本当の位置");
    }

    [Test]
    public void CuttingAMirrorNoteSlicesTheWholeCubeAndScoresNormally()
    {
        spawner.SetChart(Chart());
        spawner.Tick(5.01);
        var note = Live(6.5);
        spawner.Tick(6.47);
        Assert.IsTrue(note.IsMirror);
        Assert.AreNotSame(note.transform, note.VisualTransform);
        var earlier = new HashSet<SlicePieceDecay>(Object.FindObjectsByType<SlicePieceDecay>(FindObjectsInactive.Include, FindObjectsSortMode.None));
        Assert.IsTrue(note.CutAtSongTime(note.transform.position, Vector3.down * 8f, CutDirection.None, SaberHand.Right, 6.49));
        var pieces = Object.FindObjectsByType<SlicePieceDecay>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Where(p => !earlier.Contains(p) && p.GetComponent<MeshFilter>().sharedMesh == p.ReusableMesh).ToList();
        Assert.AreEqual(2, pieces.Count, "見た目の子の本体の立方体を振った向きで2つに切る");
        var bounds = pieces[0].ReusableMesh.bounds;
        bounds.Encapsulate(pieces[1].ReusableMesh.bounds);
        Assert.AreEqual(1f, bounds.size.x, 1e-3, "立方体全体");
        Assert.AreEqual(1f, bounds.size.y, 1e-3);
        Assert.AreEqual(1, score.PerfectCount, "判定・採点はふつう");
    }

    [Test]
    public void LongNotesStayOrdinaryAndPoolKeysKeepMirrorNotesApart()
    {
        var chart = Chart();
        spawner.SetChart(chart);
        spawner.Tick(6.6);
        var longNote = Live(8.0);
        Assert.NotNull(longNote);
        Assert.IsFalse(longNote.HasReverse, "ロングは鏡にしない");
        Assert.AreSame(longNote.transform, longNote.VisualTransform);

        var key = typeof(NoteSpawner).GetMethod("Key", BindingFlags.Instance | BindingFlags.NonPublic);
        var tap = new NoteData { color = "red" };
        var mirror = new NoteData { color = "red", mirror = true };
        var hold = new NoteData { color = "red", type = "long", count = 2 };
        object reverseTap = key.Invoke(spawner, new object[] { tap });
        object reverseMirror = key.Invoke(spawner, new object[] { mirror });
        object reverseHold = key.Invoke(spawner, new object[] { hold });
        Assert.AreNotEqual(reverseTap, reverseMirror, "見た目の子を持つ鏡のノーツは別のプール");
        spawner.SetChart(new ChartData { bpm = 120 });
        Assert.AreEqual(reverseTap, key.Invoke(spawner, new object[] { tap }), "鏡でないタップはふつうのタップと同じ");
        Assert.AreNotEqual(reverseMirror, key.Invoke(spawner, new object[] { mirror }), "鏡のノーツをほかの曲へ持ち越さない");
        Assert.AreEqual(reverseHold, key.Invoke(spawner, new object[] { hold }), "ロングはふつうのロングと同じ");
    }

    [Test]
    public void ReuseClearsTheOffsetAndTheNegativeScale()
    {
        spawner.SetChart(Chart());
        spawner.Tick(5.01);
        var note = Live(6.5);
        spawner.Tick(5.3);
        Assert.AreEqual(-1f, note.VisualTransform.localScale.x, 1e-6);
        note.ResetForSpawn();
        Assert.IsFalse(note.HasReverse);
        Assert.IsFalse(note.IsMirror);
        Assert.AreEqual(Vector3.zero, note.VisualTransform.localPosition, "鏡のずれを持ち越さない");
        Assert.AreEqual(Vector3.one, note.VisualTransform.localScale, "負の拡縮を持ち越さない");
        Assert.IsTrue(note.BodyRenderer.enabled, "本体を描く");
    }
}

// ReverseMode(門・天地返し・音・光・剣の履歴)。カメラはテストが作って渡す。EditMode では OnDestroy が呼ばれないので EndPlay を呼ぶ。
public class ReverseModeTests
{
    readonly List<GameObject> created = new List<GameObject>();
    GameObject root;
    NoteSpawner spawner;
    ScoreManager score;
    Camera view;
    ReverseMode mode;
    double clock;

    [SetUp]
    public void SetUp()
    {
        DisplaySettings.SetReducedEffectsForTest(false);
        view = ReverseProjectionTests.MakeGameCamera(created, "ReverseModeCamera");
        root = new GameObject("ReverseModeTests");
        created.Add(root);
        var prefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
        prefab.transform.SetParent(root.transform, false);
        prefab.AddComponent<CuttableNote>();
        prefab.SetActive(false);
        spawner = root.AddComponent<NoteSpawner>();
        spawner.notePrefab = prefab;
        spawner.noteRoot = root.transform;
        spawner.reuseNotes = false;
        spawner.approachTime = 1.5f;
        spawner.ConfigureFloorGuide(-3f);
        score = root.AddComponent<ScoreManager>();
        score.Reset();
        score.Bind(spawner);
    }

    [TearDown]
    public void TearDown()
    {
        if (mode != null) mode.EndPlay();
        if (WorldTurn.Active != null) WorldTurn.Active.Dispose();
        if (root != null)
            foreach (var note in root.GetComponentsInChildren<CuttableNote>(true))
                if (note.TimingCue != null)
                {
                    if (note.TimingCue.GhostRoot != null) Object.DestroyImmediate(note.TimingCue.GhostRoot);
                    if (note.TimingCue.ApproachRoot != null) Object.DestroyImmediate(note.TimingCue.ApproachRoot);
                }
        foreach (var go in created) if (go != null) Object.DestroyImmediate(go);
        created.Clear();
        foreach (var piece in Object.FindObjectsByType<SlicePieceDecay>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (piece != null) Object.DestroyImmediate(piece.gameObject);
        DisplaySettings.ResetReducedEffectsCacheForTest();
    }

    // ReverseNoteTests の譜面に、天地返し(ちょうつがいの金・きしみ・最後の一回転)を足したもの。
    ChartData Chart()
    {
        var chart = ReverseNoteTests.Chart();
        var turns = ReverseRulesTests.Turns();
        chart.reverse.turns = turns.turns;
        chart.reverse.creaks = turns.creaks;
        chart.editorTempoMap = new List<ChartTempoPoint>(ReverseRulesTests.ManicTempo().Points);
        chart.notes.Add(new NoteData { time = 127500, x = 0f, y = -.15f, color = "gold" });
        chart.notes.Add(new NoteData { time = 135500, x = 1.9f, y = -.15f, color = "blue" });
        chart.notes = chart.notes.OrderBy(n => n.time).ToList();
        chart.reverse = ReverseRules.Normalize(chart.reverse, chart.notes);
        return chart;
    }

    ReverseMode Make(ChartData chart, SaberCutJudge judge = null)
    {
        spawner.SetChart(chart);
        mode = ReverseMode.Create(root.transform, chart, spawner, () => clock, judge, null, null, view);
        Assert.NotNull(mode);
        return mode;
    }

    [Test]
    public void OnlyChartsWithReverseMakeTheMode()
    {
        Assert.IsNull(ReverseMode.Create(root.transform, new ChartData { bpm = 120 }, spawner, () => 0, null, null, null, view));
        Assert.IsNull(WorldTurn.Active);
    }

    [Test]
    public void TickTurnsTheViewTintsTheFloorAndEndPlayRestoresEverything()
    {
        Matrix4x4 normal = view.projectionMatrix;
        Make(Chart());
        Assert.AreEqual(127.5, mode.HingeSeconds, 1e-6, "ちょうつがいの金");
        Assert.AreEqual(151.4519, mode.FinalSeconds, 1e-3, "最後の一回転が着く時刻(曲の最後の拍)");

        mode.Tick(6.0);
        Assert.AreEqual(1f, mode.Gates.Tint, 1e-6);
        Assert.AreEqual(1f, spawner.FloorGuide.CountTintAmount, 1e-6, "鏡の間は床の線を左赤・右青");
        mode.Tick(9.7);
        Assert.AreEqual(0f, spawner.FloorGuide.CountTintAmount, "鏡の外では必ず戻す(カウントインと共有)");

        mode.Tick(135);
        Assert.AreEqual(180f, WorldTurn.CurrentDegrees, 1e-4);
        Assert.IsTrue(mode.Turn.ProjectionApplied, "135 秒で投影が回っている");
        mode.Tick(6.0);
        mode.EndPlay();
        Assert.IsTrue(mode.Ended);
        Assert.IsNull(WorldTurn.Active, "入力の写しを戻す");
        for (int i = 0; i < 16; i++) Assert.AreEqual(normal[i], view.projectionMatrix[i], 1e-6, "投影を戻す");
        Assert.AreEqual(0f, spawner.FloorGuide.CountTintAmount, "床の塗り分けを戻す");
        Assert.IsTrue(mode.Gates.Renderer == null || !mode.Gates.Renderer.enabled);
        mode.Tick(135);
        Assert.IsNull(WorldTurn.Active, "終わったあとは動かない");
    }

    // GamePlayManager はフレームの最初に ApplyView(一時停止中も)、判定の前に Advance(曲が進む間だけ)を呼ぶ。
    [Test]
    public void ApplyViewOnlyTurnsTheViewAndAdvanceLeavesItAlone()
    {
        Matrix4x4 normal = view.projectionMatrix;
        Make(Chart());
        mode.ApplyView(135);
        Assert.IsTrue(mode.Turn.ProjectionApplied, "投影は ApplyView で当てる");
        Assert.AreEqual(ReverseRules.GatePhase.None, mode.Gates.Phase, "門・音・光は進めない(止まっている間も投影だけ作り直す)");
        Assert.AreEqual(0, mode.StreaksStarted);
        mode.Advance(5.6);
        Assert.AreEqual(ReverseRules.GatePhase.Mirror, mode.Gates.Phase, "門は Advance で進める");
        Assert.AreEqual(180f, WorldTurn.CurrentDegrees, 1e-4, "Advance は投影に触らない");
        mode.EndPlay();
        for (int i = 0; i < 16; i++) Assert.AreEqual(normal[i], view.projectionMatrix[i], 1e-6);
    }

    [Test]
    public void TheGateArrivesStopsTurnsIntoAMirrorAndShatters()
    {
        Make(Chart());
        mode.Tick(2.9);
        Assert.AreEqual(ReverseRules.GatePhase.None, mode.Gates.Phase);
        mode.Tick(3.5);
        Assert.AreEqual(ReverseRules.GatePhase.Arriving, mode.Gates.Phase);
        Assert.Greater(mode.Gates.DrawnZ, mode.Gates.GateZ, "奥から流れてくる");
        mode.Tick(4.2);
        Assert.AreEqual(ReverseRules.GatePhase.Pass, mode.Gates.Phase);
        Assert.AreEqual(spawner.judgeZ + spawner.Speed * .5f, mode.Gates.DrawnZ, 1e-4, "届く 0.5 秒前の深さで止まる");
        Assert.AreEqual(ReverseRules.PassGlow, mode.Gates.Glow, 1e-6, "素通しの光は 40%");
        Assert.IsTrue(mode.Gates.Renderer.enabled);
        Assert.Greater(mode.Gates.Mesh.vertexCount, 0);
        mode.Tick(5.6);
        Assert.AreEqual(ReverseRules.GatePhase.Mirror, mode.Gates.Phase);
        Assert.AreEqual(180f, mode.Gates.Flip, 1e-4, "縦の軸で裏返る");
        Assert.AreEqual(ReverseRules.MirrorGlow, mode.Gates.Glow, 1e-6);
        mode.Tick(9.2);
        Assert.AreEqual(ReverseRules.GatePhase.Broken, mode.Gates.Phase);
        Assert.Greater(mode.Gates.ShardsDrawn, 0, "ガラスの破片になって飛ぶ");
        mode.Tick(9.7);
        Assert.AreEqual(ReverseRules.GatePhase.None, mode.Gates.Phase);
    }

    [Test]
    public void ReducedEffectsFadeTheBrokenGateAndDrawNoStreak()
    {
        DisplaySettings.SetReducedEffectsForTest(true);
        Make(Chart());
        spawner.Tick(5.01);
        mode.Tick(5.9);
        mode.Tick(6.02);
        Assert.AreEqual(0, mode.Gates.StreaksDrawn, "F3 では筋・波紋を出さない");
        mode.Tick(9.1);
        Assert.AreEqual(0, mode.Gates.ShardsDrawn, "F3 では破片の代わりに薄くなって消える");
        Assert.Greater(mode.Gates.Mesh.vertexCount, 0);
        mode.Tick(9.35);
        Assert.AreEqual(0, mode.Gates.Mesh.vertexCount, "0.3 秒で消える");
    }

    [Test]
    public void MirrorNotesCrossingTheGateDrawOneStreakEach()
    {
        Make(Chart());
        spawner.Tick(5.01);
        mode.Tick(5.98);
        Assert.AreEqual(0, mode.StreaksStarted);
        mode.Tick(6.02);
        Assert.AreEqual(1, mode.StreaksStarted, "門を通った瞬間");
        Assert.AreEqual(1, mode.Gates.StreaksDrawn);
        mode.Tick(6.05);
        Assert.AreEqual(1, mode.StreaksStarted, "1つのノーツに1本");
        mode.Tick(6.25);
        Assert.AreEqual(0, mode.Gates.StreaksDrawn, "0.2 秒で消える");
        // コマ飛びで門を飛び越しても、少し遅れて出る(7.0 のノーツは 6.5 秒に門を通る)。
        spawner.Tick(6.55);
        mode.Tick(6.55);
        Assert.AreEqual(2, mode.StreaksStarted, "次の鏡のノーツ(7.0)も");
    }

    [Test]
    public void HingeCutFinalTurnAndShatterAskForEdgeFlashes()
    {
        Make(Chart());
        spawner.Tick(126.2);
        var hinge = spawner.LiveNotes.Single(n => Math.Abs(n.HitTime - 127.5) < 1e-6);
        Assert.IsTrue(hinge.IsGold);
        spawner.Tick(127.5);
        int before = mode.FlashRequests;
        Assert.IsTrue(hinge.CutAtSongTime(hinge.transform.position, Vector3.down * 8f, CutDirection.None, SaberHand.Any, 127.5));
        Assert.AreEqual(before + 1, mode.FlashRequests, "ちょうつがいを切った瞬間に縁の光を1回");

        mode.Tick(8.9);
        mode.Tick(9.05);
        Assert.AreEqual(before + 2, mode.FlashRequests, "門が割れるとき");
        mode.Tick(151.3);
        mode.Tick(151.5);
        Assert.AreEqual(before + 3, mode.FlashRequests, "最後の一回転が着いたとき");
        mode.Tick(151.6);
        Assert.AreEqual(before + 3, mode.FlashRequests, "1回だけ");
    }

    [Test]
    public void AJumpInTheInputTurnDropsTheSaberHistory()
    {
        var saber = new GameObject("ReverseModeSaber");
        created.Add(saber);
        var tracker = saber.AddComponent<SaberTracker>();
        var bridge = saber.AddComponent<SaberInputBridge>();
        bridge.useInputPoint = false;
        var judge = saber.AddComponent<SaberCutJudge>();
        judge.autonomous = false;
        judge.saber = tracker;
        judge.bladeProvider = bridge;
        Make(Chart(), judge);
        var applied = typeof(SaberInputBridge).GetProperty(nameof(SaberInputBridge.AppliedTurnDegrees));
        applied.SetValue(bridge, 0f);
        mode.Tick(129.4);
        int version = tracker.ResetVersion;
        applied.SetValue(bridge, 180f);
        mode.Tick(129.6);
        Assert.AreEqual(1, mode.SaberResets, "F3 の段の切り替えで剣の速さの履歴を捨てる");
        Assert.AreNotEqual(version, tracker.ResetVersion);
        applied.SetValue(bridge, 181.5f);
        mode.Tick(129.7);
        Assert.AreEqual(1, mode.SaberResets, "なめらかな回転では捨てない");
    }
}

// 音の時刻と予約(ReverseSfx)。EditMode では出音の代わりに、声へ渡した再生要求を数える。
public class ReverseSfxTests
{
    readonly List<GameObject> created = new List<GameObject>();
    ReverseSfx sfx;

    [TearDown]
    public void TearDown()
    {
        sfx?.Dispose();
        sfx = null;
        foreach (var go in created) if (go != null) Object.DestroyImmediate(go);
        created.Clear();
        DisplaySettings.ResetReducedEffectsCacheForTest();
    }

    ReverseSfx Make(List<ReverseSfx.Event> events)
    {
        var go = new GameObject("ReverseSfxTests");
        created.Add(go);
        sfx = new ReverseSfx(go.transform, events);
        return sfx;
    }

    [Test]
    public void TheReverseCymbalPeaksAtItsEndAndStopsShort()
    {
        // 最後の一回転のせり上がり(WorldFinaleSwell)に使う。
        var samples = FlourishSfx.ReverseCymbal();
        Assert.AreEqual(FlourishSfx.ReverseCymbalSeconds * SfxLab.Rate, samples.Length, 2);
        int peak = 0;
        for (int i = 1; i < samples.Length; i++) if (Mathf.Abs(samples[i]) > Mathf.Abs(samples[peak])) peak = i;
        Assert.Greater(peak, samples.Length * .9, "終わりでいちばん大きい");
        Assert.LessOrEqual(SfxLab.PeakDb(samples), -13.9);
        Assert.AreEqual(0f, samples[samples.Length - 1], 1e-3, "ぷつっと切れる(最後は 0)");
    }

    [Test]
    public void EventsAreScheduledAheadAndPlayedAgainAfterAStop()
    {
        DisplaySettings.SetReducedEffectsForTest(false);
        Make(new List<ReverseSfx.Event>
        {
            new ReverseSfx.Event { At = 10, Cue = ReverseCue.Cue, Length = .09 },
            new ReverseSfx.Event { At = 11, Cue = ReverseCue.Rumble, Length = 4 },
            new ReverseSfx.Event { At = 12, Cue = ReverseCue.Creak, Length = .32 },
            new ReverseSfx.Event { At = 20 - .667, Cue = ReverseCue.FinaleSwell, Length = .667 },
        });
        sfx.Tick(9.0);
        Assert.AreEqual(0, sfx.Played(ReverseCue.Cue));
        sfx.Tick(9.9);
        Assert.AreEqual(1, sfx.Played(ReverseCue.Cue), "曲の時計で少し前に予約する");
        Assert.AreEqual(.1, sfx.LastDelay, 1e-9);
        sfx.Tick(10.5);
        Assert.AreEqual(1, sfx.Played(ReverseCue.Cue), "1回だけ");
        sfx.Tick(11.0);
        Assert.AreEqual(1, sfx.Played(ReverseCue.Rumble));
        // 止まって再開したら、鳴っていた長い音は途中から鳴らし直し、終わった短い音は鳴らさない。
        sfx.ResetSounds();
        sfx.Tick(11.5);
        Assert.AreEqual(2, sfx.Played(ReverseCue.Rumble), "うなりは途中から");
        Assert.AreEqual(1, sfx.Played(ReverseCue.Cue), "終わった予告音は鳴らさない");
        Assert.AreEqual(-.5, sfx.LastDelay, 1e-9);
        DisplaySettings.SetReducedEffectsForTest(true);
        sfx.Tick(12.0);
        Assert.AreEqual(0, sfx.Played(ReverseCue.Creak), "F3 ではきしみを出さない");
        DisplaySettings.SetReducedEffectsForTest(false);
        sfx.Tick(19.3);
        Assert.AreEqual(1, sfx.Played(ReverseCue.FinaleSwell));
        Assert.AreEqual(20 - .667 - 19.3, sfx.LastDelay, 1e-9, "最後の一回転のせり上がりも少し前に予約する");
        sfx.Tick(25);
        sfx.Tick(5);
        Assert.AreEqual(1, sfx.Played(ReverseCue.Cue), "時計が戻っても、まだ来ていない音だけ");
        sfx.Tick(9.9);
        Assert.AreEqual(2, sfx.Played(ReverseCue.Cue));
    }
}

// 天地返しの間、SaberInputBridge のカメラの経路だけが入力を逆に回す(WorldEndA/B・中点・カメラの履歴がそろう)。
// 録画・テストの直接の差し込み(OverrideBlade・ApplyBladeImmediate)とマウスは回さない。InputPoint は受信を始めずに値だけ入れる。
public class ReverseInputBridgeTests
{
    readonly List<GameObject> created = new List<GameObject>();
    InputPoint input;
    InputPoint previousInstance;
    WorldTurn turn;
    double clock;
    double stamp = 1;
    float previousTimeScale;

    static readonly PropertyInfo InstanceProperty = typeof(InputPoint).GetProperty(nameof(InputPoint.Instance), BindingFlags.Public | BindingFlags.Static);

    [SetUp]
    public void SetUp()
    {
        previousTimeScale = Time.timeScale;
        Time.timeScale = 1;
        previousInstance = InputPoint.Instance;
        var go = new GameObject("ReverseInputPoint");
        go.SetActive(false); // Editor のテストからソケットや外部プロセスを開始しない。
        created.Add(go);
        input = go.AddComponent<InputPoint>();
        input.enabled = false;
        InstanceProperty.SetValue(null, input);
        // 本番の Game シーンと同じカメラ(写しは回す前の投影とカメラの行列を使う)。
        view = ReverseProjectionTests.MakeGameCamera(created, "ReverseInputCamera");
        turn = new WorldTurn(ReverseRulesTests.Turns(), () => clock, 0, 0f, view);
    }

    Camera view;

    [TearDown]
    public void TearDown()
    {
        turn?.Dispose();
        InstanceProperty.SetValue(null, previousInstance);
        foreach (var go in created) if (go != null) Object.DestroyImmediate(go);
        created.Clear();
        Time.timeScale = previousTimeScale;
    }

    void Feed(Vector2 a, Vector2 b)
    {
        void Set(string name, object value) => typeof(InputPoint).GetProperty(name).SetValue(input, value);
        Set(nameof(InputPoint.LastReceivedTime), Time.realtimeSinceStartupAsDouble);
        Set(nameof(InputPoint.HasValidStickEndpoints), true);
        Set(nameof(InputPoint.LocalStickRawA), a);
        Set(nameof(InputPoint.LocalStickRawB), b);
        Set(nameof(InputPoint.LastReceivedMonotonicTime), stamp++);
    }

    SaberInputBridge Bridge()
    {
        var go = new GameObject("ReverseInputBridge");
        created.Add(go);
        var bridge = go.AddComponent<SaberInputBridge>();
        bridge.useInputPoint = true;
        bridge.fallbackToMouse = false;
        bridge.stickIndex = 1;
        bridge.pixelsToWorld = 1f;
        return bridge;
    }

    static void Run(SaberInputBridge bridge)
        => typeof(SaberInputBridge).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(bridge, null);

    static void Near(Vector3 expected, Vector3 actual, string message)
    {
        Assert.AreEqual(expected.x, actual.x, 1e-4, message);
        Assert.AreEqual(expected.y, actual.y, 1e-4, message);
    }

    [Test]
    public void CameraPathTurnsTheEndsTheCenterAndTheCameraSampleTogether()
    {
        var a = new Vector2(-1f, .3f);
        var b = new Vector2(1f, 1.2f);
        clock = 100;
        var upright = Bridge();
        Feed(a, b);
        Run(upright);
        Assert.AreEqual(0f, upright.AppliedTurnDegrees);
        Assert.IsTrue(upright.HasBlade);
        Assert.IsTrue(upright.TryGetCameraSample(out var uprightSample));

        clock = 135;
        var turned = Bridge();
        Feed(a, b);
        Run(turned);
        Assert.AreEqual(180f, turned.AppliedTurnDegrees, 1e-4, "曲の時計から同じ角度を求める");
        Near(WorldTurn.MapInput(upright.WorldEndA, 180f), turned.WorldEndA, "端点A");
        Near(WorldTurn.MapInput(upright.WorldEndB, 180f), turned.WorldEndB, "端点B");
        Near((turned.WorldEndA + turned.WorldEndB) * .5f, turned.transform.position, "中点は写した端点の真ん中");
        Assert.IsTrue(turned.TryGetCameraSample(out var turnedSample));
        Near(WorldTurn.MapInput(uprightSample.EndA, 180f), turnedSample.EndA, "カメラの履歴(IMU 用)の端点A");
        Near(WorldTurn.MapInput(uprightSample.EndB, 180f), turnedSample.EndB, "端点B");

        // 写した剣の端は、回した投影で、回す前の手の端と同じ画面の位置に出る(本番のカメラで正確)。
        view.ResetProjectionMatrix();
        Vector3 handA = view.WorldToViewportPoint(upright.WorldEndA);
        turn.Apply(135);
        Vector3 bladeA = view.WorldToViewportPoint(turned.WorldEndA);
        Assert.AreEqual(handA.x, bladeA.x, 5e-4);
        Assert.AreEqual(handA.y, bladeA.y, 5e-4);
    }

    [Test]
    public void DirectInjectionAndTheMouseAreNotTurned()
    {
        clock = 135;
        Assert.AreEqual(180f, WorldTurn.InputDegrees, 1e-4);
        var bridge = Bridge();
        var a = new Vector3(-1f, .3f, 0f);
        var b = new Vector3(1f, 1.2f, 0f);
        bridge.OverrideBlade(a, b);
        Near(a, bridge.WorldEndA, "テストの差し込みは回さない");
        typeof(SaberInputBridge).GetMethod("ApplyBladeImmediate", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(bridge, new object[] { a, b });
        Near(a, bridge.WorldEndA, "録画の DriveBlades と同じ入口(ApplyBladeImmediate)は回さない");
        Near(b, bridge.WorldEndB, "端点B");
        var mouse = Bridge();
        mouse.ApplyMouseWorld(new Vector3(1.2f, .3f, 0f));
        Assert.AreEqual(1.2f, mouse.transform.position.x, 1e-4, "マウスは写さない(投影ごと回るので正しい)");
        Assert.AreEqual(.3f, mouse.transform.position.y, 1e-4);
        Assert.AreEqual(0f, mouse.AppliedTurnDegrees);
    }
}
