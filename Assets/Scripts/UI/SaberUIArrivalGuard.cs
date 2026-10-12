using UnityEngine;

// 入場の猶予は最初の有効な入力から数える。入力のない時間や停止したフレームは含めない。
public sealed class SaberUIArrivalGuard
{
    public const float GraceSeconds = .5f;
    bool started;
    float remaining;
    public bool CaptureTarget { get; private set; }
    public void Reset() { started = false; remaining = GraceSeconds; CaptureTarget = false; }
    public bool Tick(float dt)
    {
        CaptureTarget = !started;
        if (!started) { started = true; remaining = GraceSeconds; }
        else if (!float.IsNaN(dt) && dt >= 0 && dt <= .2f) remaining = Mathf.Max(0, remaining - dt);
        return remaining > .00001f;
    }
}
