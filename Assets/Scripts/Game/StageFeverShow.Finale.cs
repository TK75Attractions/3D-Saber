using UnityEngine;

public sealed partial class StageFeverShow
{
    // 既存の柱とレーザーの位置に細い白金の芯を重ねる。材質・描画順は書き換えない。
    public void DrawFinale(PlayExcitementMesh target, double songTime, float alpha)
    {
        if (target == null || DisplaySettings.ReducedEffects || alpha <= 0) return;
        Color tint = new Color(1, .96f, .8f, alpha * .9f);
        if (pillars)
            for (int k = 0; k < PillarsPerSide; k++)
                for (int side = -1; side <= 1; side += 2)
                {
                    float x = side * (7.6f + k * .45f), z = 12 + k * 8;
                    target.Band(new Vector3(x, floor + .1f, z),
                        new Vector3(x, floor + 5.2f + k * .6f, z), .10f, tint);
                }
        var laser = LaserAt(songTime);
        float fan = LaserFanAt(laser.poseTime), sway = LaserSwayAt(laser.poseTime, 0, 1);
        for (int k = 0; k < Mathf.Max(LasersPerSide, VoltageLevel); k++)
            for (int side = -1; side <= 1; side += 2)
            {
                Vector3 origin = new Vector3(side * 6.5f, floor + .05f, 6 + k * 8);
                target.Band(origin, origin + LaserDirection(fan, sway, k, side) * 12, .09f, tint);
            }
    }
}
