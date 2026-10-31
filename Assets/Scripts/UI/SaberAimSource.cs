using UnityEngine;

// 照準の入力元(赤い剣・マウス)の切り替えを決める純粋な部品。選曲(SongSelectAimPointer)と、
// 結果・練習・判定調整(SaberUIPointer)の照準で共通に使う。
// 赤の受信が SongSelectAimPointer.SaberActiveSeconds(1秒)途切れると、入力元はマウス(無ければ「なし」)へ移る。
// 2026-10-31 ユーザー「照準が消えても、ため(ゲージ)を0にしない」: 赤を見失っただけでは、まだだれもマウスに持ち替えていない。
// マウスが動く(MouseMoveThreshold を超える)かクリックされるまでは「見失って待っている」(HoldingLostSaber)とし、
// 呼ぶ側はためと狙っていた的を止めたまま保つ。赤が戻れば続きから(同じ的なら続きからたまる)。
// マウスが使われたら、そこで初めて入力元が変わったとして、ためを取り消す。
public sealed class SaberAimSource
{
    public const int None = 0, Saber = 1, Mouse = 2;
    // マウスを動かしたとみなす距離(画面ピクセル)。前から照準が使っていた値。
    public const float MouseMoveThreshold = 3f;

    public int Kind { get; private set; } = None;
    // このフレームで入力元が変わったか(呼ぶ側は照準のならしを最初からやり直す)。
    public bool KindChanged { get; private set; }
    // 赤を見失い、まだマウスが使われていない。この間、照準は今までどおり消すが、ためと狙っていた的は止めたまま保つ。
    public bool HoldingLostSaber { get; private set; }
    // 赤からマウスへ移ったあと、マウスが使われるまではマウスで操作しない(置いたままのマウスの位置で勝手に始めない)。
    public bool MouseNeedsMove { get; private set; }
    Vector2 mouseAtLoss;

    // 毎フレーム呼ぶ。入力元が本当に変わり、ためを取り消すべきときに true を返す。
    public bool Update(bool saberActive, bool mouseAvailable, Vector2 mousePoint, bool mouseClicked = false)
    {
        int kind = saberActive ? Saber : mouseAvailable ? Mouse : None;
        bool cancel = false;
        KindChanged = kind != Kind;
        if (KindChanged)
        {
            if (Kind == Saber)
            {
                // 赤を見失った。ためを保ったまま、マウスが使われるか赤が戻るのを待つ。
                HoldingLostSaber = true;
                MouseNeedsMove = true;
                mouseAtLoss = mousePoint;
            }
            else if (kind == Saber)
            {
                // 赤が戻った。見失って待っていたなら続きから。マウスを使っていたなら、ため直す。
                cancel = !HoldingLostSaber;
                HoldingLostSaber = false;
                MouseNeedsMove = false;
            }
            // マウスとなしの間の切り替え(赤とは関係ない)。見失って待っている間は保ち、つながったマウスの今の位置から動きを数える。
            else if (!HoldingLostSaber) cancel = true;
            else if (kind == Mouse) mouseAtLoss = mousePoint;
            Kind = kind;
        }
        if (MouseNeedsMove && kind == Mouse
            && (mouseClicked || Vector2.Distance(mouseAtLoss, mousePoint) > MouseMoveThreshold))
        {
            MouseNeedsMove = false;
            if (HoldingLostSaber) { HoldingLostSaber = false; cancel = true; }
        }
        return cancel;
    }
}
