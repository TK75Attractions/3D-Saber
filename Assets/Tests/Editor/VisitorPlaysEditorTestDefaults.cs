using NUnit.Framework;

// 既存のEditModeテストは1曲モードで実行する。実際の保存値には触れない。
[SetUpFixture]
public sealed class VisitorPlaysEditorTestDefaults
{
    [OneTimeSetUp]
    public void UseOneSong()
    {
        VisitorPlays.SetSongsPerVisitorForTest(1);
        VisitorPlays.BeginVisitor();
    }

    [OneTimeTearDown]
    public void RestoreSavedMode()
    {
        VisitorPlays.BeginVisitor();
        VisitorPlays.SetSongsPerVisitorForTest(null);
    }
}
