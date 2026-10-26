using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// ノーツの周囲だけに出す小さな桜。ひとつのメッシュを使い回し、曲の時計だけで動かす。
public sealed class FallPetals : MonoBehaviour
{
    public const int MaxPetals = 72;
    public int PetalCount { get; private set; }

    static readonly Vector2[] Outline =
    {
        new Vector2(-.065f, -.025f),
        new Vector2(-.055f, .055f),
        new Vector2(-.022f, .095f),
        new Vector2(0f, .055f),
        new Vector2(.025f, .095f),
        new Vector2(.065f, .035f),
        new Vector2(.020f, -.090f),
    };

    readonly List<Vector3> vertices = new List<Vector3>(MaxPetals * 8);
    readonly List<Color> colors = new List<Color>(MaxPetals * 8);
    readonly List<int> triangles = new List<int>(MaxPetals * 21);
    readonly List<CuttableNote> petalNotes = new List<CuttableNote>(MaxPetals);
    Mesh mesh;
    Material material;
    MeshRenderer display;
    Mesh lineMesh;
    MeshRenderer lineDisplay;
    readonly Vector3[] lineVertices = new Vector3[4];
    public bool LineVisible => lineDisplay != null && lineDisplay.enabled
        && lineDisplay.gameObject.activeInHierarchy;

    public static FallPetals Create(Transform parent)
    {
        var go = new GameObject("FallPetals");
        go.transform.SetParent(parent, false);
        var petals = go.AddComponent<FallPetals>();
        petals.mesh = new Mesh { name = "FallPetals", hideFlags = HideFlags.DontSave };
        petals.mesh.MarkDynamic();
        petals.material = new Material(Resources.Load<Shader>("Effects/NoteGuide"))
        {
            hideFlags = HideFlags.DontSave
        };
        petals.material.SetColor("_BaseColor", Color.white);
        go.AddComponent<MeshFilter>().sharedMesh = petals.mesh;
        petals.display = go.AddComponent<MeshRenderer>();
        petals.display.sharedMaterial = petals.material;
        petals.display.shadowCastingMode = ShadowCastingMode.Off;
        petals.display.receiveShadows = false;
        petals.display.lightProbeUsage = LightProbeUsage.Off;
        petals.display.reflectionProbeUsage = ReflectionProbeUsage.Off;
        // 花びらとは別メッシュにして、花びら0個でも判定の線を残す。
        var line = new GameObject("FallJudgeLine", typeof(MeshFilter), typeof(MeshRenderer));
        line.transform.SetParent(go.transform, false);
        line.layer = go.layer;
        petals.lineMesh = new Mesh { name = "FallJudgeLine", hideFlags = HideFlags.DontSave };
        petals.lineMesh.MarkDynamic();
        petals.lineMesh.vertices = new Vector3[4];
        petals.lineMesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        Color ink = new Color(.7f, .64f, .68f, .22f);
        petals.lineMesh.colors = new[] { ink, ink, ink, ink };
        line.GetComponent<MeshFilter>().sharedMesh = petals.lineMesh;
        petals.lineDisplay = line.GetComponent<MeshRenderer>();
        // 通常のアルファ合成。発光・点滅・文字は足さない。
        petals.lineDisplay.sharedMaterial = petals.material;
        petals.lineDisplay.shadowCastingMode = ShadowCastingMode.Off;
        petals.lineDisplay.receiveShadows = false;
        petals.lineDisplay.lightProbeUsage = LightProbeUsage.Off;
        petals.lineDisplay.reflectionProbeUsage = ReflectionProbeUsage.Off;
        petals.Clear();
        return petals;
    }

    public void Tick(NoteSpawner spawner, double songTime)
    {
        if (!isActiveAndEnabled || spawner == null || !spawner.isActiveAndEnabled
            || !spawner.HasFall || double.IsNaN(songTime) || double.IsInfinity(songTime))
        {
            Clear();
            return;
        }

        DrawJudgeLine(spawner.judgeZ);
        vertices.Clear();
        colors.Clear();
        triangles.Clear();
        PetalCount = 0;
        petalNotes.Clear();
        var notes = spawner.LiveNotes;
        for (int noteIndex = 0; noteIndex < notes.Count; noteIndex++)
        {
            if (PetalCount >= MaxPetals) break;
            var note = notes[noteIndex];
            if (note == null || !note.IsFall || !note.gameObject.activeInHierarchy
                || note.IsCut || note.IsMissed || note.IsFinalized || note.CutsAchieved > 0)
                continue;
            double remaining = note.HitTime - songTime;
            float alpha = FallRules.PetalAlpha(remaining, spawner.approachTime);
            if (alpha <= 0f) continue;
            Vector3 center = note.VisualTransform.position;
            float age = (float)(spawner.approachTime - remaining);
            for (int i = 0; i < 3 && PetalCount < MaxPetals; i++)
            {
                float phase = age * 6f + i * 2.1f + (float)(note.HitTime % 7);
                float side = i == 0 ? -.45f : i == 1 ? .45f : .16f;
                Vector3 offset = new Vector3(
                    side + .04f * Mathf.Sin(phase),
                    (i - 1) * .12f + .07f * Mathf.Cos(phase * .8f),
                    -.55f);
                DrawPetal(center + offset, phase, alpha);
                petalNotes.Add(note);
            }
        }

        mesh.Clear();
        mesh.SetVertices(vertices);
        RefreshDarkBrightness();
        mesh.SetTriangles(triangles, 0, true);
        display.enabled = PetalCount > 0;
    }

    void DrawJudgeLine(float judgeZ)
    {
        float x = FallRules.LineHalfWidth;
        float y = FallRules.LineY;
        float half = FallRules.LineThickness * .5f;
        lineVertices[0] = transform.InverseTransformPoint(new Vector3(-x, y - half, judgeZ));
        lineVertices[1] = transform.InverseTransformPoint(new Vector3(x, y - half, judgeZ));
        lineVertices[2] = transform.InverseTransformPoint(new Vector3(x, y + half, judgeZ));
        lineVertices[3] = transform.InverseTransformPoint(new Vector3(-x, y + half, judgeZ));
        lineMesh.vertices = lineVertices;
        lineMesh.RecalculateBounds();
        lineDisplay.enabled = true;
    }

    void DrawPetal(Vector3 center, float phase, float alpha)
    {
        int first = vertices.Count;
        Color color = new Color(1f, .76f, .87f, alpha);
        vertices.Add(transform.InverseTransformPoint(center));
        colors.Add(color);
        float angle = .45f * Mathf.Sin(phase);
        float c = Mathf.Cos(angle), s = Mathf.Sin(angle);
        float fold = .55f + .45f * Mathf.Abs(Mathf.Cos(phase * .7f));
        foreach (var point in Outline)
        {
            float x = point.x * fold, y = point.y;
            Vector3 offset = new Vector3(c * x - s * y, s * x + c * y, 0);
            vertices.Add(transform.InverseTransformPoint(center + offset));
            colors.Add(color);
        }
        for (int i = 0; i < Outline.Length; i++)
        {
            triangles.Add(first);
            triangles.Add(first + 1 + i);
            triangles.Add(first + 1 + (i + 1) % Outline.Length);
        }
        PetalCount++;
    }

    // RGB だけを元の桜色から作り直す。重ね掛けせず、フェード用の透明度は保つ。
    public void RefreshDarkBrightness()
    {
        int stride = Outline.Length + 1;
        for (int i = 0; i < PetalCount; i++)
        {
            var note = petalNotes[i];
            float brightness = note != null ? note.Brightness : 1f;
            for (int j = i * stride; j < (i + 1) * stride; j++)
                colors[j] = new Color(brightness, .76f * brightness, .87f * brightness, colors[j].a);
        }
        if (mesh != null) mesh.SetColors(colors);
    }

    public void Clear()
    {
        PetalCount = 0;
        petalNotes.Clear();
        colors.Clear();
        if (mesh != null) mesh.Clear();
        if (display != null) display.enabled = false;
        if (lineDisplay != null) lineDisplay.enabled = false;
    }

    void OnDisable() { Clear(); }
    void OnDestroy()
    {
        UISkinKit.SafeDestroy(mesh);
        UISkinKit.SafeDestroy(lineMesh);
        UISkinKit.SafeDestroy(material);
    }
}
