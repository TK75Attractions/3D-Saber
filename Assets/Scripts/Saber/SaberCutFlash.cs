using UnityEngine;

// 切れた手の刃だけを光らせる(爽快感カタログ 手8)。ScoreManager の確定判定から、切った手のセーバーへ伝える。
// 切断の演出と同じく Perfect/Great/Good だけ(Bad・Miss・ロングの時間切れは光らせない)。判定や入力は変えない。
public sealed class SaberCutFlash : MonoBehaviour
{
    ScoreManager score;
    SaberCutJudge first, second;
    public int NotifiedCount { get; private set; }

    public static SaberCutFlash Attach(GameObject host, ScoreManager score, SaberCutJudge first, SaberCutJudge second)
    {
        if (host == null) return null;
        var flash = host.GetComponent<SaberCutFlash>() ?? host.AddComponent<SaberCutFlash>();
        flash.Bind(score, first, second);
        return flash;
    }

    public void Bind(ScoreManager scoring, SaberCutJudge one, SaberCutJudge two)
    {
        if (score != null) score.OnJudgment -= Judged;
        score = scoring; first = one; second = two;
        if (score != null && enabled && gameObject.activeInHierarchy) score.OnJudgment += Judged;
    }

    void OnEnable() { if (score != null) { score.OnJudgment -= Judged; score.OnJudgment += Judged; } }
    void OnDisable() { if (score != null) score.OnJudgment -= Judged; }

    void Judged(JudgmentTier tier, int awarded)
    {
        if (score == null || score.LastCutTimedOut || !GameplayCutFeedback.Draws(tier)) return;
        var bridge = BridgeFor(score.LastCutHand);
        if (bridge == null) return;
        bridge.NotifyCut();
        NotifiedCount++;
    }

    // 切った手のセーバー。マウス(手の区別なし)で切ったときは1本目。
    public SaberInputBridge BridgeFor(SaberHand hand)
    {
        if (hand != SaberHand.Any)
        {
            if (Matches(second, hand)) return Bridge(second);
            if (Matches(first, hand)) return Bridge(first);
        }
        return Bridge(first);
    }

    static bool Matches(SaberCutJudge judge, SaberHand hand) => judge != null && judge.hand == hand;
    static SaberInputBridge Bridge(SaberCutJudge judge)
    {
        if (judge == null) return null;
        return judge.bladeProvider != null ? judge.bladeProvider : judge.GetComponent<SaberInputBridge>();
    }
}
