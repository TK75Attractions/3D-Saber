using NUnit.Framework;

public class GameSessionTests
{
    [Test]
    public void ResetResult_ClearsScoreFields()
    {
        GameSession.FinalScore = 999;
        GameSession.FinalMaxCombo = 50;
        GameSession.FinalHit = 30;
        GameSession.FinalMiss = 5;
        GameSession.ResetResult();
        Assert.AreEqual(0, GameSession.FinalScore);
        Assert.AreEqual(0, GameSession.FinalMaxCombo);
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
