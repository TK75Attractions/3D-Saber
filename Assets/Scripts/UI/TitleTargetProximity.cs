using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// タイトルの既存の時計から駆動する。判定する外周は四隅の元の位置に固定する。
internal sealed class TitleTargetProximity
{
    internal static readonly Rect Outline = new Rect(-111, -100, 222, 200);
    readonly RectTransform target;
    readonly Image glow;
    readonly List<RectTransform> edges = new List<RectTransform>();
    readonly List<Vector2> positions = new List<Vector2>();
    float amount;
    Color tint;

    internal TitleTargetProximity(RectTransform target, Image glow)
    {
        this.target = target;
        this.glow = glow;
        foreach (RectTransform child in target)
            if (child.name == "TargetEdge")
            {
                edges.Add(child);
                positions.Add(child.anchoredPosition);
            }
    }

    internal void Tick(Canvas canvas, Camera camera, InputPoint input,
        SaberInputBridge[] blades, bool active, float dt)
    {
        float red = float.PositiveInfinity, blue = float.PositiveInfinity;
        double redAge = double.PositiveInfinity, blueAge = double.PositiveInfinity;
        if (active && canvas != null && camera != null && input != null)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            redAge = now - input.LastReceivedTime;
            blueAge = now - input.LastReceivedTime2;
            Camera uiCamera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            foreach (var blade in blades)
            {
                if (blade == null || !blade.isActiveAndEnabled || !blade.useInputPoint
                    || blade.UsingMouseFallback || !blade.HasBlade) continue;
                Vector3 a = camera.WorldToScreenPoint(blade.WorldEndA);
                Vector3 b = camera.WorldToScreenPoint(blade.WorldEndB);
                if (a.z <= 0 || b.z <= 0) continue;
                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(target, a, uiCamera, out var localA)
                    || !RectTransformUtility.ScreenPointToLocalPointInRectangle(target, b, uiCamera, out var localB)) continue;
                float distance = Distance(Outline, localA, localB);
                if (blade.stickIndex == 1) red = Mathf.Min(red, distance);
                if (blade.stickIndex == 2) blue = Mathf.Min(blue, distance);
            }
        }
        Apply(Choose(red, redAge, blue, blueAge), dt);
    }

    // 両色が近いときは近い方。同距離では赤を選び、毎フレーム色を交互にしない。
    internal static SaberHand Choose(float red, double redAge, float blue, double blueAge)
    {
        bool r = redAge >= 0 && redAge <= .2 && red <= 40;
        bool b = blueAge >= 0 && blueAge <= .2 && blue <= 40;
        return r && (!b || red <= blue) ? SaberHand.Right : b ? SaberHand.Left : SaberHand.Any;
    }

    internal void Apply(SaberHand hand, float dt)
    {
        bool near = hand != SaberHand.Any;
        if (near) tint = SaberHandHelper.HandColor(hand);
        amount = Mathf.MoveTowards(amount, near ? 1 : 0, Mathf.Max(0, dt) / (near ? .15f : .20f));
        for (int i = 0; i < edges.Count; i++)
        {
            Vector2 p = positions[i];
            edges[i].anchoredPosition = p - new Vector2(Mathf.Sign(p.x), Mathf.Sign(p.y)) * (8 * amount);
        }
        glow.color = new Color(tint.r, tint.g, tint.b, .18f * amount);
    }

    internal static float Distance(Rect r, Vector2 a, Vector2 b)
    {
        if (r.Contains(a) || r.Contains(b)) return 0;
        var p = new Vector2(r.xMin, r.yMin);
        var q = new Vector2(r.xMax, r.yMin);
        var s = new Vector2(r.xMax, r.yMax);
        var t = new Vector2(r.xMin, r.yMax);
        return Mathf.Min(Mathf.Min(SegmentDistance(a, b, p, q), SegmentDistance(a, b, q, s)),
            Mathf.Min(SegmentDistance(a, b, s, t), SegmentDistance(a, b, t, p)));
    }

    static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
    static float PointDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 d = b - a;
        float t = d.sqrMagnitude > 0 ? Mathf.Clamp01(Vector2.Dot(p - a, d) / d.sqrMagnitude) : 0;
        return Vector2.Distance(p, a + t * d);
    }
    static float SegmentDistance(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        float cross = Cross(b - a, d - c);
        if (Mathf.Abs(cross) > .00001f)
        {
            float t = Cross(c - a, d - c) / cross, u = Cross(c - a, b - a) / cross;
            if (t >= 0 && t <= 1 && u >= 0 && u <= 1) return 0;
        }
        return Mathf.Min(Mathf.Min(PointDistance(a, c, d), PointDistance(b, c, d)),
            Mathf.Min(PointDistance(c, a, b), PointDistance(d, a, b)));
    }
}
