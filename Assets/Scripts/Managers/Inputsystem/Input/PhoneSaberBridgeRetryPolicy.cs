// 時刻は単調増加の秒。起動失敗 / 短時間の終了が続いても、再試行は最大60秒に1回までに抑える。
public sealed class PhoneSaberBridgeRetryPolicy
{
    int failures;
    double nextStart;
    double startedAt = -1;

    public bool CanStart(double now) { return now >= nextStart; }
    public void Started(double now) { startedAt = now; }

    public void Failed(double now)
    {
        if (startedAt >= 0 && now - startedAt >= 60) failures = 0;
        if (failures < 4) failures++;
        double delay = failures == 1 ? 2 : failures == 2 ? 5 : failures == 3 ? 15 : 60;
        nextStart = now + delay;
        startedAt = -1;
    }

    public void Reset() { failures = 0; nextStart = 0; startedAt = -1; }
}
