using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

// Manic Sonic の裏譜面 R(chart_r.json)を本編で読み、鏡門・天地返しが判定とそろうことを確かめる。
// 曲は止め、時刻を選んで同じ本体(NoteSpawner・ReverseMode)を進める。結果画面には進めず、失敗時もシーン・選曲・設定を戻す。
// 本物の判定のずらし・速度などのプレイヤー設定は GameSession.SetPlayerSettingsForTest で守る(このテストの中だけの値にする)。
public class ManicSonicReversePlayTests
{
    const string SongId = "ManicSonic";
    readonly Dictionary<FieldInfo, object> sessionValues = new Dictionary<FieldInfo, object>();
    readonly List<GameObject> created = new List<GameObject>();
    GamePlayManager manager;

    [SetUp]
    public void SetUp()
    {
        sessionValues.Clear();
        foreach (var field in typeof(GameSession).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (!field.IsLiteral && !field.IsInitOnly) sessionValues[field] = field.GetValue(null);
        // 後始末でタイトルへ入る。本物の判定のずらし・判定調整の下書き・速度は消さない。解除はタイトルへの移動と後始末が終わってから。
        GameSession.SetPlayerSettingsForTest(true);
        DisplaySettings.SetReducedEffectsForTest(false);
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        foreach (var go in created) if (go != null) Object.Destroy(go);
        created.Clear();
        if (manager != null)
        {
            manager.enabled = false;
            if (manager.songPlayer != null) manager.songPlayer.Stop();
            if (manager.ReverseMode != null) manager.ReverseMode.EndPlay();
        }
        if (SceneManager.GetActiveScene().name == "Game")
            yield return SceneManager.LoadSceneAsync("Title", LoadSceneMode.Single);
        if (WorldTurn.Active != null) WorldTurn.Active.Dispose();
        foreach (var pair in sessionValues) pair.Key.SetValue(null, pair.Value);
        GameSession.SetPlayerSettingsForTest(false);
        DisplaySettings.ResetReducedEffectsCacheForTest();
        manager = null;
    }

    IEnumerator LoadReverse()
    {
        string folder = Path.Combine(Application.streamingAssetsPath, "Songs", SongId);
        Assert.IsTrue(File.Exists(Path.Combine(folder, "chart_r.json")), "chart_r.json は必須。通常譜面へのフォールバックで通さない");
        if (InputPoint.Instance != null)
        {
            Object.Destroy(InputPoint.Instance.gameObject);
            yield return null;
        }
        GameSession.SelectedSongId = SongId;
        GameSession.SelectedSongTitle = "Manic Sonic";
        GameSession.SelectedDifficulty = "R";
        GameSession.IsCalibrationMode = false;
        GameSession.TutorialPending = false;
        GameSession.NoteApproachTime = 1f;
        yield return SceneManager.LoadSceneAsync("Game", LoadSceneMode.Single);
        manager = Object.FindFirstObjectByType<GamePlayManager>();
        Assert.NotNull(manager);
        double deadline = Time.realtimeSinceStartupAsDouble + 30;
        while ((manager.songPlayer == null || !manager.songPlayer.IsScheduled || manager.noteSpawner == null
            || manager.noteSpawner.CurrentChart == null || manager.ReverseMode == null)
            && Time.realtimeSinceStartupAsDouble < deadline)
            yield return null;
        Assert.NotNull(manager.ReverseMode, "R の譜面では ReverseMode を作る");
        Assert.IsTrue(manager.songPlayer.IsScheduled);
    }

    // 実機やマウスの入力で切らない。曲を止め、時刻を選んで同じ本体を進める。判定の時計は曲の時計そのもの。
    NoteSpawner Freeze()
    {
        foreach (var judge in Object.FindObjectsByType<SaberCutJudge>(FindObjectsSortMode.None)) judge.enabled = false;
        manager.cameraImuJudgment = null;
        manager.enabled = false;
        manager.songPlayer.Stop();
        manager.noteSpawner.JudgeLatencySeconds = 0;
        return manager.noteSpawner;
    }

    // 刃の無い点の剣(本編と同じ当たりの大きさ)。位置はテストが入れるので、毎コマの追従は止める。
    (SaberCutJudge judge, SaberTracker tracker) Rig(SaberCutJudge scene)
    {
        var go = new GameObject("ReverseTestSaber");
        created.Add(go);
        var tracker = go.AddComponent<SaberTracker>();
        tracker.enabled = false;
        var judge = go.AddComponent<SaberCutJudge>();
        judge.autonomous = false;
        judge.saber = tracker;
        judge.bladeRadius = scene != null ? scene.bladeRadius : .32f;
        judge.noteHitRadiusXY = scene != null ? scene.noteHitRadiusXY : .88f;
        judge.minCutSpeed = 2f;
        judge.maxCutDistance = 5f;
        return (judge, tracker);
    }

    // x を上から下へ一息に通る振り(曲の時計 songTime で判定する)。振るたびに前の振りを持ち越さない。
    static void Swing(SaberCutJudge judge, SaberTracker tracker, float x, double songTime)
    {
        tracker.ResetTo(new Vector3(x, 1.4f, 0f));
        tracker.Tick(new Vector3(x, -1.6f, 0f), .05f);
        judge.TryCut(songTime);
    }

    static CuttableNote Find(NoteSpawner spawner, double hit, string color)
    {
        var hand = SaberHandHelper.FromColor(color);
        var note = spawner.LiveNotes.FirstOrDefault(n => n != null && Math.Abs(n.HitTime - hit) < 1e-6 && n.RequiredHand == hand);
        Assert.NotNull(note, "時刻 " + hit + " のノーツ");
        return note;
    }

    [UnityTest]
    public IEnumerator MirrorGateAndUpsideDownWorldLineUpWithTheJudgment()
    {
        yield return LoadReverse();
        var mode = manager.ReverseMode;
        var spawner = manager.noteSpawner;
        var chart = spawner.CurrentChart;
        Assert.IsNull(manager.HeatMode, "熱交換は作られない");
        Assert.IsNull(manager.QuizMode);
        Assert.IsNull(manager.MemoryMode);
        Assert.IsNull(manager.BullMode);
        Assert.IsNull(manager.AimMode);
        Assert.IsTrue(spawner.HasReverse);
        Assert.AreEqual(1f, spawner.approachTime, 1e-6, "R でも設定どおりの速さ(既定 1.0 秒。2026-10-28 に下限 1.5 秒をやめた)");
        Assert.AreEqual(1f, GameSession.NoteApproachTime, 1e-6, "保存されている設定は変えない");
        if (manager.barLineSpawner != null)
            Assert.AreEqual(spawner.approachTime, manager.barLineSpawner.approachTime, 1e-6, "小節線も同じ接近時間");
        Assert.AreSame(mode.Turn, WorldTurn.Active);
        Assert.IsNull(GameObject.Find("FirstHandHint"), "裏譜面は始まりに決まりの一言も手の予告も出さない");

        var sceneJudge = manager.cutJudge;
        Freeze();
        var (cutter, tracker) = Rig(sceneJudge);

        // 鏡門: 根元は最初から本当の位置。届く時刻に、剣を鏡像の位置に置いても切れず、本当の位置なら切れる。
        var mirror = chart.notes.First(n => n.mirror && n.color == "red");
        double hit = spawner.EffectiveTime(mirror);
        spawner.Tick(hit - .9);
        mode.Tick(hit - .9);
        var note = Find(spawner, hit, "red");
        Assert.IsTrue(note.IsMirror);
        Assert.AreEqual(mirror.x, note.transform.position.x, 1e-4, "根元は本当の位置");
        Assert.AreEqual(-mirror.x, note.VisualTransform.position.x, 1e-3, "門より奥は左右の鏡像に見せる");
        Assert.AreEqual(ReverseRules.GatePhase.Mirror, mode.Gates.Phase, "門は鏡");
        Assert.Greater(mode.Gates.Tint, .99f, "床の判定線は左赤・右青");
        spawner.Tick(hit);
        mode.Tick(hit);
        Assert.AreEqual(mirror.x, note.VisualTransform.position.x, 1e-3, "門を通ったら本当の位置");
        cutter.hand = SaberHand.Right;
        Swing(cutter, tracker, -mirror.x, hit);
        Assert.IsFalse(note.IsCut, "鏡像の位置では切れない");
        Swing(cutter, tracker, mirror.x, hit);
        Assert.IsTrue(note.IsCut, "本当の位置なら切れる");

        // 天地返し: 135 秒で投影が回っている(軸は動かず、帯の点は軸について反対側)。
        // カメラは天地返しが実際に回すもの(ほかのテストが残した MainCamera を拾わない)。
        var view = mode.Turn.View;
        Assert.NotNull(view);
        var probe = new Vector3(1.9f, .3f, spawner.judgeZ);
        var axis = new Vector3(ReverseRules.Pivot.x, ReverseRules.Pivot.y, spawner.judgeZ);
        Vector3 probeUpright = view.WorldToViewportPoint(probe), axisUpright = view.WorldToViewportPoint(axis);
        mode.Tick(135.0);
        Assert.AreEqual(180f, WorldTurn.CurrentDegrees, 1e-4, "逆さの世界");
        Assert.IsTrue(mode.Turn.ProjectionApplied);
        Vector3 axisTurned = view.WorldToViewportPoint(axis), probeTurned = view.WorldToViewportPoint(probe);
        Assert.AreEqual(axisUpright.x, axisTurned.x, 1e-4);
        Assert.AreEqual(axisUpright.y, axisTurned.y, 1e-4, "軸(ノーツの帯の中心)は動かない");
        Assert.AreEqual(2 * axisUpright.x - probeUpright.x, probeTurned.x, .005);
        Assert.AreEqual(2 * axisUpright.y - probeUpright.y, probeTurned.y, .005);

        // 逆さのノーツは、画面で見えてほしい位置 S の写し W に焼いてある(画面ではほぼ S に見える)。手(カメラの経路)を、
        // そのノーツが見えている画面の位置に置くと、入力の写し(WorldTurn.MapInput)のあとはワールドの W にいるので切れる。
        // 写さない手の位置のままでは切れない。
        var upside = chart.notes.First(n => Math.Abs(n.beat - 387) < .01f);
        double turnedHit = spawner.EffectiveTime(upside);
        spawner.Tick(turnedHit - .5);
        var target = Find(spawner, turnedHit, upside.color);
        spawner.Tick(turnedHit);
        mode.Tick(turnedHit);
        Vector2 screen = ReverseRules.InputRotate(new Vector2(upside.x, upside.y), 180f);
        // ノーツが見えている画面の位置(回した投影)と、そこに置いた手のワールドの位置(回す前の投影で判定面へ戻す)。
        Vector3 seen = view.WorldToScreenPoint(target.transform.position);
        view.ResetProjectionMatrix();
        Assert.IsTrue(MouseSaberInput.TryProjectToPlane(seen, spawner.judgeZ, view, out Vector3 hand));
        Assert.Less(Vector2.Distance(hand, screen), .1f, "ノーツは画面ではほぼ S の位置に見える");
        mode.Tick(turnedHit);
        // 剣を見失って止まり、再開しても(曲の時計が同じなら)角度はそのまま。
        mode.ResetSounds();
        mode.Tick(turnedHit);
        Assert.AreEqual(180f, WorldTurn.CurrentDegrees, 1e-4, "止めて再開しても角度は戻らない");
        Vector3 blade = WorldTurn.MapInput(hand, WorldTurn.CurrentDegrees);
        Assert.AreEqual(target.transform.position.x, blade.x, .01, "写した剣はノーツの本当の位置");
        Assert.AreEqual(target.transform.position.y, blade.y, .01);
        cutter.hand = SaberHandHelper.FromColor(upside.color);
        Swing(cutter, tracker, hand.x, turnedHit);
        Assert.IsFalse(target.IsCut, "手の位置のまま(入力を写さない)では切れない");
        Swing(cutter, tracker, blade.x, turnedHit);
        Assert.IsTrue(target.IsCut, "カメラの経路から、見えている位置に剣を入れると切れる");

        // 剣を見失って止まったときの復帰の輪は、画面の左右のまま(赤は右)。剣の中点は入力の写しのあとのワールドの位置で見る。
        var gate = Object.FindFirstObjectByType<JudgeGateFrame>();
        var redBridge = sceneJudge != null ? sceneJudge.bladeProvider : null;
        if (gate != null && redBridge != null && redBridge.stickIndex == 1 && manager.TrackingView != null)
        {
            // PlayRecoveryView.Configure と同じ置き方(回す前に決めた赤の輪のワールドの位置)。
            Vector3 right = gate.transform.position + gate.transform.TransformVector(Vector3.right) * (gate.HalfWidth * .35f);
            Vector3 turnedRight = WorldTurn.MapInput(right, 180f);
            redBridge.OverrideBlade(turnedRight - Vector3.up * .4f, turnedRight + Vector3.up * .4f);
            redBridge.OverrideMouseFallback(false);
            Assert.AreEqual(1, manager.TrackingView.RecoveryInsideMask() & 1, "逆さでも、右手の輪は画面の右");
            redBridge.OverrideBlade(right - Vector3.up * .4f, right + Vector3.up * .4f);
            redBridge.OverrideMouseFallback(false);
            Assert.AreEqual(0, manager.TrackingView.RecoveryInsideMask() & 1, "写していない位置(画面では左)は輪の外");
        }

        // 終わり(EndPlay)で投影と入力の写しを戻す。
        mode.EndPlay();
        Assert.IsNull(WorldTurn.Active);
        Assert.AreEqual(0f, WorldTurn.InputDegrees);
        Vector3 probeAfter = view.WorldToViewportPoint(probe);
        Assert.AreEqual(probeUpright.x, probeAfter.x, 1e-4, "投影を戻す");
        Assert.AreEqual(probeUpright.y, probeAfter.y, 1e-4);
    }

    [UnityTest]
    public IEnumerator ReducedEffectsDropStreaksAndSwitchTheWorldInTheDark()
    {
        yield return LoadReverse();
        var mode = manager.ReverseMode;
        var spawner = Freeze();
        var chart = spawner.CurrentChart;
        DisplaySettings.SetReducedEffectsForTest(true);

        // 鏡でないノーツは、R でもふつうのノーツ(2026-10-28 に逆再生の2片を外した)。
        var intro = chart.notes.First(n => !n.IsLong && !n.mirror);
        double hit = spawner.EffectiveTime(intro);
        spawner.Tick(hit - .9);
        var note = Find(spawner, hit, intro.color);
        Assert.IsFalse(note.HasReverse, "鏡でないノーツは R の見た目を使わない");
        Assert.AreSame(note.transform, note.VisualTransform);

        var mirror = chart.notes.First(n => n.mirror);
        double mirrorHit = spawner.EffectiveTime(mirror);
        spawner.Tick(mirrorHit - .6);
        mode.Tick(mirrorHit - .6);
        spawner.Tick(mirrorHit - .45);
        mode.Tick(mirrorHit - .45);
        Assert.AreEqual(0, mode.Gates.StreaksDrawn, "F3 では門の筋が無い");

        mode.Tick(129.4);
        Assert.AreEqual(0f, WorldTurn.CurrentDegrees, "回る動きを見せない");
        Assert.AreEqual(ReverseRules.ReducedDimAlpha, mode.Turn.DimAlpha, 1e-6, "真ん中で暗くする");
        mode.Tick(129.6);
        Assert.AreEqual(180f, WorldTurn.CurrentDegrees, 1e-4, "暗い間に向きを切り替える");
        mode.Tick(131);
        Assert.AreEqual(0f, mode.Turn.DimAlpha);
    }

    [UnityTest]
    public IEnumerator AutoPerfectCutsScoreFullMarks()
    {
        yield return LoadReverse();
        var mode = manager.ReverseMode;
        var spawner = Freeze();
        var score = manager.scoreManager;
        var chart = spawner.CurrentChart;
        var hits = new List<double>();
        foreach (var data in chart.notes)
        {
            double at = spawner.EffectiveTime(data);
            if (hits.Count == 0 || Math.Abs(hits[hits.Count - 1] - at) > 1e-6) hits.Add(at);
        }
        double previous = double.NegativeInfinity;
        foreach (double hit in hits)
        {
            double before = Math.Max(previous, hit - .3);
            spawner.Tick(before);
            mode.Tick(before);
            spawner.Tick(hit);
            mode.Tick(hit);
            var due = spawner.LiveNotes.Where(n => n != null && !n.IsCut && !n.IsMissed && Math.Abs(n.HitTime - hit) < 1e-6).ToList();
            Assert.IsNotEmpty(due, "時刻 " + hit);
            foreach (var note in due)
            {
                Vector3 velocity = note.RequiredDirection != CutDirection.None
                    ? (Vector3)(CutDirectionHelper.ToVector(note.RequiredDirection) * 8f) : Vector3.down * 8f;
                int cuts = note.RequiredCutCount;
                for (int k = 0; k < cuts; k++)
                    Assert.IsTrue(note.CutAtSongTime(note.transform.position, velocity, CutDirection.None, note.RequiredHand, hit),
                        "時刻 " + hit + " の " + (k + 1) + " 回目");
            }
            previous = hit;
        }
        spawner.Tick(previous + 3);
        mode.Tick(previous + 3);
        Assert.AreEqual(0, score.MissCount);
        Assert.AreEqual(chart.notes.Sum(n => Mathf.Max(1, n.count)), score.HitCount, "ロングは切る回数ぶん");
        Assert.IsTrue(score.IsAllPerfect);
        score.FinalizeScore();
        Assert.AreEqual(ScoreManager.MaxScore, score.Score, "全部 Perfect で 100000 点(満点は変えない)");
        mode.EndPlay();
        Assert.IsNull(WorldTurn.Active);
    }

    // Bridge(実行順 −1500)が先に聞いた入力の角度と、あとで GamePlayManager が絵へ当てる角度は、同じコマなら同じ
    // (DSP の時計がコマの途中で進んでも)。次のコマでは新しい時刻の角度。カメラは本番の Game シーンと同じ値でテストが作る。
    [UnityTest]
    public IEnumerator TheInputAndTheViewShareOneAngleInAFrame()
    {
        var go = new GameObject("ReverseFrameCamera");
        created.Add(go);
        var camera = go.AddComponent<Camera>();
        camera.enabled = false;
        camera.fieldOfView = 60f;
        camera.nearClipPlane = .3f;
        camera.farClipPlane = 1000f;
        camera.aspect = 16f / 9f;
        camera.transform.SetPositionAndRotation(new Vector3(0f, 2.35f, -7f), Quaternion.Euler(12f, 0f, 0f));
        var data = new ReverseChartData { enabled = true };
        data.turns.Add(new ReverseTurnData { time = 127500, endTime = 131500, degrees = 180 });
        double clock = 129.5;
        var turn = new WorldTurn(data, () => clock, 0, 0f, camera);
        try
        {
            yield return null;
            float input = WorldTurn.InputDegrees;
            Assert.AreEqual(90f, input, 1e-3);
            clock = 129.53;
            turn.Apply(clock);
            Assert.AreEqual(input, turn.Degrees, 1e-6, "同じコマでは、Bridge が使った角度で絵を回す");
            Assert.AreEqual(input, WorldTurn.InputDegrees, 1e-6);
            yield return null;
            Assert.Greater(WorldTurn.InputDegrees, input + .5f, "次のコマは新しい時刻の角度");
        }
        finally
        {
            turn.Dispose();
        }
    }

    [UnityTest]
    public IEnumerator LeavingTheSceneRestoresTheProjectionAndTheInput()
    {
        yield return LoadReverse();
        var mode = manager.ReverseMode;
        Freeze();
        var turn = mode.Turn;
        mode.Tick(135.0);
        Assert.IsTrue(turn.ProjectionApplied);
        Assert.AreEqual(180f, WorldTurn.CurrentDegrees, 1e-4);
        manager = null;
        yield return SceneManager.LoadSceneAsync("Title", LoadSceneMode.Single);
        Assert.IsTrue(turn.Disposed, "シーンの破棄で天地返しを終える");
        Assert.IsNull(WorldTurn.Active);
        Assert.AreEqual(0f, WorldTurn.InputDegrees, "入力の写しも止まる");
    }
}
