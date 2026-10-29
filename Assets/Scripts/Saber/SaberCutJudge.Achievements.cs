using UnityEngine;

public partial class SaberCutJudge
{
    // 受け付けた切りに付ける記録(振りの番号・なぞり・動きの向き)。判定の状態は読むだけで変えない。
    int achievementTick = -1;
    int achievementReset;
    bool achievementMotion;
    Vector3 achievementFrom, achievementTo;
    float achievementFromTurn, achievementToTurn;

    // 毎コマ GamePlayManager が判定の前に呼ぶ(1コマ前と今の刃の中心を覚えるだけ)。
    public void ObserveAchievementMotion()
    {
        if (saber == null || saber.TickCount == achievementTick) return;
        bool same = achievementTick >= 0 && achievementReset == saber.ResetVersion;
        achievementFrom = achievementTo;
        achievementFromTurn = achievementToTurn;
        bool blade = bladeProvider != null && bladeProvider.HasBlade;
        Vector2 center = Center(blade);
        achievementTo = new Vector3(center.x, center.y, saber.CurrentPosition.z);
        achievementToTurn = bladeProvider != null ? bladeProvider.AppliedTurnDegrees : 0;
        achievementMotion = same && saber.HasPrevious;
        achievementTick = saber.TickCount;
        achievementReset = saber.ResetVersion;
    }

    SaberCutRecord AchievementCutRecord(Pending p, bool sweep)
    {
        ObserveAchievementMotion();
        Camera camera = WorldTurn.Active != null ? WorldTurn.Active.View : Camera.main;
        bool valid = achievementMotion && saber != null && !Stunned
            && (bladeProvider == null || !bladeProvider.UsingMouseFallback)
            && (!ReceivingCamera || bladeProvider.TryGetCameraSample(out _));
        return SaberCutRecord.Create(saber != null ? saber.TickCount : -1, feedbackSwingId, sweep,
            achievementFrom, achievementTo, achievementFromTurn, achievementToTurn,
            ReceivingCamera ? verticalDirectionScale : 1f, camera, valid);
    }
}
