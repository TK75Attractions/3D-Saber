using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// 熱の表示専用。元の剣・ノーツの材質や位置には触れない。
// 全部を一枚のメッシュへまとめ、曲中は頂点バッファと演出の枠を使い回す。
public sealed class HeatView : IDisposable
{
    const int PulseCapacity = 32;
    readonly GameObject root;
    readonly Mesh mesh;
    readonly Material material;
    readonly MeshRenderer renderer;
    readonly SaberCutJudge[] judges;
    readonly int max;
    readonly Dictionary<CuttableNote, uint> notes;
    readonly List<Vector3> vertices;
    readonly List<Color> colors;
    readonly List<int> triangles;
    readonly Pulse[] pulses = new Pulse[PulseCapacity];
    readonly Vector3[] roots = new Vector3[2], tips = new Vector3[2];
    readonly bool[] hasPose = new bool[2];
    readonly int[] bladeCounts = new int[2], gaugeCounts = new int[2], litGaugeCounts = new int[2];
    int nextPulse;
    HeatHand lastHand = HeatHand.Right;
    bool disposed;

    struct Pulse
    {
        public bool active;
        public HeatCue kind;
        public Vector3 point;
        public double at;
    }

    public int VisibleFlameNotes { get; private set; }
    public int SparkBursts { get; private set; }
    public int SteamBursts { get; private set; }
    public int CoolingRings { get; private set; }
    public int VertexCount => mesh != null ? mesh.vertexCount : 0;
    public bool IsDisposed => disposed;
    public int LitBladeSegments(HeatHand hand) => bladeCounts[Index(hand)];
    public int GaugeSegments(HeatHand hand) => gaugeCounts[Index(hand)];
    public int LitGaugeSegments(HeatHand hand) => litGaugeCounts[Index(hand)];

    static int Index(HeatHand hand) => hand == HeatHand.Left ? 1 : 0;
    static Vector3 Gauge(HeatHand hand)
        => new Vector3(hand == HeatHand.Left ? -3.9f : 3.9f, -2.15f, -.32f);

    public static HeatView Create(Transform parent, SaberCutJudge[] judges, int max, int noteCapacity = 32)
    {
        var shader = Resources.Load<Shader>("Effects/NoteGuide");
        return shader != null ? new HeatView(parent, judges, max, noteCapacity, shader) : null;
    }

    HeatView(Transform parent, SaberCutJudge[] judges, int max, int noteCapacity, Shader shader)
    {
        this.judges = judges;
        this.max = Mathf.Clamp(max, 1, HeatRules.MaxSupported);
        int capacity = Mathf.Max(32, noteCapacity);
        notes = new Dictionary<CuttableNote, uint>(capacity);
        // 一ノーツ最大56頂点。残りは両手と32個の輪・火花・湯気のために確保する。
        int vertexCapacity = 8192 + capacity * 64;
        vertices = new List<Vector3>(vertexCapacity);
        colors = new List<Color>(vertexCapacity);
        triangles = new List<int>(vertexCapacity * 3 / 2);
        root = new GameObject("HeatVisuals");
        root.transform.SetParent(parent, false);
        if (parent != null) root.layer = parent.gameObject.layer;
        mesh = new Mesh { name = "HeatVisuals", hideFlags = HideFlags.DontSave };
        if (vertexCapacity > 65535) mesh.indexFormat = IndexFormat.UInt32;
        mesh.MarkDynamic();
        material = new Material(shader)
        {
            name = "HeatGuide", hideFlags = HideFlags.DontSave, renderQueue = 3033
        };
        material.SetColor("_BaseColor", Color.white);
        root.AddComponent<MeshFilter>().sharedMesh = mesh;
        renderer = root.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off;
        renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        renderer.enabled = false;
    }

    public void Observe(CuttableNote note)
    {
        if (!disposed && note != null && note.IsHeat) notes[note] = note.SpawnVersion;
    }

    public void Forget(CuttableNote note) => notes.Remove(note);
    public void SetLastHand(HeatHand hand) => lastHand = hand;

    SaberCutJudge JudgeFor(HeatHand hand)
    {
        var wanted = hand == HeatHand.Left ? SaberHand.Left : SaberHand.Right;
        foreach (var judge in judges)
            if (judge != null && judge.hand == wanted) return judge;
        foreach (var judge in judges)
            if (judge != null && judge.hand == SaberHand.Any) return judge;
        return null;
    }

    public void Notify(HeatCue kind, HeatHand hand, double time, Vector3 point = default)
    {
        if (disposed) return;
        if (kind == HeatCue.Cool) point = Gauge(hand);
        else if (kind == HeatCue.Full)
        {
            var judge = JudgeFor(hand);
            var bridge = judge != null ? judge.bladeProvider : null;
            point = bridge != null && bridge.isActiveAndEnabled && bridge.HasBlade
                ? (bridge.WorldEndA + bridge.WorldEndB) * .5f + Vector3.back * .06f
                : Gauge(hand);
        }
        pulses[nextPulse] = new Pulse { active = true, kind = kind, point = point, at = time };
        nextPulse = (nextPulse + 1) % pulses.Length;
    }

    public void Draw(HeatSession session, double time)
    {
        if (disposed || session == null || !HeatRules.Finite(time)) return;
        vertices.Clear(); colors.Clear(); triangles.Clear();
        VisibleFlameNotes = SparkBursts = SteamBursts = CoolingRings = 0;
        DrawHand(HeatHand.Right, session, time);
        DrawHand(HeatHand.Left, session, time);
        foreach (var pair in notes)
        {
            var note = pair.Key;
            if (note == null || note.SpawnVersion != pair.Value || !note.isActiveAndEnabled
                || note.IsFinalized || !note.IsHeat || note.CueVisibility <= 0) continue;
            DrawNote(note, time);
            VisibleFlameNotes++;
        }
        DrawPulses(time);
        mesh.Clear();
        mesh.SetVertices(vertices);
        mesh.SetColors(colors);
        mesh.SetTriangles(triangles, 0, true);
        renderer.enabled = vertices.Count > 0;
    }

    static Color Temperature(int heat, int maximum)
        => heat >= maximum ? new Color(1f, .95f, .78f, .96f)
            : heat <= 1 ? new Color(1f, .55f, .06f, .96f)
            : new Color(1f, .22f, .035f, .96f);

    void DrawHand(HeatHand hand, HeatSession session, double time)
    {
        int h = Index(hand), heat = session.HeatOf(hand);
        bool full = heat >= session.Max, reduced = DisplaySettings.ReducedEffects;
        Color hot = Temperature(heat, session.Max);
        Vector3 g = Gauge(hand);
        bladeCounts[h] = gaugeCounts[h] = litGaugeCounts[h] = 0;
        Plate(g + Vector3.forward * .01f, .84f, max * .22f + .16f,
            new Color(.02f, .025f, .045f, .92f));
        Color handColor = hand == HeatHand.Left
            ? new Color(.22f, .65f, 1f, 1f) : new Color(1f, .12f, .2f, 1f);
        Stroke(g + new Vector3(-.36f, -.22f * max * .5f - .02f, -.01f),
            g + new Vector3(.36f, -.22f * max * .5f - .02f, -.01f), .09f, handColor);
        for (int i = 0; i < max; i++)
        {
            Plate(g + Vector3.up * ((i - (max - 1) * .5f) * .22f), .62f, .14f,
                i < heat ? hot : new Color(.18f, .23f, .3f, 1f));
            gaugeCounts[h]++;
            if (i < heat) litGaugeCounts[h]++;
        }

        var judge = JudgeFor(hand);
        var bridge = judge != null ? judge.bladeProvider : null;
        bool blade = bridge != null && bridge.isActiveAndEnabled && bridge.HasBlade && bridge.DrawBlade;
        // Any一本のときは最後に使った仮想の手だけを刃へ重ねる。固定の目盛りは両方残す。
        if (!blade || (judge.hand == SaberHand.Any && hand != lastHand))
        {
            hasPose[h] = false;
            return;
        }
        Vector3 a = bridge.WorldEndA, b = bridge.WorldEndB;
        // 端点に柄の識別はないため、初回は下側。その後は端点の入れ替わりを連続性で吸収する。
        if (hasPose[h]
            ? (a - tips[h]).sqrMagnitude + (b - roots[h]).sqrMagnitude
                < (a - roots[h]).sqrMagnitude + (b - tips[h]).sqrMagnitude
            : a.y > b.y || (Mathf.Approximately(a.y, b.y) && a.x > b.x))
            (a, b) = (b, a);
        roots[h] = a; tips[h] = b; hasPose[h] = true;
        a += Vector3.back * .06f; b += Vector3.back * .06f;
        float width = bridge.bladeWidth * (DisplaySettings.ProjectorMode ? ProjectorMode.BladeWidthScale : 1f);
        width = Mathf.Max(.01f, width) * .72f;
        hot.a = full && !reduced ? .84f + .035f * Mathf.Sin((float)time * 14.5f) : .84f;
        for (int i = 0; i < Mathf.Min(heat, max); i++)
        {
            Stroke(Vector3.Lerp(a, b, (i + .025f) / max),
                Vector3.Lerp(a, b, (i + .975f) / max), width, hot);
            bladeCounts[h]++;
        }
        if (!full) return;
        int steamCount = reduced ? 2 : 4;
        for (int i = 0; i < steamCount; i++)
        {
            float phase = Mathf.Repeat((float)time / .8f + i * .27f, 1f);
            Vector3 p = Vector3.Lerp(a, b, (i + 1f) / (steamCount + 1f));
            p += new Vector3(reduced ? 0 : .04f * Mathf.Sin((float)time * 5 + i), phase * .55f, -.015f);
            Color steam = new Color(.9f, .94f, 1f, .48f * (1 - phase) * DisplaySettings.AccentScale);
            Stroke(p, p + new Vector3(.055f, .13f, 0), .055f, steam);
            Stroke(p + new Vector3(.055f, .13f, 0), p + Vector3.up * .25f, .035f, steam);
        }
    }

    void DrawNote(CuttableNote note, double time)
    {
        var body = note.BodyRenderer;
        Transform basis = body != null ? body.transform : note.VisualTransform;
        Bounds bounds = body != null ? body.localBounds : new Bounds(Vector3.zero, Vector3.one);
        Vector3 x = basis.TransformVector(Vector3.right * bounds.extents.x);
        Vector3 y = basis.TransformVector(Vector3.up * bounds.extents.y);
        x += x.normalized * .13f; y += y.normalized * .13f;
        Vector3 c = basis.TransformPoint(bounds.center - Vector3.forward * bounds.extents.z)
            + Vector3.back * .035f;
        Vector3 a = c - x - y, b = c - x + y, d = c + x - y, e = c + x + y;
        float alpha = note.CueVisibility, brightness = note.Brightness;
        float width = DisplaySettings.ProjectorMode ? .13f : .11f;
        Color dark = new Color(.08f * brightness, .025f * brightness, 0, .9f * alpha);
        Color fire = new Color(brightness, .34f * brightness, .035f * brightness, .96f * alpha);
        Frame(a, b, e, d, width + .07f, dark);
        Frame(a, b, e, d, width, fire);
        bool reduced = DisplaySettings.ReducedEffects;
        int count = reduced ? 2 : 3;
        for (int i = 0; i < count; i++)
        {
            Vector3 p = Vector3.Lerp(b, e, (i + 1f) / (count + 1f));
            float wave = reduced ? 0 : Mathf.Sin((float)time * 7f + i * 2.1f + (float)note.HitTime);
            float height = reduced ? .14f : .28f + .055f * wave;
            Vector3 bend = p + new Vector3(.045f * wave, height * .55f, -.005f);
            Color flame = new Color(brightness, .66f * brightness, .12f * brightness,
                alpha * (reduced ? .45f : .9f));
            Stroke(p, bend, width * .8f, flame);
            Stroke(bend, p + new Vector3(-.035f * wave, height, -.005f), width * .45f, flame);
        }
    }

    void DrawPulses(double time)
    {
        bool reduced = DisplaySettings.ReducedEffects;
        float strength = DisplaySettings.AccentScale;
        for (int i = 0; i < pulses.Length; i++)
        {
            var p = pulses[i];
            if (!p.active) continue;
            double age = time - p.at;
            float duration = p.kind == HeatCue.Cool ? .5f : p.kind == HeatCue.Full ? .35f : .24f;
            if (age < 0) continue;
            if (age >= duration) { pulses[i].active = false; continue; }
            float k = (float)age / duration;
            if (p.kind == HeatCue.Cool)
            {
                CoolingRings++;
                Ring(p.point, .4f + k * (reduced ? .22f : .45f), .065f,
                    new Color(.16f, .72f, 1f, .85f * (1 - k) * strength));
                continue;
            }
            bool reject = p.kind == HeatCue.Reject;
            if (reject) SparkBursts++; else SteamBursts++;
            int count = reduced ? 4 : reject ? 8 : 6;
            for (int j = 0; j < count; j++)
            {
                float angle = reject ? j * Mathf.PI * 2 / count : .3f + j * 2.5f / (count - 1);
                Vector3 direction = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0);
                float reach = (reject ? .6f : .75f) * (reduced ? .65f : 1f);
                Vector3 a = p.point + direction * (.07f + k * reach);
                Color color = reject ? new Color(1f, .65f, .12f, .95f * (1 - k) * strength)
                    : new Color(.88f, .94f, 1f, .7f * (1 - k) * strength);
                Stroke(a, a + direction * (reject ? .16f : .26f) * (1 - k),
                    reject ? .055f : .09f, color);
            }
        }
    }

    void Frame(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float width, Color color)
    {
        Stroke(a, b, width, color); Stroke(b, c, width, color);
        Stroke(c, d, width, color); Stroke(d, a, width, color);
    }

    void Ring(Vector3 center, float radius, float width, Color color)
    {
        Vector3 previous = center + Vector3.right * radius;
        for (int i = 1; i <= 24; i++)
        {
            float angle = i * Mathf.PI * 2 / 24;
            Vector3 next = center + new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0) * radius;
            Stroke(previous, next, width, color);
            previous = next;
        }
    }

    void Plate(Vector3 center, float width, float height, Color color)
    {
        Vector3 x = Vector3.right * (width * .5f), y = Vector3.up * (height * .5f);
        Quad(center - x - y, center - x + y, center + x + y, center + x - y, color);
    }

    void Stroke(Vector3 a, Vector3 b, float width, Color color)
    {
        Vector3 delta = b - a;
        float length = Mathf.Sqrt(delta.x * delta.x + delta.y * delta.y);
        if (length < .00001f) return;
        Vector3 side = new Vector3(-delta.y, delta.x, 0) * (width * .5f / length);
        Quad(a - side, a + side, b + side, b - side, color);
    }

    void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color)
    {
        int first = vertices.Count;
        vertices.Add(root.transform.InverseTransformPoint(a)); vertices.Add(root.transform.InverseTransformPoint(b));
        vertices.Add(root.transform.InverseTransformPoint(c)); vertices.Add(root.transform.InverseTransformPoint(d));
        colors.Add(color); colors.Add(color); colors.Add(color); colors.Add(color);
        triangles.Add(first); triangles.Add(first + 1); triangles.Add(first + 2);
        triangles.Add(first); triangles.Add(first + 2); triangles.Add(first + 3);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        root.SetActive(false);
        notes.Clear();
        UISkinKit.SafeDestroy(root);
        UISkinKit.SafeDestroy(mesh);
        UISkinKit.SafeDestroy(material);
    }
}
