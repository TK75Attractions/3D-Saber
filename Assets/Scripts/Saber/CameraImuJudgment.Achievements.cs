using System;
using UnityEngine;

public partial class CameraImuJudgment
{
    SaberCutRecord AchievementCutRecord(Contact contact, PendingSwing p, SaberCutJudge judge)
    {
        Camera camera = WorldTurn.Active != null ? WorldTurn.Active.View : Camera.main;
        return SaberCutRecord.Create(Time.frameCount, p.FeedbackSwingId, false, contact.MotionFrom, contact.MotionTo,
            contact.FromTurn, contact.ToTurn, judge.verticalDirectionScale, camera, true);
    }
}
