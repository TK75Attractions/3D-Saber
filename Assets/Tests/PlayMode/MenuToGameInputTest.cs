using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

// 曲選択(InputPoint 常駐化) → Game シーンの遷移で、
// シーン直置きの InputPoint とポートを取り合わないことのスモークテスト。
// 修正前は Game 側 InputPoint.Start が SocketException (Address already in use) を投げ、
// さらに Instance を死んだ受信機で上書きしてセーバー入力が全滅していた。
public class MenuToGameInputTest
{
    [UnityTest]
    public IEnumerator SongSelectToGame_KeepsSingleAliveReceiver()
    {
        yield return SceneManager.LoadSceneAsync("SongSelect", LoadSceneMode.Single);

        // スキン構築(1フレ遅延)で InputPoint が常駐化するのを待つ
        float deadline = Time.realtimeSinceStartup + 10f;
        while (Time.realtimeSinceStartup < deadline)
        {
            if (InputPoint.Instance != null) break;
            yield return null;
        }
        Assert.IsNotNull(InputPoint.Instance, "曲選択で InputPoint が常駐する");
        var persisted = InputPoint.Instance;

        GameSession.SelectedSongId = "ElDorado";
        GameSession.SelectedDifficulty = "normal";
        GameSession.IsCalibrationMode = false;
        yield return SceneManager.LoadSceneAsync("Game", LoadSceneMode.Single);

        // シーン直置き InputPoint の Awake/Start と退場処理が済むまで数フレーム待つ。
        // (修正前はここで SocketException が unhandled log としてテストを落とす)
        for (int i = 0; i < 5; i++) yield return null;

        Assert.IsNotNull(InputPoint.Instance, "Instance が生きている");
        Assert.AreSame(persisted, InputPoint.Instance, "常駐受信機が Game でも入力の窓口のまま");

        var receivers = Object.FindObjectsByType<InputPoint>(FindObjectsSortMode.None);
        Assert.AreEqual(1, receivers.Length, "生きている受信機コンポーネントは1つだけ");
    }
}
