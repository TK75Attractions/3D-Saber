using UnityEngine;
using UnityEngine.SceneManagement;

// シーン読み込み時に対応スキンを自動で attach する。
// 各シーンを手で編集することなくクロスシーンで適用できる。
public static class UISkinBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Init()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
        // 起動時の最初のシーンも処理する
        OnSceneLoaded(SceneManager.GetActiveScene(), LoadSceneMode.Single);
    }

    static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        switch (scene.name)
        {
            case "Title":
                EnsureSkin<TitleSceneSkin>();
                break;
            case "SongSelect":
                EnsureSkin<SongSelectSkin>();
                break;
            case "Result":
                EnsureSkin<ResultSkin>();
                break;
            // Game シーンは GamePlayManager 側で SimplifyJudgeGuide 等を担当しているのでここでは触らない
        }
    }

    static void EnsureSkin<T>() where T : MonoBehaviour
    {
        if (Object.FindFirstObjectByType<T>() != null) return;
        var go = new GameObject(typeof(T).Name);
        go.AddComponent<T>();
    }
}
