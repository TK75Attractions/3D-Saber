using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

// C3の実譜面を本編で読む。検査する曲時刻は譜面と実行時の速度設定から決める。
// 結果画面へは進めず、失敗時もUnityTearDownで再生と本編を片付ける。
public class MemoryVeilPlayTests
{
    const string SongId = "Morning";
    readonly Dictionary<FieldInfo, object> sessionValues = new Dictionary<FieldInfo, object>();
    GamePlayManager manager;

    [SetUp]
    public void SetUp()
    {
        sessionValues.Clear();
        foreach (var field in typeof(GameSession).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (!field.IsLiteral && !field.IsInitOnly) sessionValues[field] = field.GetValue(null);
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        if (manager != null)
        {
            manager.enabled = false;
            if (manager.songPlayer != null) manager.songPlayer.Stop();
            if (manager.MemoryMode != null) manager.MemoryMode.EndPlay();
        }
        if (SceneManager.GetActiveScene().name == "Game")
            yield return SceneManager.LoadSceneAsync("Title", LoadSceneMode.Single);
        foreach (var pair in sessionValues) pair.Key.SetValue(null, pair.Value);
        manager = null;
    }

    [UnityTest]
    public IEnumerator MemoryChartDrivesNoiseSabersAndVeiledNotes()
    {
        string path = Path.Combine(Application.streamingAssetsPath, "Songs", SongId, "chart_m.json");
        Assert.IsTrue(File.Exists(path), "先にC3でchart_m.jsonを生成する");

        if (InputPoint.Instance != null)
        {
            Object.Destroy(InputPoint.Instance.gameObject);
            yield return null;
        }

        GameSession.SelectedSongId = SongId;
        GameSession.SelectedDifficulty = "M";
        GameSession.IsCalibrationMode = false;
        GameSession.TutorialPending = false;
        yield return SceneManager.LoadSceneAsync("Game", LoadSceneMode.Single);

        manager = Object.FindFirstObjectByType<GamePlayManager>();
        Assert.NotNull(manager);
        double deadline = Time.realtimeSinceStartupAsDouble + 30;
        while ((manager.MemoryMode == null || manager.songPlayer == null || !manager.songPlayer.IsScheduled)
            && Time.realtimeSinceStartupAsDouble < deadline)
            yield return null;

        var mode = manager.MemoryMode;
        Assert.NotNull(mode, "Mの譜面は記憶モードで始まる");
        Assert.IsTrue(manager.songPlayer.IsScheduled);
        Assert.IsFalse(manager.scoreManager.AimScoring, "採点は通常譜面のまま");
        Assert.IsTrue(manager.noteSpawner.MemoryVeilMode);
        Assert.Greater(manager.noteSpawner.TotalNoteCount, 0);

        var spawner = manager.noteSpawner;
        var chart = spawner.CurrentChart;
        Assert.NotNull(chart);
        Assert.IsTrue(chart.memory.HasContent);

        bool sawHidden = spawner.LiveNotes.Any(note => note != null && note.Veil > 0);
        spawner.OnNoteSpawned += note =>
        {
            if (note != null && note.Veil > 0) sawHidden = true;
        };

        // フェードの外で強さを測れる、これから来る区間を譜面から選ぶ。
        // 1回目のまねは説明のため veil=0 なので、見えにくいノーツが来る区間を選ぶ。
        var candidates = mode.Intervals.Where(span =>
            span.Strength > 0 &&
            span.Start > manager.songPlayer.SongTime &&
            span.End - span.Start > MemoryVeilRules.FadeIn + MemoryVeilRules.FadeOut + .2 &&
            chart.notes.Any(note => note.veil > 0 && spawner.EffectiveTime(note) >= span.Start
                && spawner.EffectiveTime(note) < span.End)).ToArray();
        Assert.IsNotEmpty(candidates, "測定できる長さのノイズ区間が必要");
        var interval = candidates[0];
        double plateauStart = interval.Start + MemoryVeilRules.FadeIn;
        double plateauEnd = interval.End - MemoryVeilRules.FadeOut;
        double sampleTime = (plateauStart + plateauEnd) * .5;

        yield return WaitForSong(sampleTime);
        Assert.Less(manager.songPlayer.SongTime, plateauEnd, "大きな処理落ちで測定区間を飛び越えていない");
        Assert.NotNull(mode.NoiseRenderer, "描画対応の環境で実行する");
        Assert.IsTrue(mode.NoiseRenderer.enabled);
        Assert.AreEqual(interval.Strength, mode.Strength, .025f);
        Assert.AreEqual(interval.Strength, mode.NoiseRenderer.sharedMaterial.GetFloat("_Strength"), .025f);
        Assert.AreEqual(MemoryVeilLayers.NoiseSortingOrder, mode.NoiseRenderer.sortingOrder);
        Assert.AreEqual(MemoryVeilLayers.NoiseRenderQueue, mode.NoiseRenderer.sharedMaterial.renderQueue);

        var sabers = Object.FindObjectsByType<SaberInputBridge>(
            FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Where(bridge => bridge.gameObject.scene == manager.gameObject.scene).ToArray();
        Assert.GreaterOrEqual(sabers.Length, 2);
        foreach (var saber in sabers)
        {
            Assert.IsTrue(saber.DrawAboveMemoryVeil);
            var renderers = saber.GetComponentsInChildren<Renderer>(true);
            Assert.IsNotEmpty(renderers);
            foreach (var renderer in renderers)
            {
                Assert.AreEqual(MemoryVeilLayers.AboveSortingOrder, renderer.sortingOrder, renderer.name);
                foreach (var material in renderer.sharedMaterials)
                    if (material != null)
                        Assert.AreEqual(MemoryVeilLayers.AboveRenderQueue, material.renderQueue, renderer.name);
            }
        }

        // 次の見本(まねの区間が終わってからの最初の見えるノーツ)が奥に出るまでに、ノイズが消え切っている。
        double offset = spawner.TotalOffsetSeconds;
        var authored = chart.memory.veils.First(v => System.Math.Abs(v.time / 1000.0 + offset - interval.Start) < 1e-6);
        var nextVisible = chart.notes.FirstOrDefault(note => note.veil == 0 && note.time >= authored.endTime - 1f);
        Assert.NotNull(nextVisible);
        double spawnAt = spawner.EffectiveTime(nextVisible) - spawner.approachTime;
        Assert.LessOrEqual(interval.End, spawnAt + 1e-7);

        yield return WaitForSong(interval.End + .01);
        Assert.AreEqual(0, mode.Strength, 1e-6);
        Assert.IsFalse(mode.NoiseRenderer.enabled);
        Assert.IsTrue(sawHidden, "C1の接点によりVeil>0のノーツが実際に出現する");

        mode.EndPlay();
        Assert.AreEqual(0, mode.Strength);
        Assert.IsFalse(mode.NoiseRenderer.enabled);
        Assert.IsFalse(spawner.MemoryVeilMode);
        foreach (var saber in sabers) Assert.IsFalse(saber.DrawAboveMemoryVeil);
        mode.Tick(sampleTime);
        Assert.IsFalse(mode.NoiseRenderer.enabled, "終了後のTickでは復活しない");
    }

    IEnumerator WaitForSong(double target)
    {
        double remaining = System.Math.Max(0, target - manager.songPlayer.SongTime);
        double deadline = Time.realtimeSinceStartupAsDouble + remaining + 20;
        while (manager.songPlayer.SongTime < target && Time.realtimeSinceStartupAsDouble < deadline)
            yield return null;
        Assert.GreaterOrEqual(manager.songPlayer.SongTime, target, "曲時計が検査する時刻まで進む");
    }
}
