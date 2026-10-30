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
    public string songSelectSceneName = "SongSelect";
    public bool HasNextSong => VisitorPlays.HasNextSong;

    void Awake()
    {
        // Skin.Start より先に数える。結果の再読み込みでも同じIDは一度だけ。
        VisitorPlays.CompleteSong(GameSession.AchievementRunId);
    }

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

    // 来場者の出口(シーンの BACK の永続onClickをそのまま使う。照準・クリック・Enterは同じ出口)。
    // 2曲モードの1曲目なら選曲へ進み、残りが無ければタイトルへ。
    public void OnBackButton()
    {
        if (!HasNextSong) { ReturnToTitle(); return; }
        if (!ScreenTransition.Load(songSelectSceneName, ScreenTransition.Style.Forward)) return;
        ResultSelectionReturn.Remember(GameSession.SelectedSongId, GameSession.SelectedDifficulty);
        // 新しい選曲(制限は SongSelectCountdown.LimitSeconds)。判定値・速度・振る範囲はこの人のまま。
        GameSession.CalibrationSelectionSeconds = null;
        GameSession.CalibrationSelectionSongId = null;
        GameSession.CalibrationSelectionDifficulty = null;
        GameSession.SongSelectOperated = false;
        // 1曲目が隠し譜面(E・G・W など)なら、判定調整の往復と同じ口で解除したまま戻す(選曲の見た目ができてから戻る)。
        var secret = RushESecretState.For(GameSession.SelectedSongId);
        GameSession.RushESecretCarry = secret != null
            && string.Equals(secret.Difficulty, GameSession.SelectedDifficulty, System.StringComparison.OrdinalIgnoreCase);
        GameSession.IsCalibrationMode = false;
        GameSession.TutorialPending = false;
        GameSession.TutorialRangeOnly = false;
    }

    // スタッフの出口(結果の Esc 2回・自動テスト)。1曲目でも必ずタイトルへ。
    public void ReturnToTitle()
    {
        if (!ScreenTransition.Load(titleSceneName, ScreenTransition.Style.Back)) return;
        ResultSelectionReturn.Remember(null, null);
    }
}
