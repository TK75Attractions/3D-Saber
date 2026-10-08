using UnityEngine;
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
        if (ScreenTransition.Load(titleSceneName, ScreenTransition.Style.Back)) ResultSelectionReturn.Remember(null, null);
    }

    public bool CanRetry()
    {
        if (string.IsNullOrEmpty(GameSession.SelectedSongId)) return false;
        try { return ChartDifficultyRater.Rate(ChartLoader.LoadFromStreamingAssets(GameSession.SelectedSongId, GameSession.SelectedDifficulty)) > 0; }
        catch (System.Exception) { return false; }
    }

    public void RetrySong()
    {
        if (ScreenTransition.IsBusy || !CanRetry()) return;
        // 校歌HARDの長い導入は選曲からの初回開始用。結果からの再挑戦は通常の幕だけで戻る。
        if (!ScreenTransition.Load("Game")) return;
        GameSession.IsCalibrationMode = false;
        GameSession.ResetResult();
    }

    public void ReturnToSongSelect()
    {
        if (!ScreenTransition.Load("SongSelect", ScreenTransition.Style.Back)) return;
        ResultSelectionReturn.Remember(GameSession.SelectedSongId, GameSession.SelectedDifficulty);
    }
}
