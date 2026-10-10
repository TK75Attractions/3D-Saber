using NUnit.Framework;
using UnityEngine;

public class GameSessionTests
{
    [Test]
    public void NewPlayerGetsDefaultsButCanKeepAdjustmentsThroughoutTheirOwnPlay()
    {
        string[] keys = { "judgmentOffsetMs", CalibrationDraft.ActiveKey, CalibrationDraft.SpeakerKey, CalibrationDraft.HeadphoneKey };
        var existed = new bool[keys.Length];
        var values = new int[keys.Length];
        for (int i = 0; i < keys.Length; i++) { existed[i] = PlayerPrefs.HasKey(keys[i]); values[i] = PlayerPrefs.GetInt(keys[i]); }
        bool hadSpeed = PlayerPrefs.HasKey("noteApproachTime");
        float savedSpeed = PlayerPrefs.GetFloat("noteApproachTime");
        try
        {
            var previous = new CalibrationDraft();
            previous.SelectProfile(0); previous.SetOffset(180);
            previous.SelectProfile(1); previous.SetOffset(-120); previous.SetSpeedStep(2); previous.Commit();
            GameSession.ResetPlayerSettings();
            Assert.AreEqual(60, GameSession.JudgmentOffsetMs);
            Assert.AreEqual(1f, GameSession.NoteApproachTime);
            var current = new CalibrationDraft();
            Assert.AreEqual(0, current.Profile);
            Assert.AreEqual(60, current.OffsetMs);
            current.SelectProfile(1);
            Assert.AreEqual(60, current.OffsetMs, "出力先を切り替えても前の人の調整値が復活しない");
            current.SetOffset(75); current.SetSpeedStep(0); current.Commit();
            Assert.AreEqual(75, new CalibrationDraft().OffsetMs);
            Assert.AreEqual(2f, GameSession.NoteApproachTime, "同じ回の画面間では調整を維持する");
            GameSession.ResetPlayerSettings();
            Assert.AreEqual(60, GameSession.JudgmentOffsetMs);
            Assert.AreEqual(1f, GameSession.NoteApproachTime);
        }
        finally
        {
            for (int i = 0; i < keys.Length; i++)
                if (existed[i]) PlayerPrefs.SetInt(keys[i], values[i]); else PlayerPrefs.DeleteKey(keys[i]);
            if (hadSpeed) PlayerPrefs.SetFloat("noteApproachTime", savedSpeed); else PlayerPrefs.DeleteKey("noteApproachTime");
            PlayerPrefs.Save();
        }
    }

    [Test]
    public void ResetResult_ClearsScoreFields()
    {
        GameSession.FinalScore = 999;
        GameSession.FinalMaxCombo = 50;
        GameSession.FinalComboBonus = 5000;
        GameSession.FinalHit = 30;
        GameSession.FinalMiss = 5;
        GameSession.ResetResult();
        Assert.AreEqual(0, GameSession.FinalScore);
        Assert.AreEqual(0, GameSession.FinalMaxCombo);
        Assert.AreEqual(0, GameSession.FinalComboBonus);
        Assert.AreEqual(0, GameSession.FinalHit);
        Assert.AreEqual(0, GameSession.FinalMiss);
    }

    [Test]
    public void ResetResult_DoesNotClearSelectedSong()
    {
        GameSession.SelectedSongId = "TestSong";
        GameSession.SelectedSongTitle = "Test";
        GameSession.ResetResult();
        Assert.AreEqual("TestSong", GameSession.SelectedSongId);
        Assert.AreEqual("Test", GameSession.SelectedSongTitle);
    }
}
