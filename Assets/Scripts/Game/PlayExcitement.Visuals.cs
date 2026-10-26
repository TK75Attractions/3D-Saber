using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public sealed partial class PlayExcitement
{
    struct RingPulse { public float age; public Color color; }
    struct Shard { public Vector2 origin, velocity; public float age, angle, spin; public Color color; }
    struct ScreenPulse { public LargeFlashKind kind; public Color color; public float age, seconds, alpha; }

    readonly List<RingPulse> rings = new List<RingPulse>();
    readonly List<Shard> shards = new List<Shard>();
    readonly List<ScreenPulse> flashes = new List<ScreenPulse>();
    readonly List<FlashRect> flashRects = new List<FlashRect>();

    JudgeGateFrame frame;
    Camera view;
    PlayExcitementMesh gauge, accents, lasers, largeLight;
    VoltageBackgroundPresentation background;
    Canvas tagCanvas;
    TextMeshProUGUI tag;
    float tagLeft;

    void BuildVisuals(FloorRenderer stage)
    {
        view = Camera.main;
        var gateObject = GameObject.Find("JudgeGate");
        frame = gateObject != null ? gateObject.GetComponent<JudgeGateFrame>() : null;
        // 背景の材質の描画順と色を書き換える案は、半透明の背景の重なりを壊す危険があるため使わない(明るさの段は保留)。
        background = null;

        if (frame != null)
        {
            gauge = new PlayExcitementMesh(frame.transform, "VoltageGauge", 3011, 0);
            accents = new PlayExcitementMesh(frame.transform, "VoltageAccents", 3011, 0);
        }
        if (view != null && frame != null)
            // 背景の描画順は変えず、いちばん最後に重ねる。ノーツはゲートの四角の内側を通るので、内側(+24px)を抜けば隠れない。
            largeLight = new PlayExcitementMesh(view.transform, "LargeFlashLayer", 4000, 100, true);
        if (stage != null && fever != null)
            lasers = new PlayExcitementMesh(fever.transform, "VoltageLasers", 3000, -190);

        if (Meter.Level == 1 && frame != null && view != null)
        {
            var go = new GameObject("VoltageContinuation", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            go.transform.SetParent(transform, false);
            tagCanvas = go.GetComponent<Canvas>();
            tagCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            tagCanvas.sortingOrder = 40;
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = .5f;
            tag = UISkinKit.MakeTMP(go.transform, "Label", "つづきから", 24, Color.white,
                TextAlignmentOptions.Center, Vector2.zero, new Vector2(180, 34));
            tag.rectTransform.anchorMin = tag.rectTransform.anchorMax = Vector2.zero;
            tag.rectTransform.pivot = new Vector2(.5f, .5f);
            tagLeft = 2;
            tagCanvas.enabled = false;
        }
    }

    public static Color LevelColor(int level, double time)
    {
        switch (level)
        {
            case 1: return new Color(.2f, 1f, 1f);
            case 2: return new Color(1f, .95f, .2f);
            case 3: return new Color(1f, .72f, .18f);
            case 4: return Color.HSVToRGB(Mathf.Repeat((float)(time % 4) / 4f, 1), .8f, 1);
            default: return Color.white;
        }
    }

    static Color FlashColor(LargeFlashTint tint)
        => tint == LargeFlashTint.Gold ? new Color(1, .78f, .25f)
            : tint == LargeFlashTint.Cyan ? new Color(.25f, 1, 1) : Color.white;

    bool TryGateRect(out FlashRect rect)
    {
        rect = default;
        if (frame == null || view == null || frame.HalfWidth <= 0 || frame.HalfHeight <= 0) return false;
        double left = double.PositiveInfinity, bottom = double.PositiveInfinity;
        double right = double.NegativeInfinity, top = double.NegativeInfinity;
        for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
            {
                Vector3 p = view.WorldToViewportPoint(frame.transform.TransformPoint(
                    new Vector3(x * frame.HalfWidth, y * frame.HalfHeight, 0)));
                if (!Finite(p.x) || !Finite(p.y) || p.z <= view.nearClipPlane) return false;
                left = System.Math.Min(left, p.x); right = System.Math.Max(right, p.x);
                bottom = System.Math.Min(bottom, p.y); top = System.Math.Max(top, p.y);
            }
        rect = new FlashRect(left, bottom, right, top);
        return true;
    }

    void DrawVisuals(float dt, double songTime, bool advancing)
    {
        bool low = DisplaySettings.ReducedEffects;
        background?.Apply(low ? 0 : Meter.Level);
        if (low) ClearTransients();
        DrawGauge();

        for (int i = rings.Count - 1; i >= 0; i--)
        {
            var r = rings[i]; r.age += dt;
            if (r.age >= .7f) rings.RemoveAt(i); else rings[i] = r;
        }
        for (int i = shards.Count - 1; i >= 0; i--)
        {
            var s = shards[i]; s.age += dt;
            if (s.age >= .3f) shards.RemoveAt(i); else shards[i] = s;
        }
        for (int i = flashes.Count - 1; i >= 0; i--)
        {
            var f = flashes[i]; f.age += dt;
            if (f.age >= f.seconds) flashes.RemoveAt(i); else flashes[i] = f;
        }

        if (TryGateRect(out var rawGate))
        {
            var protectedGate = LargeFlashLayout.ProtectedGate(rawGate);
            DrawAccents(protectedGate);
            if (lasers != null)
            {
                lasers.Clear();
                lasers.Protect(protectedGate);
                if (!low && advancing && fever != null)
                    fever.DrawVoltageLasers(lasers, songTime, LevelColor(Meter.Level, visualTime));
                lasers.Flush();
            }
            DrawScreenFlashes();
            DrawTag(rawGate, dt, advancing);
        }
        else
        {
            accents?.Hide(); lasers?.Hide(); largeLight?.Hide();
            if (tagCanvas != null) tagCanvas.enabled = false;
        }
    }

    // 上辺中央から時計回り。角をまたぐ区切りも、一つの区切りとして扱う。
    static Vector2 PerimeterPoint(float distance, float w, float h)
    {
        float length = 4 * (w + h);
        distance = Mathf.Clamp(distance, 0, length);
        if (distance <= w) return new Vector2(distance, h);
        distance -= w;
        if (distance <= 2 * h) return new Vector2(w, h - distance);
        distance -= 2 * h;
        if (distance <= 2 * w) return new Vector2(w - distance, -h);
        distance -= 2 * w;
        if (distance <= 2 * h) return new Vector2(-w, -h + distance);
        return new Vector2(-w + distance - 2 * h, h);
    }

    void GaugeDimensions(int level, out float w, out float h, out float thickness)
    {
        thickness = .03f + .01f * level;
        float offset = GameStageSkin.GateBarThickness * .5f + .02f + thickness * .5f;
        w = frame.HalfWidth + offset; h = frame.HalfHeight + offset;
    }

    void DrawGauge()
    {
        if (gauge == null || frame == null || Meter == null) return;
        gauge.Clear();
        GaugeDimensions(Meter.Level, out float w, out float h, out float thickness);
        float length = 4 * (w + h);
        Color color = LevelColor(Meter.Level, visualTime);
        float[] corners = { w, w + 2 * h, 3 * w + 2 * h, 3 * w + 4 * h, length };

        for (int segment = 0; segment < 8; segment++)
        {
            float alpha = segment < Meter.Steps ? 1 : Meter.Steps >= 5 ? .35f : .15f;
            float cursor = segment * length / 8 + .018f;
            float end = (segment + 1) * length / 8 - .018f;
            while (cursor < end - .00001f)
            {
                float next = end;
                foreach (float corner in corners)
                    if (corner > cursor + .00001f) { next = Mathf.Min(next, corner); break; }
                Vector2 a = PerimeterPoint(cursor, w, h), b = PerimeterPoint(next, w, h);
                Color core = color; core.a = alpha;
                gauge.Band(new Vector3(a.x, a.y, -.18f), new Vector3(b.x, b.y, -.18f), thickness, core);

                // 光は帯の外側だけに足し、白い判定線との隙間を埋めない。
                Vector2 along = (b - a).normalized;
                Vector2 outward = new Vector2(-along.y, along.x);
                float glowWidth = .025f + Meter.Level * .01f;
                Vector2 offset = outward * (thickness + glowWidth) * .5f;
                Color glow = color; glow.a = alpha * (.12f + .065f * Meter.Level);
                gauge.Band(new Vector3(a.x + offset.x, a.y + offset.y, -.18f),
                    new Vector3(b.x + offset.x, b.y + offset.y, -.18f), glowWidth, glow);
                cursor = next;
            }
        }
        gauge.Flush();
    }

    void AddRing(Color color)
    {
        if (rings.Count < 8) rings.Add(new RingPulse { color = color });
    }

    void AddShards(int filled, int level)
    {
        if (frame == null) return;
        GaugeDimensions(level, out float w, out float h, out _);
        float length = 4 * (w + h);
        for (int segment = 0; segment < filled; segment++)
            for (int piece = 0; piece < 3; piece++)
            {
                if (shards.Count >= 96) return;
                shards.Add(new Shard
                {
                    origin = PerimeterPoint((segment + (piece + .5f) / 3) * length / 8, w, h),
                    velocity = new Vector2((random.Next() - .5f) * 1.7f, -.6f - random.Next()),
                    angle = random.Next() * Mathf.PI * 2,
                    spin = (random.Next() - .5f) * 12,
                    color = LevelColor(level, visualTime)
                });
            }
    }

    void DrawAccents(FlashRect protectedGate)
    {
        if (accents == null) return;
        accents.Clear();
        accents.Protect(protectedGate);
        foreach (var r in rings)
        {
            float p = r.age / .7f, ease = 1 - Mathf.Pow(1 - p, 3);
            float radius = Mathf.Lerp(GateAccents.RingFrom, GateAccents.RingTo, ease);
            Color color = r.color; color.a = .85f * Mathf.Pow(1 - p, 1.2f);
            for (int i = 0; i < 96; i++)
            {
                float a = i * Mathf.PI * 2 / 96, b = (i + 1) * Mathf.PI * 2 / 96;
                accents.Band(new Vector3(Mathf.Cos(a) * radius, Mathf.Sin(a) * radius, -.19f),
                    new Vector3(Mathf.Cos(b) * radius, Mathf.Sin(b) * radius, -.19f), .11f, color);
            }
        }
        foreach (var s in shards)
        {
            float p = s.age / .3f;
            Vector2 center = s.origin + s.velocity * s.age + Vector2.down * (5 * s.age * s.age);
            float angle = s.angle + s.spin * s.age;
            Vector2 a = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * .07f * (1 - p * .4f);
            Vector2 b = new Vector2(-a.y, a.x) * .45f;
            Color color = s.color; color.a = 1 - p;
            accents.Quad(new Vector3(center.x + a.x, center.y + a.y, -.19f),
                new Vector3(center.x - a.x + b.x, center.y - a.y + b.y, -.19f),
                new Vector3(center.x - a.x - b.x, center.y - a.y - b.y, -.19f),
                new Vector3(center.x - a.x - b.x, center.y - a.y - b.y, -.19f), color, false);
        }
        accents.Flush();
    }

    void DrawScreenFlashes()
    {
        if (largeLight == null) return;
        largeLight.Clear();
        if (DisplaySettings.ReducedEffects || !TryGateRect(out var rawGate)) { largeLight.Flush(); return; }
        var protectedGate = LargeFlashLayout.ProtectedGate(rawGate);
        largeLight.Protect(protectedGate);
        foreach (var pulse in flashes)
        {
            Color color = pulse.color;
            color.a = pulse.alpha * Mathf.Clamp01(1 - pulse.age / pulse.seconds);
            LargeFlashLayout.Build(pulse.kind, protectedGate, flashRects);
            foreach (var r in flashRects)
                largeLight.Quad(ScreenPoint(r.Left, r.Bottom), ScreenPoint(r.Right, r.Bottom),
                    ScreenPoint(r.Right, r.Top), ScreenPoint(r.Left, r.Top), color, false);
        }
        largeLight.Flush();
    }

    Vector3 ScreenPoint(double x, double y)
        => view.transform.InverseTransformPoint(view.ViewportToWorldPoint(
            new Vector3((float)x, (float)y, view.nearClipPlane + .05f)));

    void DrawTag(FlashRect gate, float dt, bool advancing)
    {
        if (tagCanvas == null || tag == null) return;
        if (advancing) tagLeft = Mathf.Max(0, tagLeft - dt);
        float x = Mathf.Clamp((float)(gate.Left + gate.Right) * 960, 106, 1814);
        float y = (float)gate.Top * 1080 + 48;
        bool fits = y + 17 < 1064;
        if (!fits)
        {
            x = (float)gate.Left * 1920 - 120;
            y = Mathf.Clamp((float)(gate.Top + gate.Bottom) * 540, 40, 1040);
            fits = x - 90 >= 16;
            if (!fits)
            {
                x = (float)gate.Right * 1920 + 120;
                fits = x + 90 <= 1904;
            }
        }
        tag.rectTransform.anchoredPosition = new Vector2(x, y);
        tagCanvas.enabled = advancing && tagLeft > 0 && fits;
    }

    void ClearTransients()
    {
        rings.Clear(); shards.Clear(); flashes.Clear();
        accents?.Hide(); largeLight?.Hide();
    }

    void HideVisuals()
    {
        gauge?.Hide(); accents?.Hide(); lasers?.Hide(); largeLight?.Hide();
        if (tagCanvas != null) tagCanvas.enabled = false;
    }

    void ReleaseVisuals()
    {
        gauge?.Dispose(); accents?.Dispose(); lasers?.Dispose(); largeLight?.Dispose();
        background?.Dispose();
        if (tagCanvas != null) UISkinKit.SafeDestroy(tagCanvas.gameObject);
    }
}
