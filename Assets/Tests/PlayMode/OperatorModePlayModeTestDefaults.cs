using NUnit.Framework;
using UnityEngine;

// PlayMode テストアセンブリ全体の既定(運営モードと実績、2026-10-29): 運営モードは切ったまま(差し替え false)で、保存先も専用の鍵
// (本番の OperatorMode.v1 を読まない・書かない)。曲を始めた回もふだんの表に残る。実績の記録も専用の鍵(本番の Achievements.Log.v1 に触れない)。
[SetUpFixture]
public sealed class OperatorModePlayModeTestDefaults
{
    public const string OperatorKey = "OperatorMode.v1.__PlayModeTests";
    public const string AchievementKey = "Achievements.Log.v1.__PlayModeTests";

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
