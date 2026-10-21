using UnityEngine;

public class TitleMenuController : MonoBehaviour
{
    public string songSelectSceneName = "SongSelect";

    void Awake()
    {
        // 起動時と前の人からの交代時に、標準の判定・速度で始める。選曲の操作の記録も消す(次の選曲は新しい人の選曲)。
        GameSession.ResetPlayerSettings();
        GameSession.SongSelectOperated = false;
    }

    public void OnStartButton()
    {
        if (ScreenTransition.IsBusy) return;
        var skin = Object.FindFirstObjectByType<TitleSceneSkin>();
        if (skin != null && skin.TryStartPresentation()) return;
        ScreenTransition.Load(songSelectSceneName);
    }

    public void OnQuitButton()
    {
        ScreenTransition.Quit();
    }
}
