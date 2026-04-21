using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class ResultController : MonoBehaviour
{
    public Text titleText;
    public Text scoreText;
    public Text comboText;
    public Text perfectText;
    public Text greatText;
    public Text goodText;
    public Text badText;
    public Text missText;
    public string titleSceneName = "Title";

    void Start()
    {
        if (titleText != null) titleText.text = GameSession.SelectedSongTitle ?? "";
        if (scoreText != null) scoreText.text = $"Score  {GameSession.FinalScore}";
        if (comboText != null) comboText.text = $"Max Combo  {GameSession.FinalMaxCombo}";
        if (perfectText != null) perfectText.text = $"PERFECT  {GameSession.FinalPerfect}";
        if (greatText != null) greatText.text = $"GREAT    {GameSession.FinalGreat}";
        if (goodText != null) goodText.text = $"GOOD     {GameSession.FinalGood}";
        if (badText != null) badText.text = $"BAD      {GameSession.FinalBad}";
        if (missText != null) missText.text = $"MISS     {GameSession.FinalMiss}";
    }

    public void OnBackButton()
    {
        SceneManager.LoadScene(titleSceneName);
    }
}
