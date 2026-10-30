// タイトルの受付の鍵(2026-10-30 ユーザー「マウスの真ん中のやつのクリックで、タイトルノーツのカギを開け閉めできるようにして」
// 「①の1 2 5をやる」)。鍵が閉まっている間はタイトルの開始ノーツと的を隠して鍵の印を出し、剣でもキーでもクリックでも
// 始まらない(剣は画面に映るので、スタッフは剣の映り方を確かめられる)。スタッフがマウスの中ボタンで開け閉めする。
// 運営モード(本番)ではタイトルに入るたびに閉じる(1回の受付=1人)。ふだん(開発・テスト)は開けて入る。
public static class TitleReception
{
    public const string OpenNotice = "受付を開きました ― 来場者がノーツを切ると はじまります";
    public const string ClosedNotice = "受付を閉じました ― マウスの中ボタンで開きます";
    public const string LockedNotice = "受付が閉じています ― マウスの中ボタンで開きます";
    // 鍵の印の下に出す、来場者への一言(改善案①の絵と同じ)。
    public const string WaitingText = "スタッフが 受付をすると はじまるよ";

    public static bool Locked { get; private set; }

    // タイトルに入ったとき。運営モードなら閉じ、ふだんは開ける。
    public static void EnterTitle(bool operatorMode) => Set(operatorMode, "title");

    public static void Toggle(string how) => Set(!Locked, how);

    // テストで前の状態を残さない。
    public static void ResetForTest() { Locked = false; }

    static void Set(bool locked, string how)
    {
        Locked = locked;
        // 1人ごとの区切りとして運営の記録に残す(⑥ デバッグのしやすさ)。
        PhoneSaberEventLog.Record(PhoneSaberStation.Read(), "SYSTEM", locked ? "reception-close" : "reception-open", "how=" + how);
    }
}
