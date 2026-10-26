using UnityEngine;

public sealed partial class StageFeverShow
{
    public int VoltageLevel { get; set; }

    // V0は従来の描画。V1以上は同じレーザーの形を使い、本数を加算せず多い方にする。
    public void DrawVoltageLasers(PlayExcitementMesh target, double songTime, Color voltageColor)
    {
        if (target == null || VoltageLevel <= 0 || DisplaySettings.ReducedEffects || !Finite(songTime)) return;
        int level = Mathf.Clamp(VoltageLevel, 0, 4);
        float fever = FeverAt(songTime, strong);
        int count = Mathf.Max(level, fever > 0 ? LasersPerSide : 0);
        var laser = LaserAt(songTime);
        double pose = fever > 0 ? laser.poseTime : songTime;
        float pulse = BeatPulseAt(pose, out bool downbeat);
        float beat = Mathf.Lerp(.25f, .7f, pulse);
        float thick = 1 + (downbeat ? .9f : .35f) * pulse;
        float fan = fever > 0 ? LaserFanAt(pose) : 0;
        float sway = LaserSwayAt(pose, 0, 1);
        BoostLasers(theme, out Color a, out Color b);

        for (int k = 0; k < count; k++)
            for (int side = -1; side <= 1; side += 2)
            {
                bool voltageBeam = k < level;
                bool feverBeam = fever > 0 && k < LasersPerSide;
                float length = Mathf.Max(voltageBeam ? 12 : 0, feverBeam ? laser.length : 0);
                float width = Mathf.Max(voltageBeam ? .06f : 0, feverBeam ? .06f * thick * laser.width : 0);
                float alpha = Mathf.Max(voltageBeam ? .45f : 0, feverBeam ? beat * fever : 0);
                if (length <= 0 || width <= 0) continue;
                Vector3 origin = new Vector3(side * 6.5f, floor + .05f, 6 + k * 8);
                Vector3 end = origin + LaserDirection(fan, sway, k, side) * length;
                Color tint = Color.Lerp(voltageColor, (k + (side > 0 ? 1 : 0)) % 2 == 0 ? a : b, fever);
                Color glow = tint; glow.a = alpha * .16f;
                target.Band(origin, end, width * 4, glow);
                tint = Color.Lerp(tint, Color.white, .18f); tint.a = Mathf.Min(.92f, alpha);
                target.Band(origin, end, width, tint);
            }
    }
}
