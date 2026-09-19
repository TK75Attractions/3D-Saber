using UnityEngine;

public class TitleMenuController : MonoBehaviour
{
    public string songSelectSceneName = "SongSelect";

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
