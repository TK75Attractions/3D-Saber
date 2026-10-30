using NUnit.Framework;

// タイトルの受付の鍵と、選曲の「この曲で はじめる？」の決まり(2026-10-30 ユーザー「①の1 2 5をやる」「③はすべて実装」)。
public class TitleReceptionTests
{
    [TearDown] public void Cleanup() { TitleReception.ResetForTest(); }

    [Test]
    public void OperatorModeLocksTheTitleOnEveryEntryAndTheMiddleClickToggles()
    {
        TitleReception.EnterTitle(true);
        Assert.IsTrue(TitleReception.Locked, "本番(運営モード)はタイトルに入るたびに閉じる");
        TitleReception.Toggle("test");
        Assert.IsFalse(TitleReception.Locked, "中ボタンで開く");
        TitleReception.Toggle("test");
        Assert.IsTrue(TitleReception.Locked, "もう一度押すと閉じる");
        TitleReception.Toggle("test");
        TitleReception.EnterTitle(true);
        Assert.IsTrue(TitleReception.Locked, "開けたままでも、次にタイトルへ入ると閉じる(1回の受付=1人)");
        TitleReception.EnterTitle(false);
        Assert.IsFalse(TitleReception.Locked, "ふだん(開発・テスト)は開けて入る");
    }

    [Test]
    public void ConfirmTargetsAreLargeAndAwayFromTheRestingHand()
    {
        var size = SongSelectConfirm.TargetSize;
        Assert.GreaterOrEqual(size.x, 208); Assert.GreaterOrEqual(size.y, 208);
        // 手はまん中(x=960)に残っているので、的の内側の縁はそこから離す。
        Assert.GreaterOrEqual(960 - (SongSelectConfirm.BackCenter.x + size.x / 2), 250);
        Assert.GreaterOrEqual(SongSelectConfirm.StartCenter.x - size.x / 2 - 960, 250);
        Assert.Greater(SongSelectConfirm.StartHoldSeconds, SongSelectConfirm.BackHoldSeconds, "結果の大きい「スタート！」の方を長くためる");
        Assert.GreaterOrEqual(SongSelectConfirm.MinimumSecondsAfterBack, 10);
    }

    [Test]
    public void FooterSaysWhatHappensWhenNothingIsDone()
    {
        Assert.AreEqual("あと 8 秒で はじまるよ", SongSelectConfirm.Footer(true, 8));
        Assert.AreEqual("10秒 なにもしないと 選曲に もどるよ", SongSelectConfirm.Footer(false, 10));
        Assert.AreEqual("まん中に 2秒 → かくにん", SongSelectSkin.StartHint(30, true));
        Assert.AreEqual("そのまま かざすと かくにん", SongSelectSkin.HoldStartHint);
    }
}
