using NUnit.Framework;

public class PhoneSaberJudgeTimingLogTests
{
    [Test]
    public void DetailUsesTheGamesCutTimeForTheJudgeError()
    {
        string line = PhoneSaberJudgeTimingLog.Detail("Great", 10.0, 10.25, 10.06, .12, "Right");
        StringAssert.Contains("tier=Great hand=Right", line);
        StringAssert.Contains("judge-ms=60", line);       // ScoreManager と同じ LastCutSongTime − HitTime
        StringAssert.Contains("decided-ms=130", line);    // 確定したフレームの判定の時計
        StringAssert.Contains("song-ms=250", line);
        StringAssert.Contains("latency-ms=120", line);
        StringAssert.Contains("hit-s=10.000", line);
        StringAssert.Contains("judge-ms=unknown", PhoneSaberJudgeTimingLog.Detail("Miss", 1, 1.2, null, 0, "Any"));
        StringAssert.Contains("decided-ms=unknown", PhoneSaberJudgeTimingLog.Detail("Miss", 1, double.NaN, null, 0, "Any"));
    }

    [Test]
    public void UncutMissDoesNotClaimADirectionErrorOrACutter()
    {
        string detail = PhoneSaberJudgeTimingLog.NoteDetail(1, 0, "Up", "Any", false, true, -20);
        StringAssert.Contains("note-kind=flick", detail);
        StringAssert.Contains("cutter-hand=unknown direction-ok=unknown timed-out=1", detail);
        StringAssert.Contains("calibration-offset-ms=-20", detail);
    }

    [Test]
    public void PartialLongTimeoutRetainsTheAcceptedCutContext()
    {
        string detail = PhoneSaberJudgeTimingLog.NoteDetail(4, 2, "None", "Left", true, true, 30);
        StringAssert.Contains("note-kind=long required-cuts=4 achieved-cuts=2", detail);
        StringAssert.Contains("cutter-hand=Left direction-ok=1 timed-out=1", detail);
    }

    [TestCase("None", true, "tap", "1")]
    [TestCase("Up", false, "flick", "0")]
    public void CutContextIsAppendOnlyAndKeepsTheOriginalTimingColumns(string direction, bool ok, string kind, string flag)
    {
        const string original = "tier=Good hand=Any judge-ms=-60 decided-ms=10 song-ms=130 latency-ms=120 hit-s=10.000";
        string timing = PhoneSaberJudgeTimingLog.Detail("Good", 10, 10.13, 9.94, .12, "Any");
        Assert.AreEqual(original, timing);
        string combined = timing + PhoneSaberJudgeTimingLog.NoteDetail(1, 1, direction, "Right", ok, false, -20);
        StringAssert.StartsWith(original + " note-kind=" + kind, combined);
        StringAssert.Contains("cutter-hand=Right direction-ok=" + flag + " timed-out=0", combined);
    }
}
