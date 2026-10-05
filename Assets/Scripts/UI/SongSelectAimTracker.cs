using UnityEngine;

// 選曲専用の照準受付。発射位置が動いたり対象が再生成されても、同じ位置で連射しない。
// ただし repeat を指定した的(曲送りの盤)は、乗せたままでも、ため直すたびに続けて発射する。
public sealed class SongSelectAimTracker
{
    public const float HoldSeconds = 1f;
    public const float ReleaseSeconds = .12f;
    object current;
    float held, outside;
    float duration = HoldSeconds;
    bool releaseCircle;
    Rect releaseArea;
    public float Progress01 => Mathf.Clamp01(held / duration);
    public float HeldSeconds => held;
    public bool NeedsRelease { get; private set; }

    public bool Tick(object target, Rect area, Vector2 point, float dt, bool ready, float holdSeconds = HoldSeconds, bool circle = false, bool repeat = false)
    {
        // 停止・復帰したフレームを「かざした時間」に数えない。
        if (float.IsNaN(dt) || dt < 0 || dt > .2f) { Cancel(); return false; }
        if (NeedsRelease)
        {
            held = 0; current = null;
            outside = Contains(releaseArea, point, releaseCircle) ? 0 : outside + dt;
            if (outside >= ReleaseSeconds) { NeedsRelease = false; outside = 0; }
            return false;
        }
        if (!ready || target == null) { Cancel(); return false; }
        float requested = float.IsNaN(holdSeconds) || float.IsInfinity(holdSeconds) ? HoldSeconds : Mathf.Max(.05f, holdSeconds);
        if (!ReferenceEquals(current, target) || duration != requested) { held = 0; current = target; duration = requested; }
        held += dt;
        if (held + .00001f < duration) return false;
        // 連続送りの的は外すまで待たせない。次の発射も同じ時間のため直しから数える。
        if (repeat) { Cancel(); return true; }
        BlockUntilExit(area, circle);
        return true;
    }

    public void BlockUntilExit(Rect area, bool circle = false)
    {
        releaseArea = area;
        releaseCircle = circle;
        NeedsRelease = true;
        Cancel();
    }

    // 入力断・画面遷移でも再発射ロックは解除しない。
    public void Cancel() { current = null; held = outside = 0; }

    public static bool Contains(Rect area, Vector2 point, bool circle)
    {
        if (!area.Contains(point)) return false;
        if (!circle) return true;
        Vector2 delta = point - area.center;
        return area.width > 0 && area.height > 0 &&
            delta.x * delta.x / (area.width * area.width * .25f) +
            delta.y * delta.y / (area.height * area.height * .25f) <= 1;
    }
}
