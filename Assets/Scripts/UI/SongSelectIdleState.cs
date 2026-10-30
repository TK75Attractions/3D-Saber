// 入場時に一度案内し、未操作なら10秒後にも案内する。照準の移動自体は操作に数えない。
public sealed class SongSelectIdleState
{
    // 2026-10-30 ユーザー「④もすべてやる」: 入場カードは3.5秒。人体模型の再生周期と、10秒後の再案内は変えない。
    public const float EntryDisplaySeconds = 3.5f;
    public const float DelaySeconds = 10f;
    public const float AimActivitySeconds = .4f;
    double elapsed;
    public bool Completed { get; private set; }
    public bool ShouldShow => !Completed && (elapsed < EntryDisplaySeconds || elapsed >= DelaySeconds);

    public void Tick(float seconds, bool active)
    {
        if (!active || Completed || float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds < 0) return;
        elapsed += seconds;
    }

    public void RegisterActivity() { Completed = true; }
}
