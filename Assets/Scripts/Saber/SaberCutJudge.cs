using System.Collections.Generic;
using UnityEngine;

// セーバー軌跡の XY 成分だけでノーツとの当たり判定を取る（Z は無視）。
// 刃の有効範囲に入った瞬間を entry として記録し、範囲から抜けたら Cut を発火させる。
// 曲時計を受け取ったときは、刃がノーツにいちばん近づいた時刻で採点する(当たりの大きさで判定の時刻がずれない)。
// 1回の振りで切れるのは1つの時刻のノーツだけ(同時のノーツはまとめて)。後続のノーツは同じ振りの続きでは切れない。
public class SaberCutJudge : MonoBehaviour
{
    // 同時に切れるとみなすノーツの時刻の差(秒)。譜面の同時ノーツは時刻差0で、後続のノーツは0.12秒以上離れている。
    public const float SimultaneousSeconds = .025f;

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
    // 振りが止まったとみなす時間(秒)。カメラの値は間引かれて届き、値の来ないフレームは速度0になるので短くしすぎない。
    public float strokeRestSeconds = 0.08f;
    // 動く向きがこの角度より大きく変わったら、次の振り(振り返し)として扱う。
    public float strokeTurnDegrees = 100f;

    // GamePlayManager / GManager から RunJudge() を呼ぶときは false にして
    // 自前の Update を止め、外部から1点で呼び出す（GManager 主体パターン）。
    public bool autonomous = true;

    // 本編のIMU+Camera判定がこの棒を担当する間だけ、従来の接触履歴を止める。
    public bool ExternalJudgment
    {
        get => externalJudgment;
        set { if (externalJudgment != value) { pending.Clear(); ResetStroke(); } externalJudgment = value; }
    }
    bool externalJudgment;

    private struct Pending
    {
        public uint version;
        public Vector3 hitPoint;
        public Vector3 velocity;
        // 刃がいちばん近づいたときの距離と曲時計。ノーツが切れる時間内のフレームだけで更新する(時計がなければ NaN)。
        public float bestDistance;
        public double bestSongTime;
    }

    private readonly Dictionary<CuttableNote, Pending> pending = new Dictionary<CuttableNote, Pending>();
    readonly List<CuttableNote> toRemove = new List<CuttableNote>(16);
    readonly List<KeyValuePair<CuttableNote, Pending>> tracked = new List<KeyValuePair<CuttableNote, Pending>>(16);
    private SaberTracker contactTracker;
    private int contactResetVersion;
    double songTime = double.NaN;

    // 今の振り。止まるか向きが大きく変わるまでを1回とし、その振りで切った時刻(未カットは NaN)を覚える。
    int strokeTick = -1;
    bool strokeMoving;
    Vector2 strokeDirection;
    float strokeRest;
    double strokeGroupTime = double.NaN;
    readonly Dictionary<CuttableNote, uint> strokeBlocked = new Dictionary<CuttableNote, uint>();

    public int PendingCount => pending.Count;

    // 入力の停止をまたいで、以前の刃の進入を次の振りへ持ち越さない。
    void OnDisable() { pending.Clear(); ResetStroke(); }

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

    // 本編: 曲時計を渡すと、刃がいちばん近づいた時刻で採点する。
    public void RunJudge(double currentSongTime)
    {
        TryCut(currentSongTime);
    }

    public int TryCut() => TryCut(double.NaN);

    // 戻り値は、このフレームで刃が抜けたノーツの数(逆方向などで切れなかったものも数える)。
    public int TryCut(double currentSongTime)
    {
        songTime = currentSongTime;
        if (ExternalJudgment) { pending.Clear(); ResetStroke(); return 0; }
        if (ScreenTransition.IsBusy || !isActiveAndEnabled || saber == null || !saber.HasPrevious)
        {
            pending.Clear();
            ResetStroke();
            return 0;
        }
        // 再開後のサンプルが先に来ても、リセット前のentryだけは引き継がない。
        if (contactTracker != saber || contactResetVersion != saber.ResetVersion)
        {
            pending.Clear();
            ResetStroke();
            contactTracker = saber;
            contactResetVersion = saber.ResetVersion;
        }
        UpdateStroke();
        // ブレード（線分）モード優先。利用不可なら従来の点-軌跡モードへ。
        if (bladeProvider != null && bladeProvider.HasBlade)
        {
            return TryCutBlade();
        }
        return TryCutPoint();
    }

    private int TryCutBlade()
    {
        if (saber.Speed < minCutSpeed) return CheckExitsBlade();

        Vector2 a = new Vector2(bladeProvider.WorldEndA.x, bladeProvider.WorldEndA.y);
        Vector2 b = new Vector2(bladeProvider.WorldEndB.x, bladeProvider.WorldEndB.y);
        float hitRange = bladeRadius + noteHitRadiusXY;

        var notes = CuttableNote.ActiveNotes;
        for (int i=0;i<notes.Count;i++)
        {
            var note = notes[i];
            if (!IsCandidate(note)) continue;
            // 担当外の手のノーツはそもそも判定対象にしない(誤った手のスイングは無反応)
            if (!SaberHandHelper.CanCut(note.RequiredHand, EffectiveHand())) continue;
            if (pending.ContainsKey(note) || ExcludedFromStroke(note)) continue;
            Vector2 noteXY = new Vector2(note.transform.position.x, note.transform.position.y);
            float d = DistPointToSegment(noteXY, a, b, out Vector2 closest);
            if (d <= hitRange) pending[note] = NewPending(note, closest, d);
        }
        return CheckExitsBlade();
    }

    private int CheckExitsBlade()
    {
        if (pending.Count == 0) return 0;
        Vector2 a = new Vector2(bladeProvider.WorldEndA.x, bladeProvider.WorldEndA.y);
        Vector2 b = new Vector2(bladeProvider.WorldEndB.x, bladeProvider.WorldEndB.y);
        float hitRange = bladeRadius + noteHitRadiusXY;

        toRemove.Clear(); tracked.Clear();
        int cuts = 0;
        // 同時に抜けたノーツは、期限境界でも同じヒントで評価する（消費しない）。
        CutDirection imuHint = ResolveImuHint();
        foreach (var kv in pending)
        {
            CuttableNote note = kv.Key;
            if (!StillCuttable(note, kv.Value))
            {
                toRemove.Add(note);
                continue;
            }
            Vector2 noteXY = new Vector2(note.transform.position.x, note.transform.position.y);
            float distNow = DistPointToSegment(noteXY, a, b, out Vector2 closest);
            if (distNow > hitRange)
            {
                if (TakeTurn(note)) Finish(note, kv.Value, imuHint);
                cuts++;
                toRemove.Add(note);
            }
            else Track(note, kv.Value, distNow, closest);
        }
        Apply();
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

        var notes = CuttableNote.ActiveNotes;
        for (int i=0;i<notes.Count;i++)
        {
            var note = notes[i];
            if (!IsCandidate(note)) continue;
            // 担当外の手のノーツはそもそも判定対象にしない(誤った手のスイングは無反応)
            if (!SaberHandHelper.CanCut(note.RequiredHand, EffectiveHand())) continue;
            if (pending.ContainsKey(note) || ExcludedFromStroke(note)) continue;
            Vector2 noteXY = new Vector2(note.transform.position.x, note.transform.position.y);
            float d = DistPointToSegment(noteXY, from, to, out Vector2 closest);
            if (d <= hitRange) pending[note] = NewPending(note, closest, d);
        }

        return CheckExitsPoint();
    }

    private int CheckExitsPoint()
    {
        if (pending.Count == 0) return 0;
        Vector2 now = new Vector2(saber.CurrentPosition.x, saber.CurrentPosition.y);
        float hitRange = bladeRadius + noteHitRadiusXY;

        toRemove.Clear(); tracked.Clear();
        int cuts = 0;
        CutDirection imuHint = ResolveImuHint();
        foreach (var kv in pending)
        {
            CuttableNote note = kv.Key;
            if (!StillCuttable(note, kv.Value))
            {
                toRemove.Add(note);
                continue;
            }
            Vector2 noteXY = new Vector2(note.transform.position.x, note.transform.position.y);
            float distNow = Vector2.Distance(now, noteXY);
            if (distNow > hitRange)
            {
                if (TakeTurn(note)) Finish(note, kv.Value, imuHint);
                cuts++;
                toRemove.Add(note);
            }
            else Track(note, kv.Value, distNow, now);
        }
        Apply();
        return cuts;
    }

    Pending NewPending(CuttableNote note, Vector2 closest, float distance)
    {
        return new Pending
        {
            version = note.SpawnVersion,
            hitPoint = new Vector3(closest.x, closest.y, note.transform.position.z),
            velocity = saber.Velocity,
            bestDistance = distance,
            // 進入は判定窓の中でしか起きない(IsCandidate)ので、進入時刻はそのまま採点に使える。
            bestSongTime = Finite(songTime) ? songTime : double.NaN
        };
    }

    // 範囲内にとどまっている間は、いちばん近づいた瞬間を更新する(判定窓の中のフレームだけ)。
    void Track(CuttableNote note, Pending p, float distance, Vector2 closest)
    {
        if (!Finite(songTime) || !note.IsJudgeable) return;
        if (Finite(p.bestSongTime) && distance >= p.bestDistance) return;
        p.bestDistance = distance;
        p.bestSongTime = songTime;
        p.hitPoint = new Vector3(closest.x, closest.y, note.transform.position.z);
        if (saber.Speed >= minCutSpeed) p.velocity = saber.Velocity;
        tracked.Add(new KeyValuePair<CuttableNote, Pending>(note, p));
    }

    // 列挙中に辞書を書き換えないよう、ループのあとでまとめて反映する。
    void Apply()
    {
        foreach (var n in toRemove) pending.Remove(n);
        foreach (var kv in tracked) if (pending.ContainsKey(kv.Key)) pending[kv.Key] = kv.Value;
        toRemove.Clear(); tracked.Clear();
    }

    void Finish(CuttableNote note, Pending p, CutDirection imuHint)
    {
        double? judged = Finite(p.bestSongTime) ? p.bestSongTime : (double?)null;
        bool accepted = note.CutAtSongTime(p.hitPoint, p.velocity, imuHint, EffectiveHand(), judged);
        if (accepted && Grouped(note) && double.IsNaN(strokeGroupTime)) strokeGroupTime = note.HitTime;
    }

    // 抜けたノーツを今の振りで切ってよいか。1回の振りでは1つの時刻のノーツだけを切る。
    // まだ何も切っていない振りが複数のノーツにかかっていたら、タイミングのいちばん合ったノーツを選ぶ。
    bool TakeTurn(CuttableNote note)
    {
        if (!Grouped(note)) return true;
        if (!double.IsNaN(strokeGroupTime))
            return System.Math.Abs(note.HitTime - strokeGroupTime) <= SimultaneousSeconds || Block(note);
        CuttableNote best = BestTimed();
        if (best == null || System.Math.Abs(note.HitTime - best.HitTime) <= SimultaneousSeconds) return true;
        return Block(note);
    }

    bool Block(CuttableNote note)
    {
        strokeBlocked[note] = note.SpawnVersion;
        return false;
    }

    // 保留中のノーツのうち、近づいた時刻がヒット時刻にいちばん近いもの(時計がなければヒット時刻が早いもの)。
    CuttableNote BestTimed()
    {
        CuttableNote best = null;
        bool bestTimed = false;
        double bestScore = double.PositiveInfinity;
        foreach (var kv in pending)
        {
            CuttableNote note = kv.Key;
            if (!Grouped(note) || !StillCuttable(note, kv.Value) || IsBlocked(note)) continue;
            bool timed = Finite(kv.Value.bestSongTime);
            double score = timed ? System.Math.Abs(kv.Value.bestSongTime - note.HitTime) : note.HitTime;
            bool better = best == null
                || (timed && !bestTimed)
                || (timed == bestTimed && (score < bestScore || (score == bestScore && note.HitTime < best.HitTime)));
            if (!better) continue;
            best = note; bestTimed = timed; bestScore = score;
        }
        return best;
    }

    // 今の振りでは切らないノーツ: この振りで見送ったもの、またはすでに切った時刻と同時でない単発ノーツ。
    bool ExcludedFromStroke(CuttableNote note)
    {
        if (IsBlocked(note)) return true;
        return Grouped(note) && !double.IsNaN(strokeGroupTime)
            && System.Math.Abs(note.HitTime - strokeGroupTime) > SimultaneousSeconds;
    }

    bool IsBlocked(CuttableNote note) => strokeBlocked.TryGetValue(note, out uint version) && version == note.SpawnVersion;

    // ロングは何度も切る仕組みなので、1回の振りの決まりの対象外。
    static bool Grouped(CuttableNote note) => note.RequiredCutCount <= 1;

    // 振りの区切り: 速度が出ている間は同じ振り。止まって strokeRestSeconds たつか、向きが大きく変わったら次の振り。
    void UpdateStroke()
    {
        if (saber.TickCount == strokeTick) return; // 同じ位置の更新を二重に数えない
        strokeTick = saber.TickCount;
        Vector2 velocity = new Vector2(saber.Velocity.x, saber.Velocity.y);
        if (saber.Speed >= minCutSpeed && velocity.sqrMagnitude > 1e-8f)
        {
            Vector2 direction = velocity.normalized;
            if (strokeMoving && Vector2.Dot(direction, strokeDirection) < Mathf.Cos(strokeTurnDegrees * Mathf.Deg2Rad))
                NextStroke();
            strokeDirection = strokeMoving ? (strokeDirection + direction).normalized : direction;
            strokeMoving = true;
            strokeRest = 0;
        }
        else if (strokeMoving)
        {
            strokeRest += saber.LastDeltaTime;
            if (strokeRest >= strokeRestSeconds) NextStroke();
        }
    }

    void NextStroke()
    {
        strokeMoving = false;
        strokeRest = 0;
        strokeGroupTime = double.NaN;
        strokeBlocked.Clear();
    }

    void ResetStroke()
    {
        NextStroke();
        strokeTick = -1;
    }

    private static bool IsCandidate(CuttableNote n)
    {
        if (n == null) return false;
        if (n.IsCut || n.IsMissed) return false;
        if (!n.IsJudgeable) return false;
        if (!n.gameObject.activeInHierarchy) return false;
        return true;
    }

    // 抜けた時点でまだ切ってよいか。曲時計がある本編では、近づいたのが判定窓の中なら窓が閉じた直後でも切れる。
    // 時計がない(タイトル・単体テスト)ときは従来どおり、今の判定窓で決める。
    static bool StillCuttable(CuttableNote note, Pending p)
    {
        if (note == null || note.SpawnVersion != p.version) return false;
        if (note.IsCut || note.IsMissed || !note.gameObject.activeInHierarchy) return false;
        return note.IsJudgeable || Finite(p.bestSongTime);
    }

    static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

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
