using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

// 選曲画面で運営モード(F12)を切り替えたら、曲や難易度を変えなくても HI-SCORE・TOP 5・達成の回数をそのモードの表で読み直す(2026-10-29)。
// 本番の表には書かない: 入れたときの名前空間をテスト専用に差し替え(OperatorMode.UseFestivalNamespaceForTest)、ふだんの表は読むだけ。
public class OperatorModeSongSelectPlayTests
{
    const string TestNamespace = "t1029sel";
    readonly HashSet<GameObject> persistentBefore = new HashSet<GameObject>();
    readonly Dictionary<FieldInfo, object> session = new Dictionary<FieldInfo, object>();
    InputPoint input;
    InputSettings originalInputSettings, testInputSettings;
    string song, difficulty;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        foreach (var root in PersistentRoots()) persistentBefore.Add(root);
        foreach (var field in typeof(GameSession).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (!field.IsLiteral && !field.IsInitOnly) session[field] = field.GetValue(null);
        GameSession.SetPlayerSettingsForTest(true);
        originalInputSettings = InputSystem.settings;
        testInputSettings = Object.Instantiate(originalInputSettings);
        InputSystem.settings = testInputSettings;
        // 選曲の照準が受信を始めるので、テスト専用のポートを先に決める(既定の 5005/5006 を開かない)。
        foreach (var old in Object.FindObjectsByType<InputPoint>(FindObjectsSortMode.None)) Object.DestroyImmediate(old.gameObject);
        var receiver = new GameObject("OperatorModeTestInput");
        receiver.SetActive(false);
        Object.DontDestroyOnLoad(receiver);
        input = receiver.AddComponent<InputPoint>();
        input.port = FreePort();
        do { input.port2 = FreePort(); } while (input.port2 == input.port);
        receiver.SetActive(true);
        InputPoint.EnsureInstance();
        OperatorMode.OverrideForTest(false);
        OperatorMode.UseFestivalNamespaceForTest(TestNamespace);
        yield return null;
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        OperatorMode.OverrideForTest(false);
        OperatorMode.UseFestivalNamespaceForTest(null);
        if (song != null)
        {
            PlayerPrefs.DeleteKey(HighScoreStore.Key(TestNamespace, song, difficulty));
            PlayerPrefs.DeleteKey(SongAchievementStore.Key(TestNamespace, song, difficulty));
            PlayerPrefs.Save();
        }
        yield return ScreenTransitionPlayTests.WaitForTransition();
        var scene = SceneManager.GetActiveScene();
        SceneManager.SetActiveScene(SceneManager.CreateScene("OperatorModeCleanup"));
        if (scene.IsValid() && scene.isLoaded) yield return SceneManager.UnloadSceneAsync(scene);
        if (input != null) Object.DestroyImmediate(input.gameObject);
        input = null;
        // テストの間に作られた常駐物(照準の受信・効果音の再生役など)を残さない。
        foreach (var root in PersistentRoots())
            if (!persistentBefore.Contains(root)) Object.DestroyImmediate(root);
        persistentBefore.Clear();
        if (originalInputSettings != null) InputSystem.settings = originalInputSettings;
        if (testInputSettings != null) Object.DestroyImmediate(testInputSettings);
        foreach (var entry in session) entry.Key.SetValue(null, entry.Value);
        session.Clear();
        GameSession.SetPlayerSettingsForTest(false);
    }

    [UnityTest]
    public IEnumerator TogglingTheModeReloadsTheRecordsOfTheSelectedChart()
    {
        yield return SceneManager.LoadSceneAsync("SongSelect");
        float until = Time.realtimeSinceStartup + 15;
        while (Object.FindFirstObjectByType<SongSelectAimPointer>() == null && Time.realtimeSinceStartup < until) yield return null;
        var aim = Object.FindFirstObjectByType<SongSelectAimPointer>(); Assert.NotNull(aim); aim.enabled = false;
        var controller = Object.FindFirstObjectByType<SongSelectController>();
        controller.Select(Enumerable.Range(0, controller.SongCount).Single(i => controller.SongIdAt(i) == "Epilogue"));
        controller.SetDifficulty(0);
        song = controller.SongIdAt(controller.SelectedIndex);
        difficulty = controller.CurrentDifficultyName;
        PlayerPrefs.DeleteKey(HighScoreStore.Key(TestNamespace, song, difficulty));
        PlayerPrefs.DeleteKey(SongAchievementStore.Key(TestNamespace, song, difficulty));
        HighScoreStore.Record(TestNamespace, song, difficulty, new HighScoreEntry { score = 91234, rank = "S+", date = "2026/10/29", runId = "festival-test" }, out _);
        SongAchievementStore.Record(TestNamespace, song, difficulty, "festival-test", 10, 0, 0, 0, 0, 91234);
        // ふだんの表(本番の記録)は読むだけ。
        var ordinaryTable = HighScoreStore.Load(OperatorMode.OrdinaryNamespace, song, difficulty);
        string ordinaryBest = SongSelectSkin.FormatHighScore(ordinaryTable), ordinaryTop = SongSelectSkin.TopScoreValue(ordinaryTable, 0);
        string ordinaryCount = SongAchievementStore.Load(OperatorMode.OrdinaryNamespace, song, difficulty).s + "<size=24>回</size>";
        var festivalTable = HighScoreStore.Load(TestNamespace, song, difficulty);
        string festivalBest = SongSelectSkin.FormatHighScore(festivalTable), festivalTop = SongSelectSkin.TopScoreValue(festivalTable, 0);
        Assume.That(festivalBest != ordinaryBest, "ふだんの1位がたまたま同じ点");
        controller.SetDifficulty(0);
        var panel = GameObject.Find("Achievements").transform;
        var best = panel.Find("HighScore").GetComponent<TMP_Text>();
        var count = panel.Find("Count0").GetComponent<TMP_Text>();
        var top = GameObject.Find("TopScore0").GetComponent<TMP_Text>();
        Assert.AreEqual(ordinaryBest, best.text);
        Assert.AreEqual(ordinaryTop, top.text);
        Assert.AreEqual(ordinaryCount, count.text);
        OperatorMode.OverrideForTest(true);
        Assert.AreEqual(festivalBest, best.text, "運営モードの表に読み直す");
        Assert.AreEqual(festivalTop, top.text);
        Assert.AreEqual("1<size=24>回</size>", count.text);
        OperatorMode.OverrideForTest(false);
        Assert.AreEqual(ordinaryBest, best.text, "ふだんの表に戻す");
        Assert.AreEqual(ordinaryTop, top.text);
        Assert.AreEqual(ordinaryCount, count.text);
    }

    static int FreePort()
    {
        using (var socket = new UdpClient(0)) return ((IPEndPoint)socket.Client.LocalEndPoint).Port;
    }

    // シーンをまたいで残る物(DontDestroyOnLoad)の根。
    static GameObject[] PersistentRoots()
    {
        var probe = new GameObject("OperatorModePersistentProbe");
        Object.DontDestroyOnLoad(probe);
        var roots = probe.scene.GetRootGameObjects();
        Object.DestroyImmediate(probe);
        var result = new List<GameObject>();
        foreach (var root in roots) if (root != null && root != probe) result.Add(root);
        return result.ToArray();
    }
}
