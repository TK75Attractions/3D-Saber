using UnityEngine;

// 選曲に入って最初の操作までだけ案内する。追跡の静止ノイズや入力源の切替は操作に数えない。
public sealed class SongSelectIdleState
{
    public const float DelaySeconds = 10f;
    double elapsed;
    Vector2 anchor;
    int source;
    public bool Completed { get; private set; }
    public bool ShouldShow => !Completed && elapsed >= DelaySeconds;

    public void Tick(float seconds, bool active)
    {
        if (!active || Completed || float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds < 0) return;
        elapsed += seconds;
    }

    public void RegisterActivity() { Completed = true; }

    // 座標は画面の短辺で正規化する。同じ位置のパケットを受け続けてもタイマーは進む。
    public void ObservePointer(Vector2 point, int inputSource, bool valid)
    {
        if (Completed) return;
        if (!valid) { source = 0; return; }
        if (source != inputSource) { source = inputSource; anchor = point; return; }
        float distance = inputSource == 1 ? .018f : .006f;
        if ((point - anchor).sqrMagnitude >= distance * distance) RegisterActivity();
    }
}
