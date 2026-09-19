using System.Collections.Generic;
using UnityEngine;

// セーバー軌跡の XY 成分だけでノーツとの当たり判定を取る（Z は無視）。
// 「完全に通り抜けた」時点で Cut を発火させるため、
// 刃の有効範囲に入った瞬間を entry として記録し、
// 以後セーバー現在位置がノーツから離れたら exit = Cut。
public class SaberCutJudge : MonoBehaviour
{
    public SaberTracker saber;
    // ブレード（線分）の端点を提供するソース。指定なし or HasBlade=false のときは従来の点-軌跡判定。
    public SaberInputBridge bladeProvider;
    // このセーバーの手(Left=青ノーツ担当 / Right=赤ノーツ担当 / Any=区別なし)。
    // マウスフォールバック中は EffectiveHand() が Any を返し、全色を切れる(ハード無しでの検証用)。
    public SaberHand hand = SaberHand.Any;
    public float bladeRadius = 0.3f;
    public float minCutSpeed = 3.0f;
    public float maxCutDistance = 3.0f;
    public float noteHitRadiusXY = 0.5f;
    // 直近の IMU 振り検知をどれだけ古くまで採用するか
    public float imuHintMaxAgeSeconds = 0.30f;

    // GamePlayManager / GManager から RunJudge() を呼ぶときは false にして
    // 自前の Update を止め、外部から1点で呼び出す（GManager 主体パターン）。
    public bool autonomous = true;

    private struct Pending
    {
        public Vector3 hitPoint;
        public Vector3 velocity;
    }

    private readonly Dictionary<CuttableNote, Pending> pending = new Dictionary<CuttableNote, Pending>();
    private SaberTracker contactTracker;
    private int contactResetVersion;
    struct RejectedContact
    {
        public Vector2 entry, velocity;
        public Vector3 point;
        public CutRejectionReason reason;
    }
    readonly Dictionary<CuttableNote, RejectedContact> rejectedContacts = new Dictionary<CuttableNote, RejectedContact>();
    readonly List<CuttableNote> rejectedRemoval = new List<CuttableNote>();

    public int PendingCount => pending.Count;

    // 入力の停止をまたいで、以前の刃の進入を次の振りへ持ち越さない。
    void OnDisable() { pending.Clear(); rejectedContacts.Clear(); }

    void Awake()
    {
        if (bladeProvider == null) bladeProvider = GetComponent<SaberInputBridge>();
    }

    void Update()
    {
        if (!autonomous) return;
        RunJudge();
    }

    public void RunJudge()
    {
        TryCut();
    }

    CuttableNote[] frameNotes;
    CuttableNote[] NotesForFrame() => frameNotes ?? (frameNotes = Object.FindObjectsByType<CuttableNote>(FindObjectsSortMode.None));

    public int TryCut()
    {
        frameNotes = null;
        if (ScreenTransition.IsBusy || !isActiveAndEnabled || saber == null || !saber.HasPrevious)
        {
            pending.Clear();
            rejectedContacts.Clear();
            return 0;
        }
        // 再開後のサンプルが先に来ても、リセット前のentryだけは引き継がない。
        if (contactTracker != saber || contactResetVersion != saber.ResetVersion)
        {
            pending.Clear();
            rejectedContacts.Clear();
            contactTracker = saber;
            contactResetVersion = saber.ResetVersion;
        }
        // ブレード（線分）モード優先。利用不可なら従来の点-軌跡モードへ。
        if (bladeProvider != null && bladeProvider.HasBlade)
        {
            int cuts = TryCutBlade();
            ObserveRejectedContacts(true);
            return cuts;
        }
        int pointCuts = TryCutPoint();
        ObserveRejectedContacts(false);
        return pointCuts;
    }

    // 既存の切断経路と独立した観察。判定中のノーツに触れて通過した時だけ説明する。
    // 静止・追跡ジャンプ・かすって同じ側へ戻る動き・正しい手の速度回復では説明を出さない。
    void ObserveRejectedContacts(bool blade)
    {
        Vector2 previous = saber.PreviousPosition, current = saber.CurrentPosition;
        Vector2 delta = current - previous;
        if (delta.magnitude > maxCutDistance) { rejectedContacts.Clear(); return; }
        Vector2 a = blade ? (Vector2)bladeProvider.WorldEndA : previous;
        Vector2 b = blade ? (Vector2)bladeProvider.WorldEndB : current;
        float range = bladeRadius + noteHitRadiusXY;
        bool moving = delta.sqrMagnitude > .0001f && saber.Speed >= minCutSpeed * .35f;
        if (moving)
        {
            foreach (var note in NotesForFrame())
            {
                if (!IsCandidate(note) || !note.HasRejectionListener || pending.ContainsKey(note)) continue;
                bool wrongHand = !SaberHandHelper.CanCut(note.RequiredHand, EffectiveHand());
                if (!wrongHand && saber.Speed >= minCutSpeed) { rejectedContacts.Remove(note); continue; }
                if (rejectedContacts.ContainsKey(note)) continue;
                if (DistPointToSegment(note.transform.position, a, b, out var point) > range) continue;
                rejectedContacts[note] = new RejectedContact {
                    entry = previous, velocity = delta.normalized,
                    point = new Vector3(point.x, point.y, note.transform.position.z),
                    reason = wrongHand ? CutRejectionReason.Hand : CutRejectionReason.Speed
                };
            }
        }
        rejectedRemoval.Clear();
        foreach (var pair in rejectedContacts)
        {
            var note = pair.Key;
            if (!IsCandidate(note) || pending.ContainsKey(note)) { rejectedRemoval.Add(note); continue; }
            float distance = blade ? DistPointToSegment(note.transform.position, a, b, out _) : Vector2.Distance(current, note.transform.position);
            if (distance <= range) continue;
            var contact = pair.Value;
            // ノーツの反対側へ抜けた接触だけ。戻し動作や端への接触を連続警告にしない。
            Vector2 center = note.transform.position;
            if (Vector2.Dot(current - center, contact.velocity) > range * .5f &&
                Vector2.Dot(contact.entry - center, contact.velocity) < -range * .5f &&
                (contact.reason == CutRejectionReason.Hand || saber.Speed < minCutSpeed))
                note.NotifyRejected(contact.reason, contact.point);
            rejectedRemoval.Add(note);
        }
        foreach (var note in rejectedRemoval) rejectedContacts.Remove(note);
    }

    private int TryCutBlade()
    {
        if (saber.Speed < minCutSpeed) return CheckExitsBlade();

        Vector2 a = new Vector2(bladeProvider.WorldEndA.x, bladeProvider.WorldEndA.y);
        Vector2 b = new Vector2(bladeProvider.WorldEndB.x, bladeProvider.WorldEndB.y);
        float hitRange = bladeRadius + noteHitRadiusXY;

        CuttableNote[] notes = NotesForFrame();
        foreach (var note in notes)
        {
            if (!IsCandidate(note)) continue;
            // 担当外の手のノーツはそもそも判定対象にしない(誤った手のスイングは無反応)
            if (!SaberHandHelper.CanCut(note.RequiredHand, EffectiveHand())) continue;
            if (pending.ContainsKey(note)) continue;
            Vector2 noteXY = new Vector2(note.transform.position.x, note.transform.position.y);
            float d = DistPointToSegment(noteXY, a, b, out Vector2 closest);
            if (d <= hitRange)
            {
                pending[note] = new Pending
                {
                    hitPoint = new Vector3(closest.x, closest.y, note.transform.position.z),
                    velocity = saber.Velocity
                };
            }
        }
        return CheckExitsBlade();
    }

    private int CheckExitsBlade()
    {
        if (pending.Count == 0) return 0;
        Vector2 a = new Vector2(bladeProvider.WorldEndA.x, bladeProvider.WorldEndA.y);
        Vector2 b = new Vector2(bladeProvider.WorldEndB.x, bladeProvider.WorldEndB.y);
        float hitRange = bladeRadius + noteHitRadiusXY;

        var toRemove = new List<CuttableNote>();
        int cuts = 0;
        // 同時に抜けたノーツは、期限境界でも同じヒントで評価する（消費しない）。
        CutDirection imuHint = ResolveImuHint();
        foreach (var kv in pending)
        {
            CuttableNote note = kv.Key;
            if (!IsCandidate(note))
            {
                toRemove.Add(note);
                continue;
            }
            Vector2 noteXY = new Vector2(note.transform.position.x, note.transform.position.y);
            float distNow = DistPointToSegment(noteXY, a, b, out _);
            if (distNow > hitRange)
            {
                note.Cut(kv.Value.hitPoint, kv.Value.velocity, imuHint, EffectiveHand());
                cuts++;
                toRemove.Add(note);
            }
        }
        foreach (var n in toRemove) pending.Remove(n);
        return cuts;
    }

    private int TryCutPoint()
    {
        if (saber.Speed < minCutSpeed) return CheckExitsPoint();

        Vector2 from = new Vector2(saber.PreviousPosition.x, saber.PreviousPosition.y);
        Vector2 to = new Vector2(saber.CurrentPosition.x, saber.CurrentPosition.y);
        Vector2 delta = to - from;
        float dist = delta.magnitude;
        if (dist <= 0.0001f) return CheckExitsPoint();
        if (dist > maxCutDistance) return CheckExitsPoint();

        float hitRange = bladeRadius + noteHitRadiusXY;

        CuttableNote[] notes = NotesForFrame();
        foreach (var note in notes)
        {
            if (!IsCandidate(note)) continue;
            // 担当外の手のノーツはそもそも判定対象にしない(誤った手のスイングは無反応)
            if (!SaberHandHelper.CanCut(note.RequiredHand, EffectiveHand())) continue;
            if (pending.ContainsKey(note)) continue;
            Vector2 noteXY = new Vector2(note.transform.position.x, note.transform.position.y);
            float d = DistPointToSegment(noteXY, from, to, out Vector2 closest);
            if (d <= hitRange)
            {
                pending[note] = new Pending
                {
                    hitPoint = new Vector3(closest.x, closest.y, note.transform.position.z),
                    velocity = saber.Velocity
                };
            }
        }

        return CheckExitsPoint();
    }

    private int CheckExitsPoint()
    {
        if (pending.Count == 0) return 0;
        Vector2 now = new Vector2(saber.CurrentPosition.x, saber.CurrentPosition.y);
        float hitRange = bladeRadius + noteHitRadiusXY;

        var toRemove = new List<CuttableNote>();
        int cuts = 0;
        CutDirection imuHint = ResolveImuHint();
        foreach (var kv in pending)
        {
            CuttableNote note = kv.Key;
            if (!IsCandidate(note))
            {
                toRemove.Add(note);
                continue;
            }
            Vector2 noteXY = new Vector2(note.transform.position.x, note.transform.position.y);
            float distNow = Vector2.Distance(now, noteXY);
            if (distNow > hitRange)
            {
                note.Cut(kv.Value.hitPoint, kv.Value.velocity, imuHint, EffectiveHand());
                cuts++;
                toRemove.Add(note);
            }
        }
        foreach (var n in toRemove) pending.Remove(n);
        return cuts;
    }

    private static bool IsCandidate(CuttableNote n)
    {
        if (n == null) return false;
        if (n.IsCut || n.IsMissed) return false;
        if (!n.IsJudgeable) return false;
        if (!n.gameObject.activeInHierarchy) return false;
        return true;
    }

    // この Judge が「どの手として」切るか。マウスフォールバック中は手の区別をしない(Any)。
    public SaberHand EffectiveHand()
    {
        if (hand == SaberHand.Any) return SaberHand.Any;
        if (bladeProvider != null && bladeProvider.UsingMouseFallback) return SaberHand.Any;
        return hand;
    }

    // 受信からN秒以内の方向だけを取得。配送遅延やTime.timeScaleで期限を延ばさない。
    private CutDirection ResolveImuHint()
    {
        return Swing8DirectionLogger.TryGetRecent(imuHintMaxAgeSeconds, out CutDirection direction)
            ? direction : CutDirection.None;
    }

    // 点 p と線分 a→b の最短距離と、線分上の最近点。
    public static float DistPointToSegment(Vector2 p, Vector2 a, Vector2 b, out Vector2 closest)
    {
        Vector2 ab = b - a;
        float denom = ab.sqrMagnitude;
        if (denom < 0.0001f)
        {
            closest = a;
            return Vector2.Distance(p, a);
        }
        float t = Vector2.Dot(p - a, ab) / denom;
        t = Mathf.Clamp01(t);
        closest = a + ab * t;
        return Vector2.Distance(p, closest);
    }
}
