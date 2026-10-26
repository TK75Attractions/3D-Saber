using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// 熱の表示専用。刃の元の色は変更せず、T の灰色や復帰と競合させない。
// 全画面の光・文字・点数は作らない。
public sealed class HeatView : IDisposable
{
    sealed class HandView
    {
        public LineRenderer[] marks;
        public LineRenderer whiteBlade, cooling;
        public LineRenderer[] steam;
        public double cooledAt = double.NegativeInfinity;
    }

    readonly GameObject root;
    readonly Material material;
    readonly SaberCutJudge[] judges;
    readonly HandView[] hands = new HandView[2];
    readonly Dictionary<CuttableNote, LineRenderer> rims =
        new Dictionary<CuttableNote, LineRenderer>();
    bool disposed;

    public static HeatView Create(Transform parent, SaberCutJudge[] judges, int max)
    {
        var shader = Resources.Load<Shader>("Effects/NoteGuide")
            ?? Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
        return shader != null ? new HeatView(parent, judges, max, shader) : null;
    }

    HeatView(Transform parent, SaberCutJudge[] judges, int max, Shader shader)
    {
        root = new GameObject("HeatVisuals");
        root.transform.SetParent(parent, false);
        this.judges = judges;
        material = new Material(shader)
        {
            name = "HeatGuide",
            hideFlags = HideFlags.DontSave,
            renderQueue = 3033
        };
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.white);
        if (material.HasProperty("_Color")) material.SetColor("_Color", Color.white);
        for (int h = 0; h < 2; h++)
        {
            var view = new HandView
            {
                marks = new LineRenderer[max],
                whiteBlade = Line("HotBlade", 2, false, .045f),
                cooling = Line("CoolingRing", 20, true, .035f),
                steam = new LineRenderer[4]
            };
            for (int i = 0; i < max; i++) view.marks[i] = Line("HeatMark", 12, true, .04f);
            for (int i = 0; i < view.steam.Length; i++)
                view.steam[i] = Line("Steam", 3, false, .025f);
            hands[h] = view;
            Visible(view, false);
        }
    }

    LineRenderer Line(string name, int count, bool loop, float width)
    {
        var go = new GameObject(name);
        go.transform.SetParent(root.transform, false);
        var line = go.AddComponent<LineRenderer>();
        line.sharedMaterial = material;
        line.useWorldSpace = true;
        line.positionCount = count;
        line.loop = loop;
        line.startWidth = line.endWidth = width;
        line.numCapVertices = 2;
        line.alignment = LineAlignment.View;
        line.shadowCastingMode = ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.lightProbeUsage = LightProbeUsage.Off;
        line.reflectionProbeUsage = ReflectionProbeUsage.Off;
        return line;
    }

    static void ColorOf(LineRenderer line, Color color)
    {
        line.startColor = color;
        line.endColor = color;
    }

    static void Circle(LineRenderer line, Vector3 center, float radius)
    {
        for (int i = 0; i < line.positionCount; i++)
        {
            float angle = i * Mathf.PI * 2f / line.positionCount;
            line.SetPosition(i, center + new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0) * radius);
        }
    }

    static void Visible(HandView view, bool visible)
    {
        foreach (var mark in view.marks) mark.enabled = visible;
        view.whiteBlade.enabled = visible;
        view.cooling.enabled = visible;
        foreach (var steam in view.steam) steam.enabled = visible;
    }

    SaberCutJudge JudgeFor(HeatHand hand)
    {
        SaberHand wanted = hand == HeatHand.Right ? SaberHand.Right : SaberHand.Left;
        foreach (var judge in judges)
            if (judge != null && judge.hand == wanted) return judge;
        foreach (var judge in judges)
            if (judge != null && judge.hand == SaberHand.Any) return judge;
        return null;
    }

    public void Observe(CuttableNote note)
    {
        if (disposed || note == null || rims.ContainsKey(note)) return;
        var rim = Line("HeatNoteRim", 4, true, .025f);
        rim.gameObject.layer = note.gameObject.layer;
        rims.Add(note, rim);
    }

    public void Forget(CuttableNote note)
    {
        if (!rims.TryGetValue(note, out var rim)) return;
        rims.Remove(note);
        if (rim != null)
        {
            rim.gameObject.SetActive(false);
            Discard(rim.gameObject);
        }
    }

    public void Cooled(HeatHand hand, double songTime)
    {
        if (!disposed) hands[hand == HeatHand.Left ? 1 : 0].cooledAt = songTime;
    }

    public void Draw(HeatSession session, double songTime)
    {
        if (disposed) return;
        DrawHand(HeatHand.Right, hands[0], session, songTime);
        DrawHand(HeatHand.Left, hands[1], session, songTime);
        foreach (var pair in rims)
        {
            var note = pair.Key;
            var rim = pair.Value;
            bool visible = note != null && note.isActiveAndEnabled && !note.IsFinalized
                && note.IsHeat && note.CueVisibility > 0;
            rim.enabled = visible;
            if (!visible) continue;
            var body = note.BodyRenderer;
            Transform basis = body != null ? body.transform : note.VisualTransform;
            Bounds bounds = body != null ? body.localBounds : new Bounds(Vector3.zero, Vector3.one);
            float sway = .025f + .01f * Mathf.Sin((float)songTime * 4f);
            Vector3 extent = bounds.extents + new Vector3(sway, sway, .015f);
            Vector3 center = bounds.center;
            rim.SetPosition(0, basis.TransformPoint(center + new Vector3(-extent.x, -extent.y, -extent.z)));
            rim.SetPosition(1, basis.TransformPoint(center + new Vector3(-extent.x, extent.y, -extent.z)));
            rim.SetPosition(2, basis.TransformPoint(center + new Vector3(extent.x, extent.y, -extent.z)));
            rim.SetPosition(3, basis.TransformPoint(center + new Vector3(extent.x, -extent.y, -extent.z)));
            float brightness = note.Brightness;
            ColorOf(rim, new Color(brightness, .23f * brightness, .07f * brightness,
                .85f * note.CueVisibility));
        }
    }

    void DrawHand(HeatHand hand, HandView view, HeatSession session, double time)
    {
        var judge = JudgeFor(hand);
        var bridge = judge != null ? judge.bladeProvider : null;
        bool blade = bridge != null && bridge.isActiveAndEnabled && bridge.HasBlade;
        bool point = judge != null && judge.saber != null && judge.saber.HasPrevious;
        if (!blade && !point) { Visible(view, false); return; }

        Vector3 a = blade ? bridge.WorldEndA : judge.saber.CurrentPosition;
        Vector3 b = blade ? bridge.WorldEndB : a;
        a.z = b.z = -.23f;
        Vector3 basePoint = (a.y <= b.y ? a : b) + Vector3.down * .2f;
        if (judge.hand == SaberHand.Any)
            basePoint.x += hand == HeatHand.Right ? .35f : -.35f;

        int heat = session.HeatOf(hand);
        bool full = heat >= session.Max;
        float spacing = Mathf.Min(.16f, .9f / session.Max);
        Color hot = Color.Lerp(new Color(1f, .65f, .12f), new Color(1f, .12f, .04f),
            heat / (float)session.Max);
        for (int i = 0; i < view.marks.Length; i++)
        {
            var mark = view.marks[i];
            mark.enabled = true;
            mark.startWidth = mark.endWidth = i < heat ? spacing * .38f : spacing * .14f;
            Circle(mark, basePoint + Vector3.right * ((i - (session.Max - 1) * .5f) * spacing),
                spacing * .28f);
            ColorOf(mark, i < heat ? hot : new Color(.25f, .38f, .48f, .8f));
        }

        view.whiteBlade.enabled = full && blade;
        if (view.whiteBlade.enabled)
        {
            view.whiteBlade.SetPosition(0, a);
            view.whiteBlade.SetPosition(1, b);
            ColorOf(view.whiteBlade, new Color(1f, .93f, .83f, .8f));
        }
        for (int i = 0; i < view.steam.Length; i++)
        {
            var steam = view.steam[i];
            steam.enabled = full;
            if (!full) continue;
            float phase = Mathf.Repeat((float)time / .6f + i * .25f, 1f);
            Vector3 p = (a + b) * .5f
                + new Vector3((i - 1.5f) * .08f, phase * .4f, -.01f);
            steam.SetPosition(0, p);
            steam.SetPosition(1, p + new Vector3(.025f, .045f, 0));
            steam.SetPosition(2, p + new Vector3(0, .09f, 0));
            ColorOf(steam, new Color(.95f, .9f, .8f, .38f * (1f - phase)));
        }

        double age = time - view.cooledAt;
        view.cooling.enabled = age >= 0 && age < .4;
        if (view.cooling.enabled)
        {
            float k = (float)(age / .4);
            Circle(view.cooling, basePoint, .12f + k * .22f);
            ColorOf(view.cooling, new Color(.2f, .75f, 1f, .75f * (1f - k)));
        }
    }

    static void Discard(UnityEngine.Object value)
    {
        if (value == null) return;
        if (Application.isPlaying) UnityEngine.Object.Destroy(value);
        else UnityEngine.Object.DestroyImmediate(value);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        root.SetActive(false);
        rims.Clear();
        Discard(root);
        Discard(material);
    }
}
