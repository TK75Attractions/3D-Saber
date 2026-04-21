using UnityEngine;
using UnityEngine.SceneManagement;

public class TitleMenuController : MonoBehaviour
{
    public string songSelectSceneName = "SongSelect";

    public void OnStartButton()
    {
        SceneManager.LoadScene(songSelectSceneName);
    }

    public void OnQuitButton()
    {
        Application.Quit();
    }
}
