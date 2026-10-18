using NUnit.Framework;

public class PhoneSaberPhoneCommandTests
{
    [Test]
    public void CommandFormatMatchesWhatTheIPhoneParses()
    {
        Assert.AreEqual("PHONESABER_COMMAND 1 id=7 ignore=red", PhoneSaberDiscoveryResponder.BuildCommand(7, "ignore=red"));
        // 応答の形式は変えない（iPhone の LAN 生存確認・Android の探索がそのまま読む）。
        Assert.AreEqual("PHONESABER_UNITY 1 red=5005 blue=5006 name=Mac station=A",
            PhoneSaberDiscoveryResponder.BuildReply(5005, 5006, "Mac", "A"));
    }

    // プレイ範囲・露出補正（2026-10-18）。iPhone の testPCCommandParsingPlayAreaAndExposure・Android の PlayAreaTest と同じ形式。
    [Test]
    public void PlayAreaAndExposureCommandsMatchWhatThePhonesParse()
    {
        Assert.AreEqual("PHONESABER_COMMAND 1 id=11 learn-area=10",
            PhoneSaberDiscoveryResponder.BuildCommand(11, PhoneSaberDiscoveryResponder.LearnAreaCommand));
        Assert.AreEqual("PHONESABER_COMMAND 1 id=12 clear-area=1",
            PhoneSaberDiscoveryResponder.BuildCommand(12, PhoneSaberDiscoveryResponder.ClearAreaCommand));
        CollectionAssert.AreEqual(new[] { 0, -1, -2 }, PhoneSaberDiscoveryResponder.ExposureBiasChoices);
        Assert.AreEqual("PHONESABER_COMMAND 1 id=13 exposure-bias=-1",
            PhoneSaberDiscoveryResponder.BuildCommand(13, PhoneSaberDiscoveryResponder.ExposureBiasCommand(-1)));
        Assert.AreEqual("exposure-bias=-2", PhoneSaberDiscoveryResponder.ExposureBiasCommand(-2));
        Assert.AreEqual("exposure-bias=0", PhoneSaberDiscoveryResponder.ExposureBiasCommand(0));
    }

    [Test]
    public void AreaCommandsAreQueuedLikeTheOthers()
    {
        // F8 は同じ指示（同じ id）を 10 秒送り直す。id は押すたびに新しく、スマホはその id で一度だけ学習を始める。
        double now = SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp);
        int id = PhoneSaberDiscoveryResponder.QueueCommand(PhoneSaberDiscoveryResponder.LearnAreaCommand);
        Assert.Greater(PhoneSaberDiscoveryResponder.CommandSecondsLeft(now), 0);
        int next = PhoneSaberDiscoveryResponder.QueueCommand(PhoneSaberDiscoveryResponder.ClearAreaCommand);
        Assert.AreNotEqual(id, next);
    }

    [Test]
    public void QueuedCommandIsSentForTenSecondsWithANewId()
    {
        double now = SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp);
        int first = PhoneSaberDiscoveryResponder.QueueCommand("ignore=red");
        int second = PhoneSaberDiscoveryResponder.QueueCommand("clear-ignore=1");
        Assert.AreNotEqual(first, second);
        double left = PhoneSaberDiscoveryResponder.CommandSecondsLeft(now);
        Assert.Greater(left, PhoneSaberDiscoveryResponder.CommandSeconds - 1);
        Assert.LessOrEqual(left, PhoneSaberDiscoveryResponder.CommandSeconds + 1);
        Assert.AreEqual(0, PhoneSaberDiscoveryResponder.CommandSecondsLeft(now + 60));
    }
}
