using System;
using System.Collections.Generic;
using UnityEngine;

// 闘牛の裏譜面(アンダルシアの「T」、2026-10-26 ユーザー「ただ突進だけじゃなくてシンプル弾幕もちょっと飛んできて、
// 食らうとその棒が2秒スタンしてしまう感じで」)の決まり。画面は BullfightMode。
// ・突進: 高さ y の道を、判定面の端から端まで time〜endTime に横切る丸(半径 ChargeRadius)。
// ・弾: 奥から飛び、判定面で当たる。ふつうは前後 ShotWindow 秒、地雷は滞在中、爆弾は着地後の広がり中。1つの弾は1回だけ当たる。
// ・刃(両端を結ぶ線分)が丸に触れたら、その剣は StunSeconds 秒しびれて切れない。しびれが解けてから GraceSeconds 秒は当たらない。
// 当たりは判定の時計(カメラの遅れを引いた時刻)で見る。点は引かない(しびれている間に来たノーツが切れないことが罰)。
public static class BullRules
{
    public const double StunSeconds = 2.0, GraceSeconds = .4;
    public const float ChargeRadius = .4f, ShotRadius = .2f, BladeRadius = .05f;
    public const double ShotWindow = .1, CurveSettle = .25;
    public const double BombExpand = .3, WavePeriod = .4;
    public const float BigRadius = .45f, SpearHalf = .55f, SpearRadius = .06f;
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

    static double Cross(Vector2 a, Vector2 b) => (double)a.x * b.y - (double)a.y * b.x;

    // 交差は0。平行・重なり・長さ0の線分も、端から相手までの距離で扱う。
    public static float SegmentDistance(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        Vector2 ab = b - a, cd = d - c;
        double den = Cross(ab, cd);
        if (Math.Abs(den) > 1e-12)
        {
            double t = Cross(c - a, cd) / den, u = Cross(c - a, ab) / den;
            if (t >= 0 && t <= 1 && u >= 0 && u <= 1) return 0;
        }
        return Mathf.Min(Mathf.Min(DistanceToSegment(a, c, d), DistanceToSegment(b, c, d)),
            Mathf.Min(DistanceToSegment(c, a, b), DistanceToSegment(d, a, b)));
    }

    public static string ShotKind(string value)
    {
        string kind = value == null ? "" : value.Trim().ToLowerInvariant();
        return kind == "big" || kind == "spear" || kind == "split"
            || kind == "mine" || kind == "bomb" || kind == "wave" ? kind : "";
    }

    public static float Radius(BullShot shot) => shot.Kind == "big" ? BigRadius : ShotRadius;

    // 判定時間を描画・終了時刻・テストでも共有する。爆弾は着地前には当たらない。
    public static double HitStart(BullShot shot) => shot.Time - (shot.Kind == "bomb" ? 0 : ShotWindow);
    public static double HitEnd(BullShot shot) => shot.Time
        + (shot.Kind == "mine" ? shot.Stay : shot.Kind == "bomb" ? BombExpand : ShotWindow);
    public static bool Active(BullShot shot, double judgeTime)
        => !shot.Spent && judgeTime >= HitStart(shot) && judgeTime <= HitEnd(shot);
    public static float Radius(BullShot shot, double judgeTime) => shot.Kind == "bomb"
        ? Mathf.Lerp(ShotRadius, shot.Blast, Mathf.Clamp01((float)((judgeTime - shot.Time) / BombExpand)))
        : Radius(shot);

    public static void SpearEnds(BullShot shot, out Vector2 a, out Vector2 b)
    {
        float angle = (shot.Angle % 360f) * Mathf.Deg2Rad;
        Vector2 half = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * SpearHalf;
        a = shot.Position - half;
        b = shot.Position + half;
    }

    public static bool Touches(BullShot shot, BladeSample blade) => Touches(shot, blade, shot.Time);

    public static bool Touches(BullShot shot, BladeSample blade, double judgeTime)
    {
        if (!blade.Valid || !Active(shot, judgeTime)) return false;
        if (shot.Kind != "spear") return Touches(shot.Position, Radius(shot, judgeTime), blade);
        SpearEnds(shot, out var a, out var b);
        return SegmentDistance(a, b, blade.A, blade.B) <= SpearRadius + BladeRadius;
    }

    // うねりは lead 省略時も1秒間動く。他の弾の0は従来の直進。
    public static double CurveLead(BullShot shot) => shot.Lead > 0
        ? Math.Max(CurveSettle, shot.Lead) : shot.Kind == "wave" ? 1 : 0;

    public static Vector2 ShotPosition(BullShot shot, double songTime)
    {
        double lead = CurveLead(shot);
        if (lead <= 0) return shot.Position;
        double remaining = shot.Time - songTime;
        if (remaining <= CurveSettle) return shot.Position;
        Vector2 from = shot.Lead > 0 ? shot.From : shot.Position;
        if (remaining >= lead) return from;
        float k = (float)((lead - remaining) / (lead - CurveSettle));
        Vector2 p = Vector2.Lerp(from, shot.Position, 1f - (1f - k) * (1f - k));
        if (shot.Kind == "wave")
        {
            // 両端で振幅と速度を0へ戻す。angle=0で左右、90で上下。周期は0.4秒。
            float envelope = Mathf.Sin(Mathf.PI * k);
            float sway = shot.Amp * envelope * envelope
                * Mathf.Sin((float)((lead - remaining) / WavePeriod * Math.PI * 2));
            float angle = (shot.Angle % 360f) * Mathf.Deg2Rad;
            p += new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * sway;
        }
        return p;
    }

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
    public double Time, Lead;
    public double Stay = .7;
    public float Blast = .9f, Amp = .6f;
    public Vector2 Position, From;
    public string Kind;
    public float Angle;
    public SaberHand Follow;
    public bool Placed, Spent;
    public BullSplitGroup Split;
}

// 同じ時刻・出発点の子を束ねる。見た目の代表と割れる時刻を共有する。
public sealed class BullSplitGroup
{
    public BullShot Leader;
    public double Lead;
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
        var groups = new Dictionary<(double, float, float), BullSplitGroup>();
        foreach (var s in chart.shots)
        {
            var shot = new BullShot
            {
                Time = s.time / 1000.0 + offsetSeconds, Position = new Vector2(s.x, s.y),
                From = new Vector2(s.fromX, s.fromY), Lead = s.lead / 1000.0,
                Kind = BullRules.ShotKind(s.kind), Angle = s.angle,
                Stay = s.stay / 1000.0, Blast = s.blast, Amp = s.amp
            };
            shot.Follow = shot.Kind == "" && shot.Lead <= 0 ? AimDodgeRules.FollowOf(s.follow) : SaberHand.Any;
            if (shot.Kind == "split")
            {
                var key = (shot.Time, shot.From.x, shot.From.y);
                if (!groups.TryGetValue(key, out var group))
                {
                    group = new BullSplitGroup { Leader = shot };
                    groups.Add(key, group);
                }
                group.Lead = Math.Max(group.Lead, shot.Lead);
                shot.Split = group;
            }
            Shots.Add(shot);
        }
        // 同じ組に違う lead が書かれていても、全員が一緒に割れる。
        foreach (var shot in Shots)
            if (shot.Split != null) shot.Lead = shot.Split.Lead;
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
            if (!BullRules.Active(shot, judgeTime)) continue;
            // 地雷も含め、最初に刃が触れたTickで消費する。しびれ中の刃で触れても再判定しない。
            if (Check(shot.Position, BullRules.Radius(shot, judgeTime), judgeTime, red, blue, events, shot))
                shot.Spent = true;
        }
    }

    bool Check(Vector2 center, float radius, double time, BladeSample red, BladeSample blue, List<BullEvent> events, BullShot shot = null)
    {
        bool hit = false;
        for (int i = 0; i < 2; i++)
        {
            var blade = i == 1 ? blue : red;
            if (!(shot != null ? BullRules.Touches(shot, blade, time) : BullRules.Touches(center, radius, blade))) continue;
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
            foreach (var s in Shots) last = Math.Max(last, BullRules.HitEnd(s));
            return last;
        }
    }
}
