using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

// Game.unity をロードして、実曲(ElDorado)の譜面が読み込まれるかを見るスモークテスト。
// デモ曲は削除済みのため、実曲=テスト曲。最初のノーツ時刻は譜面編集で変わり得るので
// 「スポーンしたか」ではなく「譜面が読み込まれたか(TotalNoteCount)」を検証する。
public class GameSceneSmokeTest
{
    [UnityTest]
    public IEnumerator GameScene_LoadsChartAndAudio()
    {
        GameSession.SelectedSongId = "ElDorado";
        GameSession.SelectedDifficulty = "normal";
        GameSession.IsCalibrationMode = false;

        yield return SceneManager.LoadSceneAsync("Game", LoadSceneMode.Single);

        // 譜面読み込み完了(音源mp3のランタイムデコード込み)をポーリングで待つ。
        // 音源サイズや実行環境で所要が変わるため固定待ちにしない(最長15秒)。
        NoteSpawner spawner = null;
        float deadline = Time.realtimeSinceStartup + 15f;
        while (Time.realtimeSinceStartup < deadline)
        {
            if (spawner == null)
            {
                spawner = Object.FindFirstObjectByType<NoteSpawner>(FindObjectsInactive.Include);
            }
            if (spawner != null && spawner.TotalNoteCount > 0) break;
            yield return null;
        }

        if (spawner == null)
        {
            // 失敗時の診断: 全階層のコンポーネント一覧(missing script は MISSING と表示)
            var scene = SceneManager.GetActiveScene();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"NoteSpawner が見つからない。activeScene={scene.name}");
            foreach (var root in scene.GetRootGameObjects())
            {
                DumpHierarchy(root.transform, 0, sb);
            }
            Assert.Fail(sb.ToString());
        }
        Assert.Greater(spawner.TotalNoteCount, 0, "譜面が読み込まれているはず(15秒以内)");
    }

    private static void DumpHierarchy(Transform t, int depth, System.Text.StringBuilder sb)
    {
        var comps = t.GetComponents<Component>();
        var list = new System.Text.StringBuilder();
        foreach (var c in comps)
        {
            list.Append(c == null ? "MISSING" : c.GetType().Name).Append(",");
        }
        sb.AppendLine($"{new string(' ', depth * 2)}{t.name} [{list}] active={t.gameObject.activeSelf}");
        for (int i = 0; i < t.childCount; i++)
        {
            DumpHierarchy(t.GetChild(i), depth + 1, sb);
        }
    }
}
