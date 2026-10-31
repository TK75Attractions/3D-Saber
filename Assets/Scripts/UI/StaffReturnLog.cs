using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

// スタッフがタイトルへ戻した記録(2026-10-30 夜、改善案⑥)。右クリック3回(RightClickHome)と、Esc・ハイフンの2回目
// (EscapeTwiceGate)、v2.10 からは次の曲が無い結果画面のスタッフの Enter(ResultSkin、how=enter)で
// events.log に staff-return を1行書く: 戻し方・場面・本編か練習か判定調整か・曲 ID・難易度・曲の秒。
// 10/30 は来場者の流れを場面の移りから推して数えた。あとから「いつ・どこで戻したか」を数えられるようにする。
public static class StaffReturnLog
{
    public const string Kind = "staff-return";
    const string GameScene = "Game", ResultScene = "Result";

    public static void Record(string how)
    {
        if (PhoneSaberEventLog.Current == null) return;
        PhoneSaberEventLog.Record(PhoneSaberStation.Read(), "SYSTEM", Kind, Detail(how, SceneManager.GetActiveScene().name));
    }

    // 1行の中身(key=value を空白で区切る。曲 ID の空白は _ にする)。
    public static string Detail(string how, string scene)
    {
        var text = new StringBuilder();
        text.Append("how=").Append(Safe(how)).Append(" scene=").Append(Safe(scene));
        if (scene == GameScene)
        {
            string part = GameSession.IsCalibrationMode ? "calibration"
                : Object.FindFirstObjectByType<TutorialController>() != null ? "tutorial" : "song";
            text.Append(" part=").Append(part);
            if (part == "song") AppendSong(text);
            var player = Object.FindFirstObjectByType<SongPlayer>();
            if (player != null) text.Append(" song-s=").Append(player.SongTime.ToString("0.0", CultureInfo.InvariantCulture));
        }
        else if (scene == ResultScene) AppendSong(text);
        return text.ToString();
    }

    static void AppendSong(StringBuilder text) =>
        text.Append(" song=").Append(Safe(GameSession.SelectedSongId)).Append(" diff=").Append(Safe(GameSession.SelectedDifficulty));

    static string Safe(string value) => string.IsNullOrEmpty(value) ? "-" : value.Replace(' ', '_');
}
