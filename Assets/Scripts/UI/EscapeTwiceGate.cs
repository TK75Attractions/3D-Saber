// 来場者の画面に共通の「Esc を2秒以内に2回でタイトルへ」(仕様面の点検 2、2026-10-21)。
// 1回目は知らせだけを出し、2秒以内の2回目で true を返す。期限を過ぎたら、また1回目から数える。
// 判定は純粋(時刻は呼び出し側が渡す)。知らせは OperatorNotice に出す。
public sealed class EscapeTwiceGate
{
    public const double WindowSeconds = 2;
    public const string ToTitleNotice = "タイトルへ戻るには、２秒以内にもう一度 Esc";
    public const string AbortNotice = "中断するには、２秒以内にもう一度 Esc";
    double armedUntil = double.NegativeInfinity;

    public bool Armed(double now) => now <= armedUntil;

    // Esc が押されたフレームに呼ぶ。2回目なら true(数え直しに戻る)。
    public bool Press(double now)
    {
        if (now <= armedUntil) { armedUntil = double.NegativeInfinity; return true; }
        armedUntil = now + WindowSeconds;
        return false;
    }

    // 1回目なら知らせを出す。2回目(戻す)なら events.log に staff-return を1行(2026-10-30 夜、改善案⑥。ハイフンも Esc と同じ)。
    public bool PressAndNotify(double now, string notice)
    {
        if (Press(now)) { StaffReturnLog.Record("esc"); return true; }
        OperatorNotice.Show(notice);
        return false;
    }

    public void Reset() { armedUntil = double.NegativeInfinity; }
}
