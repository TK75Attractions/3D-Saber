using NUnit.Framework;
using UnityEngine;

// Editorテストは、再配布できない本番Makinasを取得せず、既存のOFL日本語fontを使う。
// 本番UIのMakinas resource名は維持し、このfixtureは同じfallback契約だけを供給する。
[SetUpFixture]
public sealed class JapaneseFontTestAssemblyFixture
{
    [OneTimeSetUp]
    public void Install()
    {
        JapaneseFontTestFixture.Install();
    }

    [OneTimeTearDown]
    public void Uninstall()
    {
        UISkinKit.ClearJapaneseFontForTests();
    }
}

public static class JapaneseFontTestFixture
{
    const string ResourcePath = "Fonts/NotoSansJP-Light";
    static Font fixture;

    public static Font Install()
    {
        fixture = Resources.Load<Font>(ResourcePath);
        Assert.IsNotNull(fixture,
            "required external/test fixture missing: Resources/Fonts/NotoSansJP-Light. " +
            "Restore Git LFS assets before running Unity tests.");
        Assert.IsTrue(fixture.HasCharacter('揺') && fixture.HasCharacter('籠'),
            "required Japanese test fixture lacks the 揺籠 glyphs");
        UISkinKit.SetJapaneseFontForTests(fixture);
        Debug.Log("[UNITY_TEST][FONT] fixture=Fonts/NotoSansJP-Light source=existing-OFL-asset");
        return fixture;
    }

    public static Font Font => fixture ?? Install();
}
