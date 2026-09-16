using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

// 実ResultシーンのHI-SCORE表示と、曲・難易度別の保存結果を照合する。
public class ResultHighScorePlayTests
{
    readonly Dictionary<FieldInfo, object> sessionValues = new Dictionary<FieldInfo, object>();
    string songId;

    [SetUp]
    public void SetUp()
    {
        foreach (var field in typeof(GameSession).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (!field.IsLiteral && !field.IsInitOnly) sessionValues[field] = field.GetValue(null);
        songId = "__ResultHighScore_" + Guid.NewGuid().ToString("N");
        GameSession.SelectedSongId = songId;
        GameSession.SelectedSongTitle = "Score verification";
        GameSession.SelectedDifficulty = "Normal";
        GameSession.IsCalibrationMode = false;
        GameSession.ResetResult();
        GameSession.FinalPerfect = 100;
        GameSession.FinalHit = 100;
        GameSession.FinalMaxCombo = 100;
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        var scene = SceneManager.GetActiveScene();
        SceneManager.SetActiveScene(SceneManager.CreateScene("ResultHighScoreCleanup"));
        yield return SceneManager.UnloadSceneAsync(scene);
        HighScoreStore.Clear(songId, "Normal");
        HighScoreStore.Clear(songId, "Hard");
        foreach (var entry in sessionValues) entry.Key.SetValue(null, entry.Value);
        sessionValues.Clear();
    }

    [UnityTest]
    public IEnumerator NewRecordShowsTheUpdatedBestAndSavesItOnce()
    {
        Record(12000);
        yield return Verify(18500, 18500, true, 2);
    }

    [UnityTest]
    public IEnumerator LowerScoreKeepsTheBestWithoutNewRecord()
    {
        Record(18500);
        yield return Verify(12000, 18500, false, 2);
    }

    [UnityTest]
    public IEnumerator TieKeepsTheEarlierRecordAndDoesNotShowNewRecord()
    {
        Record(18500);
        yield return Verify(18500, 18500, false, 2);
        Assert.AreEqual("earlier", HighScoreStore.Load(songId, "Normal").entries[0].date);
    }

    [UnityTest]
    public IEnumerator FirstResultUsesThisDifficultyWithoutOverwritingAnother()
    {
        HighScoreStore.Record(songId, "Hard", new HighScoreEntry { score = 90000, rank = "A", accuracy = .8f, date = "other" }, out _);
        yield return Verify(18500, 18500, true, 1);
        Assert.AreEqual(90000, HighScoreStore.Load(songId, "Hard").entries[0].score);
    }

    [UnityTest]
    public IEnumerator PartlyInvalidHistoryKeepsValidBestAndFinishesBuildingTheScreen()
    {
        PlayerPrefs.SetString(HighScoreStore.Key(songId, "Normal"),
            "{\"entries\":[{\"score\":-50},null,{\"score\":12000,\"rank\":\"B\",\"date\":\"old\"},{\"score\":18500,\"rank\":\"A\",\"date\":\"best\"}]}");
        yield return Verify(15000, 18500, false, -1);
        var table = HighScoreStore.Load(songId, "Normal");
        Assert.AreEqual(15000, table.entries[1].score);
        Assert.AreEqual(12000, table.entries[2].score);
    }

    void Record(int score)
    {
        HighScoreStore.Record(songId, "Normal", new HighScoreEntry { score = score, rank = "A", accuracy = .9f, date = "earlier" }, out _);
    }

    IEnumerator Verify(int score, int best, bool newRecord, int expectedCount)
    {
        GameSession.FinalScore = score;
        yield return SceneManager.LoadSceneAsync("Result");
        yield return null;
        var root = GameObject.Find("ResultStats");
        Assert.NotNull(root);
        var row = root.transform.Find("ScoreBlock/HiScoreRow");
        Assert.NotNull(row);
        Assert.AreEqual(best.ToString("N0"), row.Find("Value").GetComponent<TMP_Text>().text,
            "HI-SCOREは今回の記録を含む最高点を表示する");
        Assert.AreEqual(newRecord, row.Find("NewRecord") != null);
        var table = HighScoreStore.Load(songId, "Normal");
        Assert.AreEqual(best, table.entries[0].score);
        if (expectedCount >= 0) Assert.AreEqual(expectedCount, table.entries.Count);
        var reveal = Object.FindFirstObjectByType<ResultReveal>();
        reveal.enabled = false;
        reveal.Tick(999);
        var back = Object.FindFirstObjectByType<ResultController>().GetComponent<Canvas>().GetComponentInChildren<Button>();
        Assert.NotNull(back);
        Assert.True(back.IsInteractable(), "記録に問題があっても結果画面から戻れること");
    }
}
