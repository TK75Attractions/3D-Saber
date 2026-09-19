using UnityEngine;

// 選曲専用の照準受付。発射位置が動いたり対象が再生成されても、同じ位置で連射しない。
public sealed class SongSelectAimTracker
{
    public const float HoldSeconds = 1f;
    public const float ReleaseSeconds = .12f;
    object current;
    float held, outside;
    Rect releaseArea;
    public float Progress01 => Mathf.Clamp01(held / HoldSeconds);
    public bool NeedsRelease { get; private set; }

    public bool Tick(object target, Rect area, Vector2 point, float dt, bool ready)
    {
        // 停止・復帰したフレームを「かざした時間」に数えない。
        if (float.IsNaN(dt) || dt < 0 || dt > .2f) { Cancel(); return false; }
        if (NeedsRelease)
        {
            held = 0; current = null;
            outside = releaseArea.Contains(point) ? 0 : outside + dt;
            if (outside >= ReleaseSeconds) { NeedsRelease = false; outside = 0; }
            return false;
        }
        if (!ready || target == null) { Cancel(); return false; }
        if (!ReferenceEquals(current, target)) { held = 0; current = target; }
        held += dt;
        if (held + .00001f < HoldSeconds) return false;
        BlockUntilExit(area);
        return true;
    }

    public void BlockUntilExit(Rect area)
    {
        releaseArea = area;
        NeedsRelease = true;
        Cancel();
    }

    // 入力断・画面遷移でも再発射ロックは解除しない。
    public void Cancel() { current = null; held = outside = 0; }
}
