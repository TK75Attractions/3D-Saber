using NUnit.Framework;

// PlayMode 全体では tips を止め、既存の遷移時間と画面の文字検査を保つ。
[SetUpFixture]
public sealed class TransitionTipsPlayModeTestDefaults
{
    bool saved;

    [OneTimeSetUp]
    public void DisableTips()
    {
        saved = TransitionTips.Enabled;
        TransitionTips.Enabled = false;
    }

    [OneTimeTearDown]
    public void RestoreTips()
    {
        TransitionTips.Enabled = saved;
    }
}
