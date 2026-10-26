using UnityEngine;

public partial class CuttableNote
{
    public float Brightness { get; private set; } = 1f;

    // 本体と合図の共通入口。判定可能・位置・方向・時刻には触れない。
    public void SetDarkBrightness(float brightness)
    {
        brightness = DarkRules.ClampBrightness(brightness);
        if (Brightness == brightness) return;
        Brightness = brightness;
        if (Visuals != null) Visuals.SetBrightness(brightness);
        if (Arrow != null) Arrow.RefreshDarkBrightness();
        if (TimingCue != null) TimingCue.SetDarkBrightness(brightness);
        if (RevealSeconds > 0f) RefreshRevealCountColor();
        else if (countLabel != null)
        {
            Color color = countLabel.color;
            countLabel.color = new Color(brightness, brightness, brightness, color.a);
        }
        // 発光が落ち着いたひびも、光の出入りに合わせる。
        for (int i = 0; i < cracksUsed; i++)
        {
            var crack = ownedCracks[i];
            ApplyCrack(crack.transform, crack.material, crack.fullScale, crack.age, Veil, brightness);
        }
    }
}
