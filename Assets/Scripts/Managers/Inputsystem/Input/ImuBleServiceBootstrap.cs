using UnityEngine;
using UnityEngine.SceneManagement;

// BLE transportはScene localの判定処理と分離し、ゲーム全体で一つだけ維持する。
public static class ImuBleServiceBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetRegistration()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Register()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void EnsureInitialScene()
    {
        EnsureFor(SceneManager.GetActiveScene());
    }

    static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => EnsureFor(scene);

    static void EnsureFor(Scene scene)
    {
        if (!Application.isPlaying || !BleBridgeAutoStartPolicy.IsGameFlowScene(scene.path)) return;
        UdpImuBridge.EnsurePersistent(ImuBleServiceSettings.AutoStartBleBridge);
    }
}
