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

// アンダルシアの裏譜面 T(闘牛、2026-10-26)を本編で読み、最初の突進に触れた剣がしびれて、2秒後に解けることを確かめる。
// 刃の位置はテストから差し替える(カメラの座標を送れないため)。結果画面には進めない。
public class AndalusiaBullPlayTests
{
    const string SongId = "Andalusia";
    readonly Dictionary<FieldInfo, object> sessionValues = new Dictionary<FieldInfo, object>();
    GamePlayManager manager;

    [SetUp]
    public void SetUp()
    {
        sessionValues.Clear();
        foreach (var field in typeof(GameSession).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (!field.IsLiteral && !field.IsInitOnly)
                sessionValues[field] = field.GetValue(null);
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        if (manager != null)
        {
            manager.enabled = false;
            if (manager.songPlayer != null) manager.songPlayer.Stop();
        }
        if (SceneManager.GetActiveScene().name == "Game")
            yield return SceneManager.LoadSceneAsync("Title", LoadSceneMode.Single);
        foreach (var pair in sessionValues) pair.Key.SetValue(null, pair.Value);
        manager = null;
    }

    [UnityTest]
    public IEnumerator TheFirstChargeStunsTheSaberInItsLaneForTwoSeconds()
    {
        string path = Path.Combine(Application.streamingAssetsPath, "Songs", SongId, "chart_t.json");
        Assert.IsTrue(File.Exists(path), "chart_t.jsonは必須。通常譜面へのフォールバックで通さない");

        if (InputPoint.Instance != null)
        {
            Object.Destroy(InputPoint.Instance.gameObject);
            yield return null;
        }

        GameSession.SelectedSongId = SongId;
        GameSession.SelectedDifficulty = "T";
        GameSession.IsCalibrationMode = false;
        GameSession.TutorialPending = false;
        GameSession.NoteApproachTime = 1f;
        yield return SceneManager.LoadSceneAsync("Game", LoadSceneMode.Single);

        manager = Object.FindFirstObjectByType<GamePlayManager>();
        Assert.NotNull(manager);
        double deadline = Time.realtimeSinceStartupAsDouble + 30;
        while ((manager.songPlayer == null || !manager.songPlayer.IsScheduled
            || manager.noteSpawner.CurrentChart == null)
            && Time.realtimeSinceStartupAsDouble < deadline)
            yield return null;

        Assert.IsTrue(manager.songPlayer.IsScheduled);
        var bull = manager.BullMode;
        Assert.NotNull(bull, "T の譜面は突進と弾を持つ");
        Assert.IsNull(manager.AimMode);
        Assert.IsFalse(manager.scoreManager.AimScoring, "点はふつうのノーツと同じ数え方");

        // 赤の刃を最初の突進の道に置き、青は反対側に置く。
        var charge = bull.Session.Charges[0];
        Vector2 lane = new Vector2(0, charge.Y);
        bull.BladeOverride = hand => hand == SaberHand.Right
            ? new BladeSample(lane + new Vector2(0, -.3f), lane + new Vector2(0, .3f))
            : new BladeSample(new Vector2(-1.5f, -charge.Y - .3f), new Vector2(-1.5f, -charge.Y + .3f));

        double crossMid = (charge.Start + charge.End) * .5 + manager.noteSpawner.JudgeLatencySeconds + .05;
        yield return WaitForSong(crossMid);
        Assert.IsTrue(bull.IsStunned(SaberHand.Right), "道にいた赤はしびれる");
        Assert.IsFalse(bull.IsStunned(SaberHand.Left));
        Assert.AreEqual(1, bull.StunCount);
        var rightJudge = new[] { manager.cutJudge }.Concat(Object.FindObjectsByType<SaberCutJudge>(FindObjectsSortMode.None))
            .First(j => j != null && j.hand == SaberHand.Right);
        Assert.IsTrue(rightJudge.Stunned, "しびれている間は切れない");

        bull.BladeOverride = hand => new BladeSample(new Vector2(hand == SaberHand.Right ? 1.5f : -1.5f, -.3f), new Vector2(hand == SaberHand.Right ? 1.5f : -1.5f, .3f));
        yield return WaitForSong(crossMid + BullRules.StunSeconds + .15);
        Assert.IsFalse(bull.IsStunned(SaberHand.Right), "2秒で解ける");
        Assert.IsFalse(rightJudge.Stunned);

        manager.enabled = false;
        manager.songPlayer.Stop();
    }

    IEnumerator WaitForSong(double target)
    {
        double deadline = Time.realtimeSinceStartupAsDouble
            + Math.Max(0, target - manager.songPlayer.SongTime) + 20;
        while (manager.songPlayer.SongTime < target && Time.realtimeSinceStartupAsDouble < deadline)
            yield return null;
        Assert.GreaterOrEqual(manager.songPlayer.SongTime, target, "曲時計が検査する時刻まで進む");
    }
}
