// 選曲の時計の下の「もっと高難易度の譜面をあそびたい方へ…」(5秒かざす)で出す、裏難易度のヒント
// (2026-10-29 ユーザー「押すと、裏難易度へのヒントがでる仕組みにして(10秒かほかのボタン選択で自動で消える)」)。
// 押すたびに次のヒントへ進む(最後のあとは最初へ)。出してから ShowSeconds で消え、ほかのボタンを選んでも消える。
// 時計は呼ぶ側の秒(Time.unscaledTimeAsDouble)。純粋な状態だけで、絵は SongSelectSkin が描く。
public sealed class SongSelectSecretHint
{
    public const float HoldSeconds = 5f;
    public const float ShowSeconds = 10f;
    // ユーザーが挙げた例のまま(ふりがなは SongSelectRubyText の書き方)。
    public static readonly string[] Hints =
    {
        "<ruby=じつ>実</ruby>は、すべての<ruby=きょく>曲</ruby>に<ruby=うらなんいど>裏難易度</ruby>があります",
        "もしかして:ジャケット",
        "<ruby=うらふめん>裏譜面</ruby>は<ruby=きほん>基本</ruby><ruby=へん>変</ruby>なギミックがあります",
        "1<ruby=かい>回</ruby>じゃなくてめっちゃ<ruby=き>切</ruby>ってみるといいかも…?",
        "<ruby=ちゅう>注</ruby>:クリアできなくても<ruby=じこせきにん>自己責任</ruby>でお<ruby=ねが>願</ruby>いします",
    };

    int next;
    double hideAt = double.NegativeInfinity;

    public bool Visible { get; private set; }
    public string Text { get; private set; } = "";
    public int Shown { get; private set; }

    // 次のヒントを出す。出した文を返す。
    public string Show(double now)
    {
        Text = Hints[next];
        next = (next + 1) % Hints.Length;
        Shown++;
        Visible = true;
        hideAt = now + ShowSeconds;
        return Text;
    }

    // 時間が来たら消す。見えているかを返す。
    public bool Tick(double now)
    {
        if (Visible && !(now < hideAt)) Visible = false;
        return Visible;
    }

    // ほかのボタンを選んだとき。
    public void Dismiss() => Visible = false;
}
