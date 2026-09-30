using NUnit.Framework;
using UnityEngine;

// 見た目の基準をそろえるEditorテストは、同梱OFL日本語fontを明示使用する。
// 本番もMakinas未取得時にはこのNotoへ自動で切り替わる。
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
            "Restore the bundled font from Git before running Unity tests.");
        Assert.IsTrue(fixture.HasCharacter('揺') && fixture.HasCharacter('籠'),
            "required Japanese test fixture lacks the 揺籠 glyphs");
        UISkinKit.SetJapaneseFontForTests(fixture);
        Debug.Log("[UNITY_TEST][FONT] fixture=Fonts/NotoSansJP-Light source=existing-OFL-asset");
        return fixture;
    }

    public static Font Font => fixture ?? Install();
}
