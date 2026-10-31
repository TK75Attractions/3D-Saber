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
        // 戻ったときに時間を足す決まり(MinimumSecondsAfterBack = 15)は 2026-10-31 にやめた(選曲の時計は止まらず、増えない)。
    }

    [Test]
    public void FooterSaysWhatHappensWhenNothingIsDone()
    {
        Assert.AreEqual("10秒 なにもしないと 選曲に もどるよ", SongSelectConfirm.Footer(10));
        // 選曲の時計が先に0になるなら、もどるより先に始まるので「はじまる」を出す(2026-11-01 全体の見回り)。
        Assert.AreEqual("あと 4秒で この曲が はじまるよ", SongSelectConfirm.Footer(10, 4));
        Assert.AreEqual("あと 10秒で この曲が はじまるよ", SongSelectConfirm.Footer(10, 10));
        Assert.AreEqual("10秒 なにもしないと 選曲に もどるよ", SongSelectConfirm.Footer(10, 30));
        Assert.AreEqual("3秒 なにもしないと 選曲に もどるよ", SongSelectConfirm.Footer(3, 4), "確認が先に閉じるなら、もどる");
        Assert.AreEqual("10秒 なにもしないと 選曲に もどるよ", SongSelectConfirm.Footer(10, 0));
        Assert.AreEqual("10秒 なにもしないと 選曲に もどるよ", SongSelectConfirm.Footer(10, -1), "時計が無い");
        // 時間切れの確認(「あと 8 秒で はじまるよ」)は 2026-11-01 にやめた(時間切れは確認を開かずにそのまま始める)。
        Assert.AreEqual("まん中に 2秒 → かくにん", SongSelectSkin.StartHint(30, true));
        Assert.AreEqual("そのまま かざすと かくにん", SongSelectSkin.HoldStartHint);
    }
}
