using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

public class BlePersistentLifecyclePlayTests
{
    [UnityTest]
    public IEnumerator ProductionFlow_ReusesOneBridgeFromTitleThroughResultAndBack()
    {
        foreach (var existing in Object.FindObjectsByType<UdpImuBridge>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
            Object.DestroyImmediate(existing.gameObject);

        ImuBleServiceSettings settings =
            Resources.Load<ImuBleServiceSettings>("ImuBleServiceSettings");
        FieldInfo autoStart = typeof(ImuBleServiceSettings).GetField(
            "autoStartBleBridge", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(settings);
        Assert.IsNotNull(autoStart);
        bool originalAutoStart = (bool)autoStart.GetValue(settings);
        autoStart.SetValue(settings, true);
        yield return SceneManager.LoadSceneAsync("Title", LoadSceneMode.Single);

        float deadline = Time.realtimeSinceStartup + 8f;
        while ((UdpImuBridge.Instance == null ||
                !UdpImuBridge.Instance.IsBridgeProcessReady) &&
               Time.realtimeSinceStartup < deadline) yield return null;

        UdpImuBridge persistent = UdpImuBridge.Instance;
        Assert.IsNotNull(persistent, "Title表示時点でBLE transportを作成する");
        Assert.IsTrue(persistent.IsBridgeProcessReady,
            "Game sceneへ入る前にbridgeのPING応答が返る");
        Assert.AreEqual(1, BridgeCount());

        GameSession.SelectedSongId = "ElDorado";
        GameSession.SelectedDifficulty = "normal";
        GameSession.IsCalibrationMode = false;
        foreach (string scene in new[] { "SongSelect", "Game", "Result", "Title" })
        {
            yield return SceneManager.LoadSceneAsync(scene, LoadSceneMode.Single);
            yield return null;
            Assert.AreSame(persistent, UdpImuBridge.Instance,
                $"{scene}への遷移でtransportを再作成しない");
            Assert.AreEqual(1, BridgeCount(), $"{scene}でUdpImuBridgeを重複させない");
            Assert.IsTrue(persistent.IsBridgeProcessReady,
                $"{scene}への遷移中もbridge接続を維持する");
        }

        Object.Destroy(persistent.gameObject);
        yield return null;
        Assert.AreEqual(0, BridgeCount(), "Play Stop相当の破棄でpersistent serviceをcleanupする");
        autoStart.SetValue(settings, originalAutoStart);
    }

    static int BridgeCount() => Object.FindObjectsByType<UdpImuBridge>(
        FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
}
