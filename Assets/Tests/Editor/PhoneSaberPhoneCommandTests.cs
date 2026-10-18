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
