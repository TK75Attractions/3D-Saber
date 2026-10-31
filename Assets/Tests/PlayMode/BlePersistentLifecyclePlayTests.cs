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
        try
        {
            yield return SceneManager.LoadSceneAsync("Title", LoadSceneMode.Single);

            float deadline = Time.realtimeSinceStartup + 8f;
            while ((UdpImuBridge.Instance == null ||
                    !UdpImuBridge.Instance.IsBridgeProcessReady) &&
                   Time.realtimeSinceStartup < deadline) yield return null;

            UdpImuBridge persistent = UdpImuBridge.Instance;
            Assert.IsNotNull(persistent, "Title表示時点でBLE transportを作成する");
            // 橋渡し(Tools/mac_ble_udp_bridge.py)は macOS の python3 と BLE で動く(ImuBleBridgeLauncher は Tools/.venv/bin/python3 を探す)。
            // Windows(台B)は BLE を使わず、橋渡しも起動できないので、起動しなかったときは確かめられない扱いにする
            // (2026-11-01 全体見回り。10/29 から Windows では毎回落ちていた)。
            if (!persistent.IsBridgeProcessReady && Application.platform != RuntimePlatform.OSXEditor
                && Application.platform != RuntimePlatform.OSXPlayer)
                Assert.Inconclusive("BLE の橋渡しは macOS 用(この PC では python3 の橋渡しが起動しない)");
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
        }
        finally
        {
            // 確かめられない・失敗した回も、自動起動の設定と、起動し直しを続ける橋渡しを残さない。
            autoStart.SetValue(settings, originalAutoStart);
            foreach (var left in Object.FindObjectsByType<UdpImuBridge>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Object.Destroy(left.gameObject);
        }
    }

    static int BridgeCount() => Object.FindObjectsByType<UdpImuBridge>(
        FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
}
