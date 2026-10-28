using NUnit.Framework;
using UnityEngine;

// EditMode テストアセンブリ全体の既定(運営モードと実績、2026-10-29): 運営モードは切ったまま(差し替え false)で、保存先も専用の鍵
// (本番の OperatorMode.v1 を読まない・書かない)。名前空間を渡さない表はふだんの表のまま。実績の記録も専用の鍵(本番の Achievements.Log.v1 に触れない)。
// 運営モードを入れて確かめるテストは OperatorMode.OverrideForTest(true) にして、終わったら false に戻す。
// 注: 名前空間の無い SetUpFixture がいくつもあると、Unity ではそれぞれが一部のテストにしか効かない(2026-10-29 に結果の XML で確かめた)。
// 本番の保存値を守るのは OperatorMode 自身(遊んでいない間は OperatorMode.v1 を読まない・書かない)と、保存を書くテストの SetUp。
[SetUpFixture]
public sealed class OperatorModeEditorTestDefaults
{
    public const string OperatorKey = "OperatorMode.v1.__EditModeTests";
    public const string AchievementKey = "Achievements.Log.v1.__EditModeTests";

    [OneTimeSetUp]
    public void UseTestDefaults()
    {
        PlayerPrefs.DeleteKey(OperatorKey);
        OperatorMode.UseKeyForTest(OperatorKey);
        OperatorMode.OverrideForTest(false);
        PlayerPrefs.DeleteKey(AchievementKey);
        AchievementStore.UseKeyForTest(AchievementKey);
    }

    [OneTimeTearDown]
    public void Restore()
    {
        AchievementStore.UseKeyForTest(null);
        PlayerPrefs.DeleteKey(AchievementKey);
        OperatorMode.OverrideForTest(null);
        OperatorMode.UseKeyForTest(null);
        PlayerPrefs.DeleteKey(OperatorKey);
        PlayerPrefs.Save();
    }
}
