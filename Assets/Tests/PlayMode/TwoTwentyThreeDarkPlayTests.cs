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

// 実際のGameシーンでLを読み、実際の生成ノーツと材質を検査する。
// 音楽を129秒待たず、読み込み後に本編の時計の駆動だけをテスト側へ移す。
public class TwoTwentyThreeDarkPlayTests
{
    readonly Dictionary<FieldInfo, object> sessionValues = new Dictionary<FieldInfo, object>();
    GamePlayManager manager;

    [SetUp]
    public void SetUp()
    {
        sessionValues.Clear();
        foreach (var field in typeof(GameSession).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (!field.IsLiteral && !field.IsInitOnly) sessionValues[field] = field.GetValue(null);
        // 後始末でタイトルへ入る(ResetPlayerSettings)。本物の判定のずらし・速度は消さない。
        GameSession.SetPlayerSettingsForTest(true);
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        if (manager != null)
        {
            manager.enabled = false;
            if (manager.DarkMode != null) manager.DarkMode.EndPlay();
            if (manager.songPlayer != null) manager.songPlayer.Stop();
        }
        if (SceneManager.GetActiveScene().name == "Game")
            yield return SceneManager.LoadSceneAsync("Title", LoadSceneMode.Single);
        foreach (var pair in sessionValues) pair.Key.SetValue(null, pair.Value);
        GameSession.SetPlayerSettingsForTest(false);
        manager = null;
    }

    [UnityTest]
    public IEnumerator LLoadsAndItsRealNotesFollowTheBladesAndTrackingLoss()
    {
        string path = Path.Combine(Application.streamingAssetsPath, "Songs", "2_23_AM", "chart_y.json");
        Assert.IsTrue(File.Exists(path), "chart_y.jsonが無い場合は通常譜面へフォールバックして通さない");
        if (InputPoint.Instance != null)
        {
            Object.Destroy(InputPoint.Instance.gameObject);
            yield return null;
        }
        GameSession.SelectedSongId = "2_23_AM";
        GameSession.SelectedDifficulty = "Y";
        GameSession.IsCalibrationMode = false;
        GameSession.TutorialPending = false;
        yield return SceneManager.LoadSceneAsync("Game", LoadSceneMode.Single);
        manager = Object.FindFirstObjectByType<GamePlayManager>();
        Assert.NotNull(manager);
        double deadline = Time.realtimeSinceStartupAsDouble + 30;
        while ((manager.songPlayer == null || !manager.songPlayer.IsScheduled
            || manager.noteSpawner.CurrentChart == null || manager.DarkMode == null)
            && Time.realtimeSinceStartupAsDouble < deadline)
            yield return null;
        Assert.NotNull(manager.DarkMode);
        Assert.IsTrue(manager.songPlayer.IsScheduled);

        var spawner = manager.noteSpawner;
        var mode = manager.DarkMode;
        var chart = spawner.CurrentChart;
        Assert.AreEqual(13, chart.dark.Count);
        Assert.AreEqual(10, chart.displayLevel);
        Assert.IsNull(GameObject.Find("FirstHandHint"), "裏譜面は始まりに決まりの一言も手の予告も出さない");

        manager.enabled = false;
        manager.songPlayer.Stop();
        var source = chart.notes.First(n => n.color == "red" && n.time >= 134000 && n.time < 147000);
        double hit = spawner.EffectiveTime(source);
        double time = hit - .18;
        var far = new BladeSample(new Vector2(30, 30), new Vector2(31, 30));
        mode.BladeOverride = _ => far;

        // 通過済みノーツも通常のSpawnerで片付ける。譜面の内容・判定窓は差し替えない。
        mode.Tick(time);
        spawner.Tick(time);
        manager.barLineSpawner?.Tick(time);
        var target = spawner.LiveNotes.First(n => n.RequiredHand == SaberHand.Right
            && System.Math.Abs(n.HitTime - hit) < .001);
        Assert.IsFalse(target.IsMissed);
        Assert.AreEqual(1, mode.Level, 1e-6);
        Assert.AreEqual(DarkRules.NoteMinimum, target.Brightness, 1e-6);
        // 2026-10-27 暗幕: 画面は黒く覆い、ノーツと剣だけを上に描く(遠いノーツは自分の明るさで黒い)。
        Assert.NotNull(mode.Veil, "暗幕を作る");
        Assert.IsTrue(mode.Veil.Visible);
        Assert.AreEqual(DarkRules.VeilDarkness(1), mode.Veil.Darkness, 1e-6);
        Assert.AreEqual(MemoryVeilLayers.AboveNoteSortingOrder, target.BodyRenderer.sortingOrder, "ノーツは暗幕の上");
        var bridges = Object.FindObjectsByType<SaberInputBridge>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        Assert.IsNotEmpty(bridges);
        Assert.IsTrue(bridges.All(b => b.DrawAboveMemoryVeil), "剣は暗幕の上");
        var feedback = Object.FindFirstObjectByType<GameplayFeedbackPresenter>();
        Assert.NotNull(feedback);
        Assert.AreEqual(DarkRules.HudBrightness(1), feedback.GetComponent<CanvasGroup>().alpha, 1e-6, "判定の文字も HUD と同じだけ暗い");
        var body = target.BodyRenderer.sharedMaterial;
        var frame = target.TimingCue.GhostRoot.GetComponentInChildren<MeshRenderer>().sharedMaterial;
        Color darkBody = body.GetColor("_BaseColor");
        Color darkFrame = frame.GetColor("_BaseColor");
        Vector3 position = target.transform.position;
        bool judgeable = target.IsJudgeable;

        Vector2 p = target.transform.position;
        var near = new BladeSample(p + Vector2.left * .6f, p + Vector2.right * .6f);
        mode.BladeOverride = hand => hand == SaberHand.Right ? near : far;
        mode.Tick(time);
        spawner.Tick(time);
        Assert.AreEqual(1, target.Brightness, 1e-6);
        Assert.AreEqual(body.GetColor("_BaseColor").r * DarkRules.NoteMinimum, darkBody.r, 1e-5);
        Assert.AreEqual(frame.GetColor("_BaseColor").r * DarkRules.NoteMinimum, darkFrame.r, 1e-5);
        Assert.AreEqual(position, target.transform.position);
        Assert.AreEqual(hit, target.HitTime, 1e-9);
        Assert.AreEqual(judgeable, target.IsJudgeable);

        // 手を失った瞬間の救済は、Spawnerや曲時計を進めなくても効く。
        mode.BladeOverride = hand => hand == SaberHand.Right ? default(BladeSample) : far;
        mode.Tick(time);
        Assert.AreEqual(1, target.Brightness, 1e-6);
        mode.BladeOverride = _ => far;
        mode.Tick(time);
        Assert.AreEqual(DarkRules.NoteMinimum, target.Brightness, 1e-6);

        var floor = Object.FindFirstObjectByType<FloorRenderer>();
        Assert.NotNull(floor);
        Assert.AreEqual(DarkRules.StageMinimum, floor.DarkBrightness, 1e-6);
        Assert.AreEqual(DarkRules.StageMinimum, manager.barLineSpawner.DarkBrightness, 1e-6);
        var hud = Object.FindFirstObjectByType<GameHUDSkin>();
        Assert.NotNull(hud);
        Assert.AreEqual(DarkRules.HudBrightness(1), hud.GetComponent<CanvasGroup>().alpha, 1e-6);

        // リセットの通知で停止し、次の通常譜面へ倍率・コールバックを持ち越さない。
        var plain = new ChartData { bpm = 90 };
        plain.notes.Add(new NoteData { time = 2000, color = "red", x = source.x, y = source.y });
        spawner.SetChart(plain);
        Assert.IsNull(spawner.BrightnessFor);
        Assert.AreEqual(1, floor.DarkBrightness, 1e-6);
        Assert.AreEqual(1, manager.barLineSpawner.DarkBrightness, 1e-6);
        Assert.AreEqual(1, hud.GetComponent<CanvasGroup>().alpha, 1e-6);
        Assert.IsNull(mode.Veil, "終わったら暗幕を片付ける");
        Assert.IsTrue(bridges.All(b => b == null || !b.DrawAboveMemoryVeil), "剣の並び順を戻す");
        Assert.AreEqual(1, feedback.GetComponent<CanvasGroup>().alpha, 1e-6);
        spawner.Tick(2 + spawner.TotalOffsetSeconds - .18);
        Assert.IsTrue(spawner.LiveNotes.All(n => n.Brightness == 1f && n.GetComponent<NoteVisuals>().Brightness == 1f));
    }
}
