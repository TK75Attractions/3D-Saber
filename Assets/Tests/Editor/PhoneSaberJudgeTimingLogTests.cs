using NUnit.Framework;

public class PhoneSaberJudgeTimingLogTests
{
    [Test]
    public void DetailSubtractsTheCameraCompensationOnlyFromTheJudgeClock()
    {
        string line = PhoneSaberJudgeTimingLog.Detail("Great", 10.0, 10.25, .12, "Right");
        StringAssert.Contains("tier=Great hand=Right", line);
        StringAssert.Contains("judge-ms=130", line);
        StringAssert.Contains("song-ms=250", line);
        StringAssert.Contains("latency-ms=120", line);
        StringAssert.Contains("hit-s=10.000", line);
        StringAssert.Contains("judge-ms=unknown", PhoneSaberJudgeTimingLog.Detail("Miss", 1, double.NaN, 0, "Any"));
    }
}
