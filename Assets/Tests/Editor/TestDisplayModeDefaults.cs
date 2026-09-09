using NUnit.Framework;

// テストアセンブリ全体の既定: 表示モードは「通常(プロジェクターモード OFF)」で走らせる。
// DisplaySettings.ProjectorMode は PlayerPrefs 既定 ON なので、そのままだとノーツの色や線の太さを
// 検証する既存テストが環境依存になる。プロジェクターモードの挙動は ProjectorModeTests が明示的に ON にして検証する。
[SetUpFixture]
public class TestDisplayModeDefaults
{
    [OneTimeSetUp]
    public void ForceNormalDisplayMode()
    {
        DisplaySettings.SetProjectorModeForTest(false);
    }
}
