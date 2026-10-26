using UnityEngine;

public sealed partial class GameplayCutFeedback
{
    // A の頂点見積もりは残し、追加の白い輪だけ別に確保する。
    public const int MaximumVerticesPerBurst = VerticesPerBurst + RingSegments * 4;
    double lastCutClock = double.NaN;

    public static float LifetimeFor(JudgmentTier tier) => tier == JudgmentTier.Perfect ? .36f : .24f;
    public static float DensityScale(int activeIncludingNew) => activeIncludingNew >= 5 ? .8f : 1f;

    public static (int sparks, int shards, int rings, float size) ProfileFor(JudgmentTier tier, bool low)
    {
        var style = StyleFor(tier, low);
        return (style.sparks, style.shards, style.rings, style.size);
    }

    void DrawOuterShock(Burst burst, bool low, float gain)
    {
        if (low || burst.tier != JudgmentTier.Perfect || burst.age < .1f) return;
        float t = Mathf.Clamp01((burst.age - .1f) / .26f);
        float ease = 1 - (1 - t) * (1 - t);
        float radius = burst.scale * Mathf.Lerp(1.7f, 3f, ease);
        protectionWeight = 1;
        Color tint = WithAlpha(Color.white, (1 - t) * .55f * gain);
        for (int s = 0; s < RingSegments; s++)
            RingSegment(burst.position, s * Mathf.PI * 2 / RingSegments,
                (s + 1) * Mathf.PI * 2 / RingSegments, radius, .025f * burst.scale, tint);
    }
}
