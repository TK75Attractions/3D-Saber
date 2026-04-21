using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

// Game.unity をロードして、NoteSpawner が chart.json のノーツを時間通りに生成するかを見る。
public class GameSceneSmokeTest
{
    [UnityTest]
    public IEnumerator GameScene_LoadsAndSpawnsNotes()
    {
        GameSession.SelectedSongId = "TestSong";

        yield return SceneManager.LoadSceneAsync("Game", LoadSceneMode.Single);

        // GamePlayManager.Start が chart.json を読み、最初のノーツ(time=3s) が
        // approachTime=2s 先読みで songTime=1s に出るまで待つ。起動オーバーヘッドを見越して 2.5s。
        yield return new WaitForSeconds(2.5f);

        var spawner = Object.FindFirstObjectByType<NoteSpawner>();
        Assert.IsNotNull(spawner, "NoteSpawner がシーンに存在するはず");

        // Chart は 6 ノーツ、最初のノーツは time=3000ms → approachTime=2s を足して 1s で spawn
        // 既に数秒経過しているので少なくとも 1 個は見えるはず
        Assert.Greater(spawner.NextIndex, 0, "少なくとも1ノーツは生成処理に入っているはず");
    }
}
