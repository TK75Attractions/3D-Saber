using System.Collections;
using System.Net;
using System.Net.Sockets;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

// SongSelect.unity を実際にロードして、「照準で曲送り」ナビ一式が組み上がるかのスモークテスト。
// (過去に曲選択への3Dノーツ導入で不具合が出た経緯があるため、シーン統合をここで担保する)
public class SongSelectNavSmokeTest
{
    InputPoint testInput;

    [SetUp]
    public void SetUp()
    {
        // 起動中のゲームと受信ポートを取り合わない。
        if (InputPoint.Instance != null) return;
        var receiver = new GameObject("SongSelectSmokeInput");
        receiver.SetActive(false); Object.DontDestroyOnLoad(receiver);
        testInput = receiver.AddComponent<InputPoint>();
        using (var a = new UdpClient(0))
        using (var b = new UdpClient(0))
        {
            testInput.port = ((IPEndPoint)a.Client.LocalEndPoint).Port;
            testInput.port2 = ((IPEndPoint)b.Client.LocalEndPoint).Port;
        }
        receiver.SetActive(true);
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        var previous = SceneManager.GetActiveScene();
        SceneManager.SetActiveScene(SceneManager.CreateScene("SongSelectSmokeCleanup"));
        if (previous.IsValid() && previous.isLoaded) yield return SceneManager.UnloadSceneAsync(previous);
        if (testInput != null) Object.DestroyImmediate(testInput.gameObject);
        testInput = null;
    }

    [UnityTest]
    public IEnumerator SongSelectScene_BuildsDiscTargetsAndDedicatedBackground()
    {
        yield return SceneManager.LoadSceneAsync("SongSelect", LoadSceneMode.Single);
        float deadline = Time.realtimeSinceStartup + 15;
        SongSelectSkin skin = null;
        while (Time.realtimeSinceStartup < deadline)
        {
            skin = Object.FindFirstObjectByType<SongSelectSkin>();
            if (skin != null && skin.IsReady) break;
            yield return null;
        }
        Assert.NotNull(skin); Assert.True(skin.IsReady);
        var ctl = Object.FindFirstObjectByType<SongSelectController>();
        Assert.NotNull(ctl.startButton.GetComponent<SongSelectDiscTarget>());
        Assert.AreEqual(2, ctl.startButton.GetComponent<SongSelectDiscTarget>().HoldSeconds);
        Assert.NotNull(Object.FindFirstObjectByType<SongSelectAimPointer>());
        Assert.IsNull(Object.FindFirstObjectByType<SaberCutJudge>());
        Assert.IsNull(Object.FindFirstObjectByType<SongSelectSlashNav>());
        Assert.NotNull(skin.Background.Texture);
        Assert.True(skin.Background.Texture.IsCreated());
        Assert.AreEqual(1 << 30, skin.Background.ViewCamera.cullingMask);
        Assert.That(skin.RemainingSeconds, Is.GreaterThan(95));
        Assert.AreEqual(0, ctl.SelectedDifficultyIndex);
        Assert.IsNull(ctl.ChartPreview.View, "旧譜面の描画カメラを生成しない");
    }
}
