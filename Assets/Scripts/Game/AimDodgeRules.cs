using System;
using System.Collections.Generic;
using UnityEngine;

// 照準で遊ぶ裏譜面(Phobia の「避」、2026-10-24 ユーザー指定。2026-10-26 から難易度の表記は「D」)の決まり。画面を持たない純粋な計算だけを置く。
// ・剣は2つの照準(刃の両端の中点)になる。トゲに当たらないようによけ、的の輪が閉じる瞬間に同じ色の照準を的の中へ入れる。
// ・点は 50000 から始まり、トゲに当たるたびに 1000 減る(0 より下げない)。的(ダーツ)は全部で最大 50000。満点 100000。
// ・的は中に入ったら HIT(満点)、入らなければ MISS だけ(2026-10-26 ユーザー「照準の精度システムを消して(つまり中にあるかないか)」。
//   それまでの BULL / INNER / OUTER はやめた)。記録の形をそろえるため HIT を PERFECT の数に入れる。
// 長さはワールド単位(ノーツと同じ。判定面は x −2.5〜2.5、y −1.5〜1.5)、時刻は秒。
public static class AimDodgeRules
{
    public const int StartScore = 50000;
    public const int DamagePenalty = 1000;
    public const int DartsTotal = 50000;

    // 的の大きさ(外の縁)。照準の芯がこの内側に入れば当たり(HIT)。
    public const float TargetRadius = .62f;

    // トゲの当たり。見た目(半径約 0.5)より小さくし、照準の芯(0.10)と足して届いたら当たり(Codex・Fable の点検の案)。
    public const float SpikeHitRadius = .34f;
    public const float CrosshairCore = .10f;
    // トゲの絵の半径(棘の先まで)。譜面の決まり: 的の直前1秒・直後0.6秒には、トゲの絵を的の絵に重ねない。
    public const float SpikeVisualRadius = .52f;
    // 当たりの外のこの幅を通ると「かすり」(火花と小さな音。点は動かない)。
    public const float GrazeExtra = .35f;

    // 判定の窓(判定の時計=カメラの遅れを引いた時刻)。的は輪が閉じる前後 0.1 秒でいちばん真ん中に近かった所、
    // トゲは届いた前後 0.12 秒だけ当たる(奥で重なって見えても当たりにしない)。
    public const double DartWindow = .10;
    public const double SpikeWindow = .12;
    // 当たったあと、その手はこの秒数だけ当たらない(1回の接触で続けて減らない)。
    public const double InvulnerableSeconds = .6;
    // 的とトゲが奥から見えはじめてから届くまでの秒(ノーツの 1.0 秒より長く、よける向きを決める間をとる)。
    public const double LeadSeconds = 1.4;
    // 追尾トゲの置き場所の範囲(腕の届く所。足を動かさせない)。
    public static readonly Vector2 FollowBounds = new Vector2(2.0f, 1.2f);


    public static float ContactDistance(float size) => SpikeHitRadius * Mathf.Max(.1f, size) + CrosshairCore;

    // 的の中心からの距離 → 中なら HIT(Perfect)、外なら MISS。NaN・無限大(一度も照準が届かなかった)は MISS。
    public static JudgmentTier Classify(float distance)
    {
        if (float.IsNaN(distance) || float.IsInfinity(distance) || distance > TargetRadius) return JudgmentTier.Miss;
        return JudgmentTier.Perfect;
    }

    // 的の段の呼び名(HUD とリザルト)。
    public static string DartLabel(JudgmentTier tier)
    {
        switch (tier)
        {
            case JudgmentTier.Perfect: return "HIT";
            default: return "MISS";
        }
    }

    // 的ごとの満点。全部で DartsTotal になるよう、割り切れない分は前の的から 1 点ずつ足す(50 個なら全部 1000)。
    public static int[] DartMaxPoints(int count)
    {
        if (count <= 0) return new int[0];
        var points = new int[count];
        int each = DartsTotal / count, rest = DartsTotal - each * count;
        for (int i = 0; i < count; i++) points[i] = each + (i < rest ? 1 : 0);
        return points;
    }

    public static int DartPoints(JudgmentTier tier, int maxPoints)
    {
        switch (tier)
        {
            case JudgmentTier.Perfect: return maxPoints;
            default: return 0;
        }
    }

    // 点 = (50000 − トゲの数×1000、0 より下げない) + 的の点。
    public static int Score(int damageCount, int dartPoints) =>
        Mathf.Max(0, StartScore - Mathf.Max(0, damageCount) * DamagePenalty) + Mathf.Max(0, dartPoints);

    // 的の精度(リザルトの ACCURACY・HI-SCORE の記録)。取った的の点 ÷ 的の満点。
    public static float DartAccuracy(int dartPoints, int dartTotal) =>
        dartTotal > 0 ? Mathf.Clamp01(dartPoints / (float)dartTotal) : 0f;

    public static SaberHand HandOf(string color) => SaberHandHelper.FromColor(color) == SaberHand.Left ? SaberHand.Left : SaberHand.Right;

    public static SaberHand FollowOf(string follow)
    {
        if (string.IsNullOrEmpty(follow)) return SaberHand.Any;
        var hand = SaberHandHelper.FromColor(follow);
        return hand == SaberHand.Left || hand == SaberHand.Right ? hand : SaberHand.Any;
    }

    public static Vector2 ClampFollow(Vector2 p) =>
        new Vector2(Mathf.Clamp(p.x, -FollowBounds.x, FollowBounds.x), Mathf.Clamp(p.y, -FollowBounds.y, FollowBounds.y));
}

// 照準1つの今の状態。Valid=false は、その手の座標が届いていない(当たりも的も数えない)。
public struct AimHandSample
{
    public bool Valid;
    public Vector2 Position;
    public AimHandSample(bool valid, Vector2 position) { Valid = valid; Position = position; }
}

public sealed class AimTarget
{
    public int Index;
    public double Time;       // 判定面で輪が閉じる曲の秒(オフセット込み)
    public Vector2 Position;
    public SaberHand Hand;    // Right=赤の照準、Left=青の照準
    public int MaxPoints;
    public float Best = float.PositiveInfinity;
    public bool Sampled;      // 窓の中で一度でも照準を見たか
    public bool Done;
    public JudgmentTier Tier = JudgmentTier.Miss;
    public int Points;
}

public sealed class AimSpike
{
    public int Index;
    public double Time;       // 判定面へ届く曲の秒(オフセット込み)
    public Vector2 Position;
    public float Size = 1f;
    public SaberHand Follow = SaberHand.Any;
    public bool Placed;       // 追尾トゲの置き場所を決めたか(追尾でないトゲは最初から true)
    public bool Hit, Done;
    public SaberHand HitHand = SaberHand.Any;
    public readonly bool[] Grazed = new bool[2];
}

public enum AimEventKind { Dart, Damage, Graze, Avoided }

public struct AimEvent
{
    public AimEventKind Kind;
    public AimTarget Target;
    public AimSpike Spike;
    public SaberHand Hand;
    public Vector2 Position;   // 火花・当たりの場所
}

// 的とトゲの進み具合。判定の時計と2つの照準を受け取り、起きたことを events に足す。
// 画面(AimDodgeMode)と採点(ScoreManager)は events を見て動く。テストから直接回せる。
public sealed class AimDodgeSession
{
    public readonly List<AimTarget> Targets = new List<AimTarget>();
    public readonly List<AimSpike> Spikes = new List<AimSpike>();
    readonly double[] invulnerableUntil = { double.NegativeInfinity, double.NegativeInfinity };
    int targetCursor, spikeCursor;

    public int DamageCount { get; private set; }
    public int AvoidedCount { get; private set; }
    public int GrazeCount { get; private set; }
    public int DartPoints { get; private set; }
    public int Combo { get; private set; }
    public int MaxCombo { get; private set; }
    public readonly int[] TierCounts = new int[4]; // BULL, INNER, OUTER, MISS
    public int Score => AimDodgeRules.Score(DamageCount, DartPoints);
    public bool Completed => targetCursor >= Targets.Count && spikeCursor >= Spikes.Count;

    // chart の時刻(ミリ秒)に offsetSeconds を足して並べる。endSeconds より後に届くものは捨てる(音源の外)。
    public AimDodgeSession(AimChartData chart, double offsetSeconds, double endSeconds = double.PositiveInfinity)
    {
        var targets = new List<AimTargetData>();
        if (chart?.targets != null)
            foreach (var t in chart.targets) if (t != null && t.time / 1000.0 + offsetSeconds <= endSeconds) targets.Add(t);
        int[] max = AimDodgeRules.DartMaxPoints(targets.Count);
        for (int i = 0; i < targets.Count; i++)
            Targets.Add(new AimTarget
            {
                Index = i, Time = targets[i].time / 1000.0 + offsetSeconds, Position = new Vector2(targets[i].x, targets[i].y),
                Hand = AimDodgeRules.HandOf(targets[i].color), MaxPoints = max[i],
            });
        Targets.Sort((a, b) => a.Time.CompareTo(b.Time));
        if (chart?.spikes != null)
            foreach (var s in chart.spikes)
            {
                if (s == null || s.time / 1000.0 + offsetSeconds > endSeconds) continue;
                var follow = AimDodgeRules.FollowOf(s.follow);
                Spikes.Add(new AimSpike
                {
                    Index = Spikes.Count, Time = s.time / 1000.0 + offsetSeconds, Position = new Vector2(s.x, s.y),
                    Size = Mathf.Max(.1f, s.size), Follow = follow, Placed = follow == SaberHand.Any,
                });
            }
        Spikes.Sort((a, b) => a.Time.CompareTo(b.Time));
    }

    public double FirstTime
    {
        get
        {
            double first = double.PositiveInfinity;
            if (Targets.Count > 0) first = Math.Min(first, Targets[0].Time);
            if (Spikes.Count > 0) first = Math.Min(first, Spikes[0].Time);
            return first;
        }
    }

    public double LastTime
    {
        get
        {
            double last = double.NegativeInfinity;
            if (Targets.Count > 0) last = Math.Max(last, Targets[Targets.Count - 1].Time + AimDodgeRules.DartWindow);
            if (Spikes.Count > 0) last = Math.Max(last, Spikes[Spikes.Count - 1].Time + AimDodgeRules.SpikeWindow);
            return last;
        }
    }

    // 的・トゲが、songTime から withinSeconds 以内に見えはじめるか、いま見えているか(剣を見失ったときの一時停止用)。
    // 手を指定したとき(片方の色だけ届かないときの知らせ)は、その色の的だけを数える。トゲはどちらの手にも当たるので Any のときだけ。
    public bool Needs(double songTime, SaberHand hand, double withinSeconds = 1.0)
    {
        double from = songTime, to = songTime + AimDodgeRules.LeadSeconds + withinSeconds;
        for (int i = targetCursor; i < Targets.Count; i++)
        {
            var t = Targets[i];
            if (t.Time > to) break;
            if (!t.Done && t.Time + AimDodgeRules.DartWindow >= from && (hand == SaberHand.Any || t.Hand == hand)) return true;
        }
        if (hand != SaberHand.Any) return false;
        for (int i = spikeCursor; i < Spikes.Count; i++)
        {
            var s = Spikes[i];
            if (s.Time > to) break;
            if (!s.Done && s.Time + AimDodgeRules.SpikeWindow >= from) return true;
        }
        return false;
    }

    // 判定の時計 judgeTime で、赤(右手)と青(左手)の照準を見て進める。
    public void Tick(double judgeTime, AimHandSample red, AimHandSample blue, List<AimEvent> events)
    {
        if (double.IsNaN(judgeTime) || double.IsInfinity(judgeTime)) return;
        TickTargets(judgeTime, red, blue, events);
        TickSpikes(judgeTime, red, blue, events);
    }

    void TickTargets(double now, AimHandSample red, AimHandSample blue, List<AimEvent> events)
    {
        while (targetCursor < Targets.Count && Targets[targetCursor].Done) targetCursor++;
        for (int i = targetCursor; i < Targets.Count; i++)
        {
            var target = Targets[i];
            if (target.Time - AimDodgeRules.DartWindow > now) break;
            if (target.Done) continue;
            var sample = target.Hand == SaberHand.Left ? blue : red;
            if (sample.Valid && now <= target.Time + AimDodgeRules.DartWindow)
            {
                target.Best = Mathf.Min(target.Best, Vector2.Distance(sample.Position, target.Position));
                target.Sampled = true;
            }
            // 中に入った瞬間に HIT と決める(輪が閉じるのを待たずに知らせる)。
            if (target.Best <= AimDodgeRules.TargetRadius || now > target.Time + AimDodgeRules.DartWindow)
            {
                // 処理落ちで窓(0.2 秒)をまるごと飛び越えたときは、窓を過ぎた直後の照準の位置で決める
                // (撮影や読み込みで1コマが長くなっただけで MISS にしない)。
                if (!target.Sampled && sample.Valid)
                    target.Best = Vector2.Distance(sample.Position, target.Position);
                Finish(target, events);
            }
        }
    }

    void Finish(AimTarget target, List<AimEvent> events)
    {
        target.Done = true;
        target.Tier = AimDodgeRules.Classify(target.Best);
        target.Points = AimDodgeRules.DartPoints(target.Tier, target.MaxPoints);
        DartPoints += target.Points;
        TierCounts[TierIndex(target.Tier)]++;
        if (target.Tier == JudgmentTier.Miss) Combo = 0;
        else { Combo++; MaxCombo = Mathf.Max(MaxCombo, Combo); }
        events?.Add(new AimEvent { Kind = AimEventKind.Dart, Target = target, Hand = target.Hand, Position = target.Position });
    }

    public static int TierIndex(JudgmentTier tier) =>
        tier == JudgmentTier.Perfect ? 0 : tier == JudgmentTier.Great ? 1 : tier == JudgmentTier.Good ? 2 : 3;

    void TickSpikes(double now, AimHandSample red, AimHandSample blue, List<AimEvent> events)
    {
        while (spikeCursor < Spikes.Count && Spikes[spikeCursor].Done) spikeCursor++;
        for (int i = spikeCursor; i < Spikes.Count; i++)
        {
            var spike = Spikes[i];
            if (spike.Time - AimDodgeRules.SpikeWindow > now) break;
            if (spike.Done) continue;
            if (now <= spike.Time + AimDodgeRules.SpikeWindow && !spike.Hit)
            {
                Touch(spike, SaberHand.Right, red, now, events);
                if (!spike.Hit) Touch(spike, SaberHand.Left, blue, now, events);
            }
            if (now > spike.Time + AimDodgeRules.SpikeWindow)
            {
                spike.Done = true;
                if (!spike.Hit)
                {
                    AvoidedCount++;
                    events?.Add(new AimEvent { Kind = AimEventKind.Avoided, Spike = spike, Position = spike.Position });
                }
            }
        }
    }

    void Touch(AimSpike spike, SaberHand hand, AimHandSample sample, double now, List<AimEvent> events)
    {
        if (!sample.Valid) return;
        int h = hand == SaberHand.Left ? 1 : 0;
        float distance = Vector2.Distance(sample.Position, spike.Position);
        float contact = AimDodgeRules.ContactDistance(spike.Size);
        if (distance <= contact && now >= invulnerableUntil[h])
        {
            spike.Hit = true; spike.HitHand = hand;
            DamageCount++; Combo = 0;
            invulnerableUntil[h] = now + AimDodgeRules.InvulnerableSeconds;
            events?.Add(new AimEvent { Kind = AimEventKind.Damage, Spike = spike, Hand = hand, Position = sample.Position });
        }
        else if (distance <= contact + AimDodgeRules.GrazeExtra && !spike.Grazed[h] && distance > contact)
        {
            spike.Grazed[h] = true; GrazeCount++;
            Vector2 toward = distance > 1e-4f ? (sample.Position - spike.Position) / distance : Vector2.up;
            events?.Add(new AimEvent { Kind = AimEventKind.Graze, Spike = spike, Hand = hand, Position = spike.Position + toward * contact });
        }
    }

    // 追尾トゲの置き場所を、出てくる瞬間の照準の位置に決める。その手が見えなければ譜面の位置のまま。
    public void PlaceFollowers(double songTime, AimHandSample red, AimHandSample blue)
    {
        for (int i = spikeCursor; i < Spikes.Count; i++)
        {
            var spike = Spikes[i];
            if (spike.Time - AimDodgeRules.LeadSeconds > songTime) break;
            if (spike.Placed) continue;
            spike.Placed = true;
            var sample = spike.Follow == SaberHand.Left ? blue : red;
            if (sample.Valid) spike.Position = AimDodgeRules.ClampFollow(sample.Position);
        }
    }
}
