using System.Collections.Generic;
using UnityEngine;

// セーバー軌跡の XY 成分だけでノーツとの当たり判定を取る（Z は無視）。
// 刃の有効範囲に入った瞬間を entry として記録し、範囲から抜けたら Cut を発火させる。
// 曲時計を受け取ったときは、刃がノーツにいちばん近づいた時刻で採点する(当たりの大きさで判定の時刻がずれない)。
// さらに曲時計があるとき(本編・チュートリアル・判定調整)は、抜け切るのを待たず、刃の中心がノーツを通り過ぎたら切る。
// そのときの向きは、入った瞬間の速さではなく刃の中心が動いた向きで決める(構えの振りを拾わない)。
// 曲時計があるときは、前に判定したときの刃から今の刃までが通った面でも入ったかを見て(速い振りの飛び越し)、
// 採点は刃の中心がノーツを通り過ぎた時刻にする(縦の刃を上下に振ったとき、先端が触れた時刻で早めに出ないように)。
// 1回の振りで切れるのは1つの時刻のノーツだけ(同時のノーツはまとめて)。後続のノーツは同じ振りの続きでは切れない。
public class SaberCutJudge : MonoBehaviour
{
    // 同時に切れるとみなすノーツの時刻の差(秒)。譜面の同時ノーツは時刻差0で、後続のノーツは0.12秒以上離れている。
    public const float SimultaneousSeconds = .025f;
    // 向きを刃の中心の動きで決めるのに要る最小の移動量。これより短ければ従来どおり速さの向きを使う。
    const float DirectionTravel = .5f;
    // 通り過ぎた時刻を前後の刃の値の間で割り振るのは、値の間がこれ以下のときだけ(秒)。長ければ今の値の時刻にする。
    const double MaxStepSeconds = .05;

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
    // 刃の中心が入ってから(振り返したらそこから)この距離以上動いて、ノーツより先へ出たら、抜け切る前に切る。
    // 手の揺れ(止めていても1フレームに最大約0.5)で切れないよう、それより大きくする。曲時計があるときだけ使う。
    public float passCutTravel = 1.0f;
    // 向きが合うとみなす幅(±度)。曲時計があるときだけ使い、時計がないタイトル等は従来の±30°のまま。
    [Range(0f, 90f)] public float directionToleranceDegrees = 45f;
    // iPhone カメラの値は縦が横の約1/2.4に縮んで届く(縦長480×640の映像を1920×1080へ縦横別の倍率で広げ、
    // それを判定面の±5.5×±3へ写すため。位置合わせ(4隅)を使うと変わる)。向きを比べるときだけ縦の動きをこの倍率で戻す。
    // カメラの値が届いていて曲時計があるときだけ使う。画面の刃・切り口・破片の向きは変えない。
    [Range(1f, 4f)] public float verticalDirectionScale = 2.4f;

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
        // 通り過ぎたかと向きを測る起点(刃の中心が入った位置。振り返したらその位置へ置き直す)と、
        // 起点からいちばん近づいたところまでの中心の動き(抜けて切るときの向き)。
        public Vector2 anchor;
        public Vector2 approach;
        // 刃の中心がノーツを通り過ぎた時刻とそのときの動き(判定窓の中のときだけ。なければ NaN)。
        // 最新の1回と、その前の1回を持つ(突いて引き戻したとき、切った向きと同じ向きの通過を選べるように)。
        public double crossSongTime, previousCrossSongTime;
        public Vector2 crossDirection, previousCrossDirection;
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
    // 次の振りに変わった(保留中のノーツの起点を置き直す)。
    bool strokeRestarted;

    // 刃の中心のひとつ前の値と今の値(カメラの新しい値で動いたときだけ進める)と、それぞれを初めて見た曲時計。
    Vector2 stepFrom, stepTo;
    double stepFromTime = double.NaN, stepToTime = double.NaN;
    bool hasStepFrom, hasStepTo;
    // 前に入りを調べたときの刃(通った面で調べる起点)。
    Vector2 sweepA, sweepB;
    bool hasSweep;
    bool lastModeWasBlade;

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

    // 本編: 曲時計を渡すと、刃の中心がノーツを通り過ぎた時刻(なければいちばん近づいた時刻)で採点する。
    public void RunJudge(double currentSongTime)
    {
        TryCut(currentSongTime);
    }

    public int TryCut() => TryCut(double.NaN);

    // 戻り値は、このフレームで刃が抜けた(または通り過ぎた)ノーツの数(逆方向などで切れなかったものも数える)。
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
        bool blade = bladeProvider != null && bladeProvider.HasBlade;
        // ブレードと点を切り替えたら、前の刃の記録を引き継がない。
        if (blade != lastModeWasBlade) { ResetMotion(); lastModeWasBlade = blade; }
        // 振り返したら、保留中のノーツの起点を今の刃の中心へ置き直す(次の振りで通り過ぎたかを見る)。
        if (strokeRestarted) Reanchor(Center(blade));
        TrackStep(Center(blade));
        return blade ? TryCutBlade() : TryCutPoint();
    }

    private int TryCutBlade()
    {
        if (saber.Speed < minCutSpeed) return CheckPending(true);

        Vector2 a = new Vector2(bladeProvider.WorldEndA.x, bladeProvider.WorldEndA.y);
        Vector2 b = new Vector2(bladeProvider.WorldEndB.x, bladeProvider.WorldEndB.y);
        Vector2 center = (a + b) * .5f;
        float hitRange = bladeRadius + noteHitRadiusXY;
        // 曲時計があるときは、前に調べたときの刃から今の刃までが通った面でも入ったかを見る(速い振りで円を飛び越えても拾う)。
        // 端の向きはそろえる。誤認識で刃が飛んだとき(中心が maxCutDistance より動いた)は使わない。
        Vector2 a0 = sweepA, b0 = sweepB;
        bool sweep = Finite(songTime) && hasSweep
            && ((a0 + b0) * .5f - center).sqrMagnitude <= maxCutDistance * maxCutDistance;
        if (sweep && (a0 - b).sqrMagnitude + (b0 - a).sqrMagnitude < (a0 - a).sqrMagnitude + (b0 - b).sqrMagnitude)
        {
            a0 = sweepB; b0 = sweepA;
        }

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
            Vector2 anchor = center;
            if (d > hitRange && sweep)
            {
                // 通った面で入ったときは、動き始めた位置(前の刃の中心)を起点にする。
                float swept = CameraSaberHistory.SweptDistance(noteXY, a0, b0, a, b, out Vector2 sweptClosest);
                if (swept <= hitRange) { d = swept; closest = sweptClosest; anchor = (a0 + b0) * .5f; }
            }
            if (d <= hitRange) pending[note] = NewPending(note, closest, d, anchor, center);
        }
        sweepA = a; sweepB = b; hasSweep = true;
        return CheckPending(true);
    }

    private int TryCutPoint()
    {
        if (saber.Speed < minCutSpeed) return CheckPending(false);

        Vector2 from = new Vector2(saber.PreviousPosition.x, saber.PreviousPosition.y);
        Vector2 to = new Vector2(saber.CurrentPosition.x, saber.CurrentPosition.y);
        Vector2 delta = to - from;
        float dist = delta.magnitude;
        if (dist <= 0.0001f) return CheckPending(false);
        if (dist > maxCutDistance) return CheckPending(false);

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
            // 点は1フレームの軌跡で入るので、起点はその軌跡の始まり。
            if (d <= hitRange) pending[note] = NewPending(note, closest, d, from, to);
        }

        return CheckPending(false);
    }

    // 保留中のノーツを見て、刃が範囲から抜けたら切る。曲時計があれば、刃の中心がノーツを通り過ぎたときにも切る。
    private int CheckPending(bool blade)
    {
        if (pending.Count == 0) return 0;
        Vector2 a = blade ? new Vector2(bladeProvider.WorldEndA.x, bladeProvider.WorldEndA.y) : Vector2.zero;
        Vector2 b = blade ? new Vector2(bladeProvider.WorldEndB.x, bladeProvider.WorldEndB.y) : Vector2.zero;
        Vector2 center = Center(blade);
        float hitRange = bladeRadius + noteHitRadiusXY;

        toRemove.Clear(); tracked.Clear();
        // 先に、刃の中心がノーツを通り過ぎた時刻を全部のノーツで覚える(どのノーツを切るか選ぶときにも使う)。
        if (Finite(songTime) && hasStepFrom)
        {
            foreach (var kv in pending)
                if (StillCuttable(kv.Key, kv.Value)) TrackCrossing(kv.Key, kv.Value);
            foreach (var kv in tracked) pending[kv.Key] = kv.Value;
            tracked.Clear();
        }
        int cuts = 0;
        // 同時に抜けたノーツは、期限境界でも同じヒントで評価する（消費しない）。
        CutDirection imuHint = ResolveImuHint();
        foreach (var kv in pending)
        {
            CuttableNote note = kv.Key;
            Pending p = kv.Value;
            if (!StillCuttable(note, p))
            {
                toRemove.Add(note);
                continue;
            }
            Vector2 noteXY = new Vector2(note.transform.position.x, note.transform.position.y);
            Vector2 closest = center;
            float distNow = blade ? DistPointToSegment(noteXY, a, b, out closest) : Vector2.Distance(center, noteXY);
            if (distNow > hitRange)
            {
                if (TakeTurn(note)) Finish(note, p, imuHint, ExitVelocity(p, center));
                cuts++;
                toRemove.Add(note);
                continue;
            }
            p = Track(note, p, distNow, closest, center);
            if (!Passed(p, center, noteXY)) continue;
            // 通り過ぎた: 抜け切るのを待たずに切る。切れなかったら(逆向き・ロングの途中)この振りではもう切らない。
            // 構えの振りで通り過ぎても、振り返してから入り直すので、振り下ろしの時刻で採点される。
            if (TakeTurn(note))
            {
                Finish(note, p, imuHint, SwingVelocity(p, center - p.anchor));
                if (!note.IsCut) Block(note);
            }
            cuts++;
            toRemove.Add(note);
        }
        Apply();
        return cuts;
    }

    Pending NewPending(CuttableNote note, Vector2 closest, float distance, Vector2 anchor, Vector2 center)
    {
        return new Pending
        {
            version = note.SpawnVersion,
            hitPoint = new Vector3(closest.x, closest.y, note.transform.position.z),
            velocity = saber.Velocity,
            bestDistance = distance,
            // 進入は判定窓の中でしか起きない(IsCandidate)ので、進入時刻はそのまま採点に使える。
            bestSongTime = Finite(songTime) ? songTime : double.NaN,
            anchor = anchor,
            approach = center - anchor,
            crossSongTime = double.NaN,
            previousCrossSongTime = double.NaN
        };
    }

    // 刃の中心が、動いた向きで見てノーツを通り過ぎた時刻を覚える(曲時計があり、判定窓の中のときだけ)。
    // 時刻は前後の刃の値の間で割り振る。同じ値の組では何度呼んでも同じ結果になる。変わったら tracked に積む。
    void TrackCrossing(CuttableNote note, Pending p)
    {
        if (!Finite(songTime) || !hasStepFrom || !note.IsJudgeable) return;
        Vector2 noteXY = new Vector2(note.transform.position.x, note.transform.position.y);
        Vector2 step = stepTo - stepFrom;
        float length2 = step.sqrMagnitude;
        if (length2 < 1e-8f) return;
        float ahead = Vector2.Dot(noteXY - stepFrom, step);
        if (ahead <= 0f || Vector2.Dot(noteXY - stepTo, step) > 0f) return;
        double span = stepToTime - stepFromTime;
        double time = Finite(span) && span >= 0 && span <= MaxStepSeconds
            ? stepFromTime + span * (ahead / length2)
            : stepToTime;
        if (time == p.crossSongTime && step == p.crossDirection) return;
        p.previousCrossSongTime = p.crossSongTime;
        p.previousCrossDirection = p.crossDirection;
        p.crossSongTime = time;
        p.crossDirection = step;
        tracked.Add(new KeyValuePair<CuttableNote, Pending>(note, p));
    }

    // 刃の中心が動いたら(カメラの新しい値)、ひとつ前の値と今の値を進める。時刻はその値を初めて見たときの曲時計。
    void TrackStep(Vector2 center)
    {
        if (hasStepTo && center == stepTo) return;
        stepFrom = stepTo; stepFromTime = stepToTime; hasStepFrom = hasStepTo;
        stepTo = center; stepToTime = songTime; hasStepTo = true;
    }

    void ResetMotion()
    {
        hasStepFrom = hasStepTo = hasSweep = false;
    }

    // 範囲内にとどまっている間は、いちばん近づいた瞬間を更新する(判定窓の中のフレームだけ)。
    Pending Track(CuttableNote note, Pending p, float distance, Vector2 closest, Vector2 center)
    {
        if (!Finite(songTime) || !note.IsJudgeable) return p;
        if (Finite(p.bestSongTime) && distance >= p.bestDistance) return p;
        p.bestDistance = distance;
        p.bestSongTime = songTime;
        p.hitPoint = new Vector3(closest.x, closest.y, note.transform.position.z);
        p.approach = center - p.anchor;
        if (saber.Speed >= minCutSpeed) p.velocity = saber.Velocity;
        tracked.Add(new KeyValuePair<CuttableNote, Pending>(note, p));
        return p;
    }

    // 刃の中心が起点から passCutTravel 以上動き、動いた向きで見てノーツより先へ出たか(曲時計があるときだけ)。
    bool Passed(Pending p, Vector2 center, Vector2 noteXY)
    {
        if (!Finite(songTime)) return false;
        Vector2 travel = center - p.anchor;
        float min = Mathf.Max(passCutTravel, .0001f);
        if (travel.sqrMagnitude < min * min) return false;
        return Vector2.Dot(noteXY - p.anchor, travel) > 0f && Vector2.Dot(noteXY - center, travel) < 0f;
    }

    // 抜けて切るときの向き: 起点からいちばん近づいたところまでの中心の動き(短ければ起点から今まで)。
    // 曲時計がないとき(タイトル・単体テスト)は従来どおり、入った(近づいた)ときの速さ。
    Vector3 ExitVelocity(Pending p, Vector2 center)
    {
        if (!Finite(songTime)) return p.velocity;
        return SwingVelocity(p, p.approach.sqrMagnitude >= DirectionTravel * DirectionTravel ? p.approach : center - p.anchor);
    }

    // 刃の中心が動いた向きに、従来の速さの大きさを乗せる(破片の飛び方は変えない)。動きが短すぎれば従来の速さ。
    static Vector3 SwingVelocity(Pending p, Vector2 travel)
    {
        if (travel.sqrMagnitude < DirectionTravel * DirectionTravel) return p.velocity;
        Vector2 v = travel.normalized * p.velocity.magnitude;
        return new Vector3(v.x, v.y, 0f);
    }

    // 振り返したら、保留中のノーツの起点を今の刃の中心へ置き直す(いちばん近づいた記録はそのまま)。
    void Reanchor(Vector2 center)
    {
        strokeRestarted = false;
        if (pending.Count == 0) return;
        tracked.Clear();
        foreach (var kv in pending)
        {
            Pending p = kv.Value;
            p.anchor = center;
            tracked.Add(new KeyValuePair<CuttableNote, Pending>(kv.Key, p));
        }
        foreach (var kv in tracked) pending[kv.Key] = kv.Value;
        tracked.Clear();
    }

    // 刃の中心(ブレードは両端の中点、点モードは今の位置)。
    Vector2 Center(bool blade)
    {
        if (blade) return new Vector2((bladeProvider.WorldEndA.x + bladeProvider.WorldEndB.x) * .5f,
            (bladeProvider.WorldEndA.y + bladeProvider.WorldEndB.y) * .5f);
        return new Vector2(saber.CurrentPosition.x, saber.CurrentPosition.y);
    }

    // 刃がこのノーツの上を通っている最中か(判定窓の中で近づき、まだ抜けても通り過ぎてもいない)。
    // NoteSpawner はこの間、締め切りのあとも少しだけ Miss を待つ(そのあいだに通り過ぎれば切れる)。
    public bool IsCuttingThrough(CuttableNote note)
    {
        if (note == null || !Finite(songTime) || ExternalJudgment) return false;
        return pending.TryGetValue(note, out Pending p) && StillCuttable(note, p) && Finite(p.bestSongTime);
    }

    // 列挙中に辞書を書き換えないよう、ループのあとでまとめて反映する。
    void Apply()
    {
        foreach (var n in toRemove) pending.Remove(n);
        foreach (var kv in tracked) if (pending.ContainsKey(kv.Key)) pending[kv.Key] = kv.Value;
        toRemove.Clear(); tracked.Clear();
    }

    void Finish(CuttableNote note, Pending p, CutDirection imuHint, Vector3 velocity)
    {
        bool clocked = Finite(songTime);
        Vector2 motion = new Vector2(velocity.x, velocity.y);
        double? judged = clocked ? ScoreTime(note, p, motion) : Finite(p.bestSongTime) ? p.bestSongTime : (double?)null;
        // 向きの幅は曲時計があるときだけ広げる(時計がないタイトル等は従来の±30°)。
        float tolerance = clocked ? Mathf.Cos(directionToleranceDegrees * Mathf.Deg2Rad) : .866f;
        // カメラの値は縦が縮んで届くので、向きを比べるときだけ縦の動きを戻す(切り口や破片の向きは画面の動きのまま)。
        Vector2? direction = clocked && ReceivingCamera
            ? new Vector2(motion.x, motion.y * verticalDirectionScale) : (Vector2?)null;
        bool accepted = note.CutAtSongTime(p.hitPoint, velocity, imuHint, EffectiveHand(), judged, tolerance, direction);
        if (accepted && Grouped(note) && double.IsNaN(strokeGroupTime)) strokeGroupTime = note.HitTime;
    }

    bool ReceivingCamera => bladeProvider != null && bladeProvider.ReceivingCamera;

    // 採点の時刻: 刃の中心がノーツを通り過ぎた時刻(切った向きと同じ向きに通り過ぎたもの)。なければいちばん近づいた時刻。
    // 1回で切るノーツは判定窓の端に収める(切れたのに MISS と採点しない)。ロングは完了の割合で採点する。
    double? ScoreTime(CuttableNote note, Pending p, Vector2 cutMotion)
    {
        double time = p.bestSongTime;
        if (Finite(p.crossSongTime) && Vector2.Dot(p.crossDirection, cutMotion) > 0f) time = p.crossSongTime;
        else if (Finite(p.previousCrossSongTime) && Vector2.Dot(p.previousCrossDirection, cutMotion) > 0f) time = p.previousCrossSongTime;
        if (!Finite(time)) return null;
        if (Grouped(note))
            time = System.Math.Max(note.HitTime - JudgmentTierHelper.EarlyBadSeconds,
                System.Math.Min(note.HitTime + JudgmentTierHelper.LateBadSeconds, time));
        return time;
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

    // 保留中のノーツのうち、通り過ぎた(なければ近づいた)時刻がヒット時刻にいちばん近いもの(時計がなければヒット時刻が早いもの)。
    CuttableNote BestTimed()
    {
        CuttableNote best = null;
        bool bestTimed = false;
        double bestScore = double.PositiveInfinity;
        foreach (var kv in pending)
        {
            CuttableNote note = kv.Key;
            if (!Grouped(note) || !StillCuttable(note, kv.Value) || IsBlocked(note)) continue;
            double time = Finite(kv.Value.crossSongTime) ? kv.Value.crossSongTime : kv.Value.bestSongTime;
            bool timed = Finite(time);
            double score = timed ? System.Math.Abs(time - note.HitTime) : note.HitTime;
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
        strokeRestarted = true;
    }

    void ResetStroke()
    {
        NextStroke();
        strokeTick = -1;
        ResetMotion();
    }

    private static bool IsCandidate(CuttableNote n)
    {
        if (n == null) return false;
        if (n.IsCut || n.IsMissed) return false;
        if (!n.IsJudgeable) return false;
        if (!n.gameObject.activeInHierarchy) return false;
        return true;
    }

    // 抜けた(通り過ぎた)時点でまだ切ってよいか。曲時計がある本編では、近づいたのが判定窓の中なら窓が閉じたあとでも、
    // Miss が決まるまでは切れる(刃がノーツの上を通っている最中は、NoteSpawner が Miss を少し待つ)。
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
