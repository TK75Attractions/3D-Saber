using System;
using System.Collections.Generic;
using UnityEngine;

// 闘牛の裏譜面(アンダルシアの「T」、2026-10-26 ユーザー「ただ突進だけじゃなくてシンプル弾幕もちょっと飛んできて、
// 食らうとその棒が2秒スタンしてしまう感じで」)の決まり。画面は BullfightMode。
// ・突進: 高さ y の道を、判定面の端から端まで time〜endTime に横切る丸(半径 ChargeRadius)。
// ・弾: ノーツと同じく奥から飛んできて、届く前後 ShotWindow 秒だけ判定面で当たる丸(半径 ShotRadius)。1つの弾は1回だけ当たる。
// ・刃(両端を結ぶ線分)が丸に触れたら、その剣は StunSeconds 秒しびれて切れない。しびれが解けてから GraceSeconds 秒は当たらない。
// 当たりは判定の時計(カメラの遅れを引いた時刻)で見る。点は引かない(しびれている間に来たノーツが切れないことが罰)。
public static class BullRules
{
    public const double StunSeconds = 2.0, GraceSeconds = .4;
    public const float ChargeRadius = .4f, ShotRadius = .2f, BladeRadius = .05f;
    public const double ShotWindow = .1;
    // 突進は画面の外(±ChargeSpan)から出て、反対の外へ抜ける。
    public const float ChargeSpan = 3.4f;

    // 突進の今の中心。始まる前と終わったあとは端の外。
    public static Vector2 ChargePosition(BullCharge charge, double time)
    {
        double k = charge.End > charge.Start ? (time - charge.Start) / (charge.End - charge.Start) : 1;
        k = Math.Max(0, Math.Min(1, k));
        float x = (float)(-charge.Dir * ChargeSpan + charge.Dir * 2 * ChargeSpan * k);
        return new Vector2(x, charge.Y);
    }

    public static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float len = ab.sqrMagnitude;
        float t = len > 1e-8f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / len) : 0f;
        return Vector2.Distance(p, a + ab * t);
    }

    public static bool Touches(Vector2 center, float radius, BladeSample blade)
        => blade.Valid && DistanceToSegment(center, blade.A, blade.B) <= radius + BladeRadius;

    // 追う弾の向かう先(画面の中に収める)。
    public static Vector2 ClampFollow(Vector2 p) => new Vector2(Mathf.Clamp(p.x, -2.2f, 2.2f), Mathf.Clamp(p.y, -1.3f, 1.3f));
}

// 刃の両端(判定面の座標)。見失い中は Valid = false(当たりにしない)。
public struct BladeSample
{
    public bool Valid;
    public Vector2 A, B;
    public BladeSample(Vector2 a, Vector2 b) { Valid = true; A = a; B = b; }
    public Vector2 Middle => (A + B) * .5f;
}

public sealed class BullCharge
{
    public double WarnFrom, Start, End;
    public float Y;
    public int Dir;
}

public sealed class BullShot
{
    public double Time;
    public Vector2 Position;
    public SaberHand Follow;
    public bool Placed, Spent;
}

public enum BullEventKind { Stun, Recover }

public struct BullEvent
{
    public BullEventKind Kind;
    public SaberHand Hand;
    public Vector2 Position;
    public double Time;
}

// 突進と弾の時刻表と、手ごとのしびれ。曲の時刻(秒、ノーツと同じずらし込み)で進める。
public sealed class BullSession
{
    public readonly List<BullCharge> Charges = new List<BullCharge>();
    public readonly List<BullShot> Shots = new List<BullShot>();
    readonly double[] stunnedUntil = { double.NegativeInfinity, double.NegativeInfinity };
    readonly bool[] recovered = { true, true };
    public int StunCount { get; private set; }

    public BullSession(BullChartData chart, double offsetSeconds)
    {
        if (chart == null) return;
        foreach (var c in chart.charges)
            Charges.Add(new BullCharge
            {
                Start = c.time / 1000.0 + offsetSeconds, End = c.endTime / 1000.0 + offsetSeconds,
                WarnFrom = (c.time - c.warn) / 1000.0 + offsetSeconds, Y = c.y, Dir = c.dir < 0 ? -1 : 1
            });
        foreach (var s in chart.shots)
            Shots.Add(new BullShot
            {
                Time = s.time / 1000.0 + offsetSeconds, Position = new Vector2(s.x, s.y),
                Follow = AimDodgeRules.FollowOf(s.follow)
            });
    }

    static int Index(SaberHand hand) => hand == SaberHand.Left ? 1 : 0;

    public bool IsStunned(SaberHand hand, double time) => time < stunnedUntil[Index(hand)];

    // しびれの残り(0〜StunSeconds)。しびれていなければ 0。
    public double StunRemaining(SaberHand hand, double time) => Math.Max(0, stunnedUntil[Index(hand)] - time);

    // 見えはじめた追う弾の行き先を、その瞬間の剣の真ん中に決める(手が見えなければ譜面の位置のまま)。
    public void PlaceFollowers(double songTime, double approach, BladeSample red, BladeSample blue)
    {
        foreach (var shot in Shots)
        {
            if (shot.Placed || shot.Time - approach > songTime) continue;
            shot.Placed = true;
            if (shot.Follow == SaberHand.Any) continue;
            var blade = shot.Follow == SaberHand.Left ? blue : red;
            if (blade.Valid) shot.Position = BullRules.ClampFollow(blade.Middle);
        }
    }

    // 判定の時計 judgeTime で、刃が突進・弾に触れたか見る。しびれた・解けたは events に足す。
    public void Tick(double judgeTime, BladeSample red, BladeSample blue, List<BullEvent> events)
    {
        if (double.IsNaN(judgeTime) || double.IsInfinity(judgeTime)) return;
        for (int i = 0; i < 2; i++)
        {
            if (recovered[i] || judgeTime < stunnedUntil[i]) continue;
            recovered[i] = true;
            events?.Add(new BullEvent { Kind = BullEventKind.Recover, Hand = i == 1 ? SaberHand.Left : SaberHand.Right, Time = stunnedUntil[i] });
        }
        foreach (var charge in Charges)
        {
            if (judgeTime < charge.Start || judgeTime > charge.End) continue;
            Vector2 center = BullRules.ChargePosition(charge, judgeTime);
            Check(center, BullRules.ChargeRadius, judgeTime, red, blue, events);
        }
        foreach (var shot in Shots)
        {
            if (shot.Spent || Math.Abs(judgeTime - shot.Time) > BullRules.ShotWindow) continue;
            if (Check(shot.Position, BullRules.ShotRadius, judgeTime, red, blue, events)) shot.Spent = true;
        }
    }

    bool Check(Vector2 center, float radius, double time, BladeSample red, BladeSample blue, List<BullEvent> events)
    {
        bool hit = false;
        for (int i = 0; i < 2; i++)
        {
            var blade = i == 1 ? blue : red;
            if (!BullRules.Touches(center, radius, blade)) continue;
            hit = true;
            if (time < stunnedUntil[i] + BullRules.GraceSeconds) continue;
            stunnedUntil[i] = time + BullRules.StunSeconds;
            recovered[i] = false;
            StunCount++;
            events?.Add(new BullEvent { Kind = BullEventKind.Stun, Hand = i == 1 ? SaberHand.Left : SaberHand.Right, Position = center, Time = time });
        }
        return hit;
    }

    // 最後の突進・弾の時刻(曲の終わりの確認用)。
    public double LastTime
    {
        get
        {
            double last = double.NegativeInfinity;
            foreach (var c in Charges) last = Math.Max(last, c.End);
            foreach (var s in Shots) last = Math.Max(last, s.Time);
            return last;
        }
    }
}
