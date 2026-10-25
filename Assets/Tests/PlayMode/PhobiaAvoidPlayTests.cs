using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

// Phobia PARTⅠ の裏譜面「避」を本編のシーンで通す(2026-10-24)。照準の位置はテスト用の口(AimDodgeMode.SampleOverride)から入れる。
// 最後まで遊ばない(結果画面・記録は作らない)。
public class PhobiaAvoidPlayTests
{
    const string SongId = "Phobia PARTⅠ";
    readonly Dictionary<FieldInfo, object> sessionValues = new Dictionary<FieldInfo, object>();

    [SetUp]
    public void SetUp()
    {
        foreach (var field in typeof(GameSession).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (!field.IsLiteral && !field.IsInitOnly) sessionValues[field] = field.GetValue(null);
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var pair in sessionValues) pair.Key.SetValue(null, pair.Value);
    }

    [UnityTest]
    public IEnumerator SabersBecomeCrosshairsBullsAddAndSpikesSubtract()
    {
        // 前の検証の受信が残っていると、見失いの一時停止が働くので消してから始める。
        if (InputPoint.Instance != null) { Object.Destroy(InputPoint.Instance.gameObject); yield return null; }
        GameSession.SelectedSongId = SongId;
        GameSession.SelectedDifficulty = "D";
        GameSession.IsCalibrationMode = false;
        GameSession.TutorialPending = false;
        yield return SceneManager.LoadSceneAsync("Game", LoadSceneMode.Single);
        var manager = Object.FindFirstObjectByType<GamePlayManager>();
        Assert.NotNull(manager);
        double deadline = Time.realtimeSinceStartupAsDouble + 30;
        while (manager.AimMode == null && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        var aim = manager.AimMode;
        Assert.NotNull(aim, "避の譜面は照準のモードで始まる");
        Assert.IsTrue(manager.scoreManager.AimScoring);
        Assert.AreEqual(50000, manager.scoreManager.Score, "点は 50000 から");
        Assert.AreEqual(0, manager.noteSpawner.TotalNoteCount, "ノーツは流さない");
        Assert.IsFalse(manager.cutJudge.bladeProvider.DrawBlade, "刃は描かない");

        var target = aim.Session.Targets[0];
        var spike = aim.Session.Spikes[0];
        Assert.AreEqual(SaberHand.Right, target.Hand);
        Vector2 red = target.Position;
        aim.SampleOverride = hand => hand == SaberHand.Right ? new AimHandSample(true, red) : (AimHandSample?)null;

        deadline = Time.realtimeSinceStartupAsDouble + 25;
        while (!target.Done && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        Assert.IsTrue(target.Done, "最初の的が決まる");
        Assert.AreEqual(JudgmentTier.Perfect, target.Tier, "真ん中に置けば BULL");
        Assert.AreEqual(51000, manager.scoreManager.Score);
        Assert.AreEqual(1, manager.scoreManager.PerfectCount);

        red = spike.Position;
        deadline = Time.realtimeSinceStartupAsDouble + 25;
        while (!spike.Done && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        Assert.IsTrue(spike.Hit, "トゲの上に照準を置くと当たる");
        Assert.AreEqual(1, manager.scoreManager.AimDamageCount);
        Assert.AreEqual(1, manager.scoreManager.MissCount, "途中の青の的は照準が無いので MISS");
        Assert.AreEqual(50000, manager.scoreManager.Score, "50000 + 1000(BULL) − 1000(トゲ)");
        Assert.AreEqual(0, manager.scoreManager.Combo);

        manager.songPlayer.Stop();
        yield return SceneManager.LoadSceneAsync("Title", LoadSceneMode.Single);
    }
}
