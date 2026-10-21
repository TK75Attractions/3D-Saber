using System.Collections.Generic;
using UnityEngine;

// セーバー軌跡の XY 成分だけでノーツとの当たり判定を取る（Z は無視）。
// 刃の有効範囲に入った瞬間を entry として記録し、範囲から抜けたら Cut を発火させる。
// 曲時計を受け取ったときは、刃がノーツにいちばん近づいた時刻で採点する(当たりの大きさで判定の時刻がずれない)。
// さらに曲時計があるとき(本編・チュートリアル・判定調整)は、抜け切るのを待たず、刃がノーツを通り過ぎたら切る。
// 見るのは刃のうちノーツに届く部分(CuttingPoint)。刃の腹で払うときはノーツの横の位置にある刃の点、
// 細い側で振る(刃が動く向きに沿う)ときは先端なので、刃の中心が届くまで振り切らなくても切れる。
// そのときの向きは、入った瞬間の速さではなく刃の中心が動いた向きで決める(構えの振りを拾わない)。
// 曲時計があるときは、前に判定したときの刃から今の刃までが通った面でも入ったかを見て(速い振りの飛び越し)、
// 採点はノーツに届く部分がノーツの中心を通り過ぎた時刻にする(前後の刃の値の間で割り振る)。
// カメラの値で細い側から振ると、振りを横切る刃の幅がなくなるので、そのときだけ当たりを広げる(近くに別の時刻のノーツがなければ)。
// 1回の振りで切れるのは、基本は1つの時刻のノーツだけ(同時のノーツはまとめて)。振り抜けの勢いで後続のノーツを巻き込まない。
// ただし曲時計があるときは、隣のマスへ順番に来るノーツを、その間隔どおりになぞれば同じ振りで続けて切れる(Sweeps)。
public class SaberCutJudge : MonoBehaviour
{
    // 同時に切れるとみなすノーツの時刻の差(秒)。譜面の同時ノーツは時刻差0で、後続のノーツは0.06秒以上離れている。
    public const float SimultaneousSeconds = .025f;
    // 向きが合うとみなす幅の既定(±度)。これより外は向き違いで1段下げ、120°より外(逆向き)は切らない(CutDirectionHelper.ShouldRejectOpposite)。
    // 2026-10-21 に±45°から±60°へ(1段下げの帯を、外側の120°は変えずに内側から狭めた)。譜面のチェック(SaberChartLint)も同じ値。
    public const float DefaultDirectionToleranceDegrees = 60f;
    // 向きを刃の中心の動きで決めるのに要る最小の移動量。これより短ければ従来どおり速さの向きを使う。
    const float DirectionTravel = .5f;
    // 通り過ぎた時刻を前後の刃の値の間で割り振るのは、値の間がこれ以下のときだけ(秒)。長ければ今の値の時刻にする。
    const double MaxStepSeconds = .05;
    // 刃が動く向きからの傾きがこれ(tan 15°)以内なら細い側で振っているとみなし、ノーツに届く部分を先頭の端にする。
    const float ThinSideSlope = .268f;

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
    [Range(0f, 90f)] public float directionToleranceDegrees = DefaultDirectionToleranceDegrees;
    // iPhone カメラの値は縦が横の約1/2.4に縮んで届く(縦長480×640の映像を1920×1080へ縦横別の倍率で広げ、
    // それを判定面の±5.5×±3へ写すため。位置合わせ(4隅)を使うと変わる)。向きを比べるときだけ縦の動きをこの倍率で戻す。
    // カメラの値が届いていて曲時計があるときだけ使う。画面の刃・切り口・破片の向きは変えない。
    [Range(1f, 4f)] public float verticalDirectionScale = 2.4f;
    // 細い側で振る(刃が動く向きに沿う・カメラに向いて短く写る)と、振りを横切る刃の幅がなくなり、横の当たりは 2×1.2 だけになる。
    // そのときだけ、振りを横切る幅がこの値になるまで当たりを広げる(刃の幅が0なら半径1.7)。腹で払う刃(幅1.0以上)は今のまま。
    // 曲時計があり、カメラの値が届いているときだけ使う。当たりの2倍(2.4)以下にすると広げない。画面の刃は変えない。
    public float minSwingWidth = 3.4f;
    // 広げた当たりで入るのは、この距離の中に時刻の違う受付中のノーツ(この手で切れるもの)がないときだけ(巻き込みを増やさない)。
    public float widenGuardDistance = 2.9f;
    // 隣のマスへ順番に来るノーツを、1回の振りでなぞって続けて切れるようにする(曲時計があるときだけ)。false なら従来どおり1つの時刻だけ。
    public bool sweepEnabled = true;
    // なぞりとみなすのは、前に切ったノーツから「譜面の間隔×この割合」以上たって刃が届いたとき。
    // 0.5 は、プレイヤーのずれを差し引いて、前のノーツより自分の時刻に近いときに届いたという意味。
    // それより早く届いたのは振り抜けの勢い(巻き込み)なので切らない。ただし自分の時刻から PERFECT の幅以内に届いたら切る。
    [Range(0f, 1f)] public float sweepPaceRatio = .5f;

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
        // このノーツの当たり(入ったときの距離の上限)。細い側で振って広げたときは広げた値のまま、抜けたかもこれで見る。
        public float range;
        // 刃がいちばん近づいたときの距離と曲時計。ノーツが切れる時間内のフレームだけで更新する(時計がなければ NaN)。
        public float bestDistance;
        public double bestSongTime;
        // 通り過ぎたかと向きを測る起点(刃の中心が入った位置。振り返したらその位置へ置き直す)と、
        // 起点からいちばん近づいたところまでの中心の動き(抜けて切るときの向き)。
        public Vector2 anchor;
        public Vector2 approach;
        // 刃のうちノーツに届く部分が、ノーツの中心を通り過ぎた時刻とそのときの動きの向き(判定窓の中のときだけ。なければ NaN)。
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

    // 今の振り。止まるか向きが大きく変わるまでを1回とし、その振りで最後に切った時刻(未カットは NaN)と、
    // そのノーツに刃が届いた曲時計(なぞりの間隔を測る起点)を覚える。
    int strokeTick = -1;
    bool strokeMoving;
    Vector2 strokeDirection;
    float strokeRest;
    double strokeGroupTime = double.NaN;
    double strokeGroupReach = double.NaN;
    readonly Dictionary<CuttableNote, uint> strokeBlocked = new Dictionary<CuttableNote, uint>();
    // 次の振りに変わった(保留中のノーツの起点を置き直す)。
    bool strokeRestarted;

    // 刃の両端のひとつ前の値と今の値(カメラの新しい値で動いたときだけ進める)と、それぞれを初めて見た曲時計。点のときは両端が同じ点。
    Vector2 stepFromA, stepFromB, stepToA, stepToB;
    double stepFromTime = double.NaN, stepToTime = double.NaN;
    bool hasStepFrom, hasStepTo;
    // 刃の中心が最後に動いた向き(刃が回るだけで中心が動かないときも、この向きで通り過ぎたかを見る)。
    Vector2 lastMotion;
    bool hasLastMotion;
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

    // 本編: 曲時計を渡すと、刃のうちノーツに届く部分がノーツの中心を通り過ぎた時刻(なければいちばん近づいた時刻)で採点する。
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
        TrackStep(blade);
        return blade ? TryCutBlade() : TryCutPoint();
    }

    private int TryCutBlade()
    {
        if (saber.Speed < minCutSpeed) return CheckPending(true);

        Vector2 a = EndA, b = EndB;
        Vector2 center = (a + b) * .5f;
        float hitRange = bladeRadius + noteHitRadiusXY;
        // 細い側で振っているときに広げた当たり(広げないときは hitRange と同じ)。
        float thinRange = ThinRange(a, b, hitRange);
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
                float start = OrderEntryFraction(note);
                Vector2 allowedA = Vector2.Lerp(a0, a, start), allowedB = Vector2.Lerp(b0, b, start);
                float swept = CameraSaberHistory.SweptDistance(noteXY, allowedA, allowedB, a, b, out Vector2 sweptClosest);
                if (swept < d) { d = swept; closest = sweptClosest; anchor = (allowedA + allowedB) * .5f; }
            }
            // 広げた当たりで入るのは、近くに時刻の違う受付中のノーツがないときだけ(巻き込みを増やさない)。
            float range = d > hitRange && d <= thinRange && !Crowded(note) ? thinRange : hitRange;
            if (d <= range) pending[note] = NewPending(note, closest, d, anchor, center, range);
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
            Vector2 allowedFrom = Vector2.Lerp(from, to, OrderEntryFraction(note));
            float d = DistPointToSegment(noteXY, allowedFrom, to, out Vector2 closest);
            // 点は1フレームの軌跡で入るので、起点はその軌跡の始まり。
            if (d <= hitRange) pending[note] = NewPending(note, closest, d, allowedFrom, to, hitRange);
        }

        return CheckPending(false);
    }

    // 保護が解ける前に通った軌跡を、解放後のフレームへ持ち越さない。
    float OrderEntryFraction(CuttableNote note)
    {
        if (!Finite(songTime)) return 0f;
        double start = note.CutOrderStartTime(EffectiveHand());
        double from = hasStepFrom && Finite(stepFromTime) ? stepFromTime : songTime - saber.LastDeltaTime;
        if (start <= from) return 0f;
        if (songTime <= from) return 1f;
        return Mathf.Clamp01((float)((start - from) / (songTime - from)));
    }

    // 保留中のノーツを見て、刃が範囲から抜けたら切る。曲時計があれば、刃がノーツを通り過ぎたときにも切る。
    private int CheckPending(bool blade)
    {
        if (pending.Count == 0) return 0;
        Vector2 center = Center(blade);
        // 点のときは刃の両端を今の位置にする(ノーツに届く部分 = 今の位置)。
        Vector2 a = blade ? EndA : center;
        Vector2 b = blade ? EndB : center;

        toRemove.Clear(); tracked.Clear();
        // 先に、刃がノーツを通り過ぎた時刻を全部のノーツで覚える(どのノーツを切るか選ぶときにも使う)。
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
            if (distNow > p.range)
            {
                Vector3 exit = ExitVelocity(p, center);
                if (TakeTurn(note, p, exit, imuHint, a, b)) Finish(note, p, imuHint, exit);
                cuts++;
                toRemove.Add(note);
                continue;
            }
            p = Track(note, p, distNow, closest, center);
            if (!Passed(p, a, b, center, noteXY)) continue;
            // 通り過ぎた: 抜け切るのを待たずに切る。切れなかったら(逆向き・ロングの途中)この振りではもう切らない。
            // 構えの振りで通り過ぎても、振り返してから入り直すので、振り下ろしの時刻で採点される。
            Vector3 swing = SwingVelocity(p, center - p.anchor);
            if (TakeTurn(note, p, swing, imuHint, a, b))
            {
                Finish(note, p, imuHint, swing);
                if (!note.IsCut) Block(note);
            }
            cuts++;
            toRemove.Add(note);
        }
        Apply();
        return cuts;
    }

    Pending NewPending(CuttableNote note, Vector2 closest, float distance, Vector2 anchor, Vector2 center, float range)
    {
        return new Pending
        {
            version = note.SpawnVersion,
            hitPoint = new Vector3(closest.x, closest.y, note.transform.position.z),
            velocity = saber.Velocity,
            range = range,
            bestDistance = distance,
            // 進入は判定窓の中でしか起きない(IsCandidate)ので、進入時刻はそのまま採点に使える。
            bestSongTime = Finite(songTime) ? songTime : double.NaN,
            anchor = anchor,
            approach = center - anchor,
            crossSongTime = double.NaN,
            previousCrossSongTime = double.NaN
        };
    }

    // 刃のうちノーツに届く部分(CuttingPoint)が、動いた向きで見てノーツの中心を通り過ぎた時刻を覚える
    // (曲時計があり、判定窓の中のときだけ)。細い側で振るときは先端が届いた時刻、腹で払うときは刃がノーツの上を通った時刻。
    // 時刻は前後の刃の値の間で割り振る。同じ値の組では何度呼んでも同じ結果になる。変わったら tracked に積む。
    void TrackCrossing(CuttableNote note, Pending p)
    {
        if (!Finite(songTime) || !hasStepFrom || !hasLastMotion || !note.IsJudgeable) return;
        Vector2 noteXY = new Vector2(note.transform.position.x, note.transform.position.y);
        Vector2 m = lastMotion;
        // 動く向きで見た、届く部分からノーツまでの残り(正ならまだ手前)。
        float before = Vector2.Dot(noteXY - CuttingPoint(stepFromA, stepFromB, noteXY, m), m);
        float after = Vector2.Dot(noteXY - CuttingPoint(stepToA, stepToB, noteXY, m), m);
        if (before <= 0f || after > 0f) return;
        double span = stepToTime - stepFromTime;
        double time = Finite(span) && span >= 0 && span <= MaxStepSeconds
            ? stepFromTime + span * (before / (before - after))
            : stepToTime;
        if (!note.CanBeginCutAt(time, EffectiveHand())) return;
        if (time == p.crossSongTime && m == p.crossDirection) return;
        p.previousCrossSongTime = p.crossSongTime;
        p.previousCrossDirection = p.crossDirection;
        p.crossSongTime = time;
        p.crossDirection = m;
        tracked.Add(new KeyValuePair<CuttableNote, Pending>(note, p));
    }

    // 刃が動いたら(カメラの新しい値)、両端のひとつ前の値と今の値を進める。時刻はその値を初めて見たときの曲時計。
    void TrackStep(bool blade)
    {
        Vector2 a = blade ? EndA : Center(false);
        Vector2 b = blade ? EndB : a;
        if (hasStepTo && a == stepToA && b == stepToB) return;
        stepFromA = stepToA; stepFromB = stepToB; stepFromTime = stepToTime; hasStepFrom = hasStepTo;
        stepToA = a; stepToB = b; stepToTime = songTime; hasStepTo = true;
        Vector2 moved = (stepToA + stepToB - stepFromA - stepFromB) * .5f;
        if (hasStepFrom && moved.sqrMagnitude > 1e-8f) { lastMotion = moved.normalized; hasLastMotion = true; }
    }

    void ResetMotion()
    {
        hasStepFrom = hasStepTo = hasSweep = hasLastMotion = false;
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

    // 刃の中心が起点から passCutTravel 以上動き、起点から見てノーツが動いた向きの先にあって、
    // 刃のうちノーツに届く部分がノーツより先へ出たか(曲時計があるときだけ)。
    // 細い側で振るときは先端が越えれば切れる(刃の中心が届くまで振り切らなくてよい)。
    bool Passed(Pending p, Vector2 a, Vector2 b, Vector2 center, Vector2 noteXY)
    {
        if (!Finite(songTime)) return false;
        Vector2 travel = center - p.anchor;
        float min = Mathf.Max(passCutTravel, .0001f);
        if (travel.sqrMagnitude < min * min) return false;
        if (Vector2.Dot(noteXY - p.anchor, travel) <= 0f) return false;
        Vector2 m = travel.normalized;
        return Vector2.Dot(noteXY - CuttingPoint(a, b, noteXY, m), m) < 0f;
    }

    // 刃 a-b のうち、向き m に動いたときにノーツに届く部分。ノーツの横の位置にある刃の点(刃の外なら横で近いほうの端)を基本に、
    // 刃が動く向きに沿っている(傾きが ThinSideSlope 以内 = 細い側で振っている)ときは先頭の端にする。点の判定では a と b が同じ。
    // 横のずれから、前に出ているぶんを少し差し引いた値がいちばん小さい点を選ぶ(候補は両端と、ノーツの横の位置にある刃の点)。
    static Vector2 CuttingPoint(Vector2 a, Vector2 b, Vector2 note, Vector2 m)
    {
        Vector2 side = new Vector2(-m.y, m.x);
        float sa = Vector2.Dot(a - note, side), sb = Vector2.Dot(b - note, side);
        Vector2 best = a;
        float bestCost = Mathf.Abs(sa) - ThinSideSlope * Vector2.Dot(a, m);
        float costB = Mathf.Abs(sb) - ThinSideSlope * Vector2.Dot(b, m);
        if (costB < bestCost) { best = b; bestCost = costB; }
        if (sa * sb < 0f)
        {
            Vector2 q = a + (b - a) * (sa / (sa - sb));
            if (-ThinSideSlope * Vector2.Dot(q, m) < bestCost) best = q;
        }
        return best;
    }

    // 細い側で振っているときの当たり。振りを横切る刃の幅(動く向きに直交する広がり)が minSwingWidth に届くまで広げる。
    // 曲時計があり、カメラの値が届いているときだけ(それ以外は hitRange のまま)。
    float ThinRange(Vector2 a, Vector2 b, float hitRange)
    {
        if (!Finite(songTime) || !ReceivingCamera) return hitRange;
        Vector2 v = new Vector2(saber.Velocity.x, saber.Velocity.y);
        if (v.sqrMagnitude < 1e-8f) return hitRange;
        Vector2 m = v.normalized, blade = b - a;
        float across = Mathf.Abs(blade.x * m.y - blade.y * m.x);
        return Mathf.Max(hitRange, (minSwingWidth - across) * .5f);
    }

    // このノーツから widenGuardDistance 以内に、時刻の違う受付中のノーツ(この手で切れるもの)があるか。
    bool Crowded(CuttableNote note)
    {
        Vector2 xy = new Vector2(note.transform.position.x, note.transform.position.y);
        float guard2 = widenGuardDistance * widenGuardDistance;
        var notes = CuttableNote.ActiveNotes;
        for (int i = 0; i < notes.Count; i++)
        {
            var other = notes[i];
            // 早切り保護中の後続も混雑に数える。当たりを広げる条件まで緩めない。
            if (other == note || !IsCandidate(other, checkCutOrder: false)) continue;
            if (!SaberHandHelper.CanCut(other.RequiredHand, EffectiveHand())) continue;
            if (System.Math.Abs(other.HitTime - note.HitTime) <= SimultaneousSeconds) continue;
            Vector2 o = new Vector2(other.transform.position.x, other.transform.position.y);
            if ((o - xy).sqrMagnitude <= guard2) return true;
        }
        return false;
    }

    Vector2 EndA => new Vector2(bladeProvider.WorldEndA.x, bladeProvider.WorldEndA.y);
    Vector2 EndB => new Vector2(bladeProvider.WorldEndB.x, bladeProvider.WorldEndB.y);

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
        Vector2? direction = clocked && ReceivingCamera ? JudgedDirection(motion) : (Vector2?)null;
        bool accepted = note.CutAtSongTime(p.hitPoint, velocity, imuHint, EffectiveHand(), judged, DirectionTolerance(), direction);
        if (!accepted || !Grouped(note)) return;
        // この振りで切った時刻を進める(同時のノーツは最初の1つで決まる)。次のなぞりの間隔は、刃がこのノーツに届いた時刻から測る。
        if (double.IsNaN(strokeGroupTime) || note.HitTime - strokeGroupTime > SimultaneousSeconds)
        {
            strokeGroupTime = note.HitTime;
            strokeGroupReach = clocked ? ReachTime(p, motion) : double.NaN;
        }
    }

    bool ReceivingCamera => bladeProvider != null && bladeProvider.ReceivingCamera;

    // 向きが合うとみなす幅(dot の閾値)。曲時計があるときだけ広げる(時計がないタイトル等は従来の±30°)。
    float DirectionTolerance() => Finite(songTime) ? Mathf.Cos(directionToleranceDegrees * Mathf.Deg2Rad) : .866f;

    // カメラの値は縦が縮んで届くので、向きを比べるときだけ縦の動きを戻す(切り口や破片の向きは画面の動きのまま)。
    Vector2 JudgedDirection(Vector2 motion) => new Vector2(motion.x, motion.y * verticalDirectionScale);

    // 刃がノーツに届いた時刻: 刃のうちノーツに届く部分がノーツの中心を通り過ぎた時刻(切った向きと同じ向きに通り過ぎたもの)。
    // なければいちばん近づいた時刻。
    static double ReachTime(Pending p, Vector2 cutMotion)
    {
        if (Finite(p.crossSongTime) && Vector2.Dot(p.crossDirection, cutMotion) > 0f) return p.crossSongTime;
        if (Finite(p.previousCrossSongTime) && Vector2.Dot(p.previousCrossDirection, cutMotion) > 0f) return p.previousCrossSongTime;
        return p.bestSongTime;
    }

    // 採点の時刻は刃がノーツに届いた時刻。
    // 1回で切るノーツは判定窓の端に収める(切れたのに MISS と採点しない)。ロングは完了の割合で採点する。
    double? ScoreTime(CuttableNote note, Pending p, Vector2 cutMotion)
    {
        double time = ReachTime(p, cutMotion);
        if (!Finite(time)) return null;
        if (Grouped(note))
            time = System.Math.Max(note.HitTime - JudgmentTierHelper.EarlyBadSeconds,
                System.Math.Min(note.HitTime + JudgmentTierHelper.LateBadSeconds, time));
        return time;
    }

    // 抜けた(通り過ぎた)ノーツを今の振りで切ってよいか。
    // まだ何も切っていない振りが複数のノーツにかかっていたら、タイミングのいちばん合ったノーツを選ぶ。
    // 切ったあとは、同時のノーツと、なぞりで届いた後ろの時刻のノーツ(Sweeps)だけを切る。ほかはこの振りでは切らない。
    bool TakeTurn(CuttableNote note, Pending p, Vector3 velocity, CutDirection imuHint, Vector2 a, Vector2 b)
    {
        if (!Grouped(note)) return true;
        if (!double.IsNaN(strokeGroupTime))
            return System.Math.Abs(note.HitTime - strokeGroupTime) <= SimultaneousSeconds
                || Sweeps(note, p, velocity, imuHint) || Block(note);
        CuttableNote best = BestTimed(note, a, b);
        if (best == null || System.Math.Abs(note.HitTime - best.HitTime) <= SimultaneousSeconds) return true;
        return Block(note);
    }

    // 同じ振りの続きで、後ろの時刻のノーツをなぞりとして切ってよいか(曲時計があるときだけ)。
    // 前に切ったノーツから「譜面の間隔×sweepPaceRatio」以上たって届いたか、このノーツの時刻から PERFECT の幅以内に届いたこと。
    // 向きの決まったノーツは振りの向きが合うときだけ(なぞりの途中で、向き違いの降格カットをしない)。
    bool Sweeps(CuttableNote note, Pending p, Vector3 velocity, CutDirection imuHint)
    {
        if (!SweepActive || !Finite(strokeGroupReach)) return false;
        double gap = note.HitTime - strokeGroupTime;
        if (gap <= SimultaneousSeconds) return false;
        Vector2 motion = new Vector2(velocity.x, velocity.y);
        double reach = ReachTime(p, motion);
        if (!Finite(reach)) return false;
        bool paced = reach - strokeGroupReach >= gap * sweepPaceRatio;
        bool onTime = reach >= note.HitTime - JudgmentTierHelper.EarlyPerfectSeconds
            && reach <= note.HitTime + JudgmentTierHelper.LatePerfectSeconds;
        if (!paced && !onTime) return false;
        if (note.RequiredDirection == CutDirection.None || note.DirectionVisualOnly) return true;
        return CutDirectionHelper.MatchesWithHint(note.RequiredDirection,
            ReceivingCamera ? JudgedDirection(motion) : motion, imuHint, DirectionTolerance());
    }

    bool SweepActive => sweepEnabled && Finite(songTime);

    bool Block(CuttableNote note)
    {
        strokeBlocked[note] = note.SpawnVersion;
        return false;
    }

    // 保留中のノーツのうち、通り過ぎた(なければ近づいた)時刻がヒット時刻にいちばん近いもの(時計がなければヒット時刻が早いもの)。
    // なぞりを使うときは、decided より後ろの時刻で刃がまだ届いていないノーツは比べない(近づいている途中の時刻で比べると、
    // 遅めのなぞりで1つ目を見送ってしまう)。そのノーツは、なぞりの続きで届いたときに Sweeps で決める。
    CuttableNote BestTimed(CuttableNote decided, Vector2 a, Vector2 b)
    {
        CuttableNote best = null;
        bool bestTimed = false;
        double bestScore = double.PositiveInfinity;
        foreach (var kv in pending)
        {
            CuttableNote note = kv.Key;
            if (!Grouped(note) || !StillCuttable(note, kv.Value) || IsBlocked(note)) continue;
            if (NotReachedYet(note, kv.Value, decided, a, b)) continue;
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

    // なぞりの続きで届くかもしれないノーツ: decided より後ろの時刻で、通り過ぎた記録がなく、
    // 刃のうちノーツに届く部分より、動いている向きの先にある。
    bool NotReachedYet(CuttableNote note, Pending p, CuttableNote decided, Vector2 a, Vector2 b)
    {
        if (!SweepActive || note.HitTime - decided.HitTime <= SimultaneousSeconds || Finite(p.crossSongTime)) return false;
        Vector2 m = hasLastMotion ? lastMotion : new Vector2(saber.Velocity.x, saber.Velocity.y);
        if (m.sqrMagnitude < 1e-8f) return false;
        m.Normalize();
        Vector2 noteXY = new Vector2(note.transform.position.x, note.transform.position.y);
        return Vector2.Dot(noteXY - CuttingPoint(a, b, noteXY, m), m) > 0f;
    }

    // 今の振りでは切らないノーツ: この振りで見送ったもの、またはすでに切った時刻より前の単発ノーツ。
    // 後ろの時刻のノーツは、なぞりの続きで届けば切れるので入れておく(なぞりを使わないときは従来どおり同時のものだけ)。
    bool ExcludedFromStroke(CuttableNote note)
    {
        if (IsBlocked(note)) return true;
        if (!Grouped(note) || double.IsNaN(strokeGroupTime)) return false;
        double gap = note.HitTime - strokeGroupTime;
        if (System.Math.Abs(gap) <= SimultaneousSeconds) return false;
        return gap < 0 || !SweepActive;
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
        strokeGroupTime = strokeGroupReach = double.NaN;
        strokeBlocked.Clear();
        strokeRestarted = true;
    }

    void ResetStroke()
    {
        NextStroke();
        strokeTick = -1;
        ResetMotion();
    }

    private bool IsCandidate(CuttableNote n, bool checkCutOrder = true)
    {
        if (n == null) return false;
        if (n.IsCut || n.IsMissed) return false;
        if (!n.IsJudgeable) return false;
        if (!n.gameObject.activeInHierarchy) return false;
        if (checkCutOrder && !n.CanBeginCutAt(songTime, EffectiveHand())) return false;
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
