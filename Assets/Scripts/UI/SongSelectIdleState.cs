// 選曲に入って最初の操作までだけ案内する。照準の移動自体は操作に数えない。
public sealed class SongSelectIdleState
{
    public const float DelaySeconds = 10f;
    public const float AimActivitySeconds = .4f;
    double elapsed;
    public bool Completed { get; private set; }
    public bool ShouldShow => !Completed && elapsed >= DelaySeconds;

    public void Tick(float seconds, bool active)
    {
        if (!active || Completed || float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds < 0) return;
        elapsed += seconds;
    }

    public void RegisterActivity() { Completed = true; }
}
