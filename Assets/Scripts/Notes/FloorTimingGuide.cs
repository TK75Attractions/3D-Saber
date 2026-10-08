using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// 装飾の強弱に影響されない譜面ガイド。生成済みノーツの位置を同じTick内で床へ写す。
// 一枚の動的メッシュで判定線・横線・方向矢印を描き、ノーツごとの資源を増やさない。
public sealed class FloorTimingGuide : MonoBehaviour
{
    const int MaxMarkers = 256;
    static readonly Color Ink = new Color(.018f, .023f, .05f, 1);
    readonly List<Vector3> vertices = new List<Vector3>(8192);
    readonly List<Color> colors = new List<Color>(8192);
    readonly List<int> triangles = new List<int>(16384);
    readonly List<CuttableNote> candidates = new List<CuttableNote>(MaxMarkers);
    double priorityTime;
    float priorityCutSeconds;
    Mesh mesh;
    Material material;
    MeshRenderer display;
    float surfaceY;
    // カウントイン(3・2・1)で中央の判定線を左右に塗り分ける(左=青、右=赤)。量 0 で通常の白。
    Color countLeft = Color.white, countRight = Color.white;
    float countLeftAmount, countRightAmount;
    public int MarkerCount { get; private set; }
    public int LongProgressCount { get; private set; }
    public float JudgmentZ { get; private set; }
    public float CountTintAmount => Mathf.Max(countLeftAmount, countRightAmount);

    public void SetCountTint(Color left, float leftAmount, Color right, float rightAmount)
    {
        countLeft = left; countRight = right;
        countLeftAmount = Mathf.Clamp01(leftAmount); countRightAmount = Mathf.Clamp01(rightAmount);
    }

    public void ClearCountTint() { countLeftAmount = 0; countRightAmount = 0; }

    public static FloorTimingGuide Create(Transform parent, float floorY)
    {
        var go = new GameObject("FloorTimingGuide");
        go.transform.SetParent(parent, false);
        var guide = go.AddComponent<FloorTimingGuide>();
        guide.surfaceY = floorY + .14f;
        guide.mesh = new Mesh { name = "FloorTimingGuides", hideFlags = HideFlags.DontSave };
        guide.mesh.MarkDynamic();
        guide.material = new Material(Resources.Load<Shader>("Effects/NoteGuide")) { hideFlags = HideFlags.DontSave };
        go.AddComponent<MeshFilter>().sharedMesh = guide.mesh;
        guide.display = go.AddComponent<MeshRenderer>();
        guide.display.sharedMaterial = guide.material;
        guide.display.shadowCastingMode = ShadowCastingMode.Off;
        guide.display.receiveShadows = false;
        guide.display.lightProbeUsage = LightProbeUsage.Off;
        guide.display.reflectionProbeUsage = ReflectionProbeUsage.Off;
        guide.Clear();
        return guide;
    }

    public void Tick(NoteSpawner spawner, double songTime)
    {
        if (!isActiveAndEnabled || spawner == null || !spawner.isActiveAndEnabled ||
            double.IsNaN(songTime) || double.IsInfinity(songTime)) { Clear(); return; }
        vertices.Clear(); colors.Clear(); triangles.Clear(); MarkerCount = LongProgressCount = 0;
        JudgmentZ = spawner.judgeZ;
        if (!Finite(JudgmentZ)) { Clear(); return; }
        // 中央の白い固定線と暗い下敷き。中心がノーツの判定面と一致する。
        float visibility = DisplaySettings.ProjectorMode ? 1.45f : 1f;
        Bar(0, JudgmentZ, 7.2f, .19f * visibility, Ink, 0);
        var lineColor = new Color(.83f, .93f, 1);
        if (countLeftAmount <= 0 && countRightAmount <= 0) Bar(0, JudgmentZ, 7.2f, .065f * visibility, lineColor, .003f);
        else
        {
            Bar(-1.8f, JudgmentZ, 3.6f, .065f * visibility, Color.Lerp(lineColor, countLeft, countLeftAmount), .003f);
            Bar(1.8f, JudgmentZ, 3.6f, .065f * visibility, Color.Lerp(lineColor, countRight, countRightAmount), .003f);
        }
        candidates.Clear();
        foreach (var note in spawner.LiveNotes)
        {
            if (note == null || !note.gameObject.activeInHierarchy || note.IsCut || note.IsMissed || note.IsFinalized || !Finite(note.HitTime)) continue;
            Vector3 p = note.transform.position;
            if (!Finite(p.x) || !Finite(p.y) || !Finite(p.z)) continue;
            candidates.Add(note);
        }
        // 高密度譜面では遠いノーツより、今まさに切るノーツの案内を優先する。
        if (candidates.Count > MaxMarkers) { priorityTime = songTime; priorityCutSeconds = spawner.secondsPerLongCut; candidates.Sort(ComparePriority); }
        foreach (var note in candidates)
        {
            if (MarkerCount + LongProgressCount >= MaxMarkers) break;
            double remaining = note.HitTime - songTime;
            if (note.RequiredCutCount > 1 && remaining < -.13)
            {
                double duration = spawner.LingerSecondsFor(note);
                if (duration > 0 && -remaining < duration)
                {
                    DrawLongProgress(note, Mathf.Clamp01((float)(1 + remaining / duration)), visibility);
                    LongProgressCount++;
                }
                continue;
            }
            // 到着後は短く消す。ロングの開始時刻も同じ基準で示す。
            if (remaining < -.13 || MarkerCount >= MaxMarkers) continue;
            var position = note.transform.position;
            float alpha = Mathf.Clamp01((float)((spawner.approachTime - remaining) / .12));
            if (remaining < 0) alpha *= Mathf.Clamp01(1 + (float)remaining / .13f);
            if (alpha <= 0) continue;
            var visuals = note.GetComponent<NoteVisuals>();
            Color tint = visuals != null ? visuals.baseColor : UISkinPalette.NoteFlick;
            tint = Color.Lerp(tint, Color.white, .22f); tint.a = alpha;
            Color ink = Ink; ink.a = alpha;
            float size = Mathf.Clamp(Mathf.Abs(note.transform.lossyScale.x), .65f, 1.05f);
            if (note.RequiredDirection == CutDirection.None)
            {
                Bar(position.x, position.z, size * 1.22f, .24f, ink, .006f);
                Bar(position.x, position.z, size * 1.12f, .12f, tint, .009f);
                Bar(position.x, position.z, size * .68f, .035f, new Color(1, 1, 1, alpha), .012f);
                if (note.RequiredCutCount > 1)
                {
                    // 二重の後縁で、同じ色の単打とロングの開始を見分ける。
                    Bar(position.x, position.z + .31f, size * 1.12f, .09f * visibility, ink, .006f);
                    Bar(position.x, position.z + .31f, size, .035f * visibility, tint, .012f);
                }
            }
            else
            {
                Arrow(position, note.RequiredDirection, size, ink, .006f, true);
                Arrow(position, note.RequiredDirection, size, Color.Lerp(tint, new Color(1, 1, 1, alpha), .75f), .009f);
            }
            MarkerCount++;
        }
        mesh.Clear(); mesh.SetVertices(vertices); mesh.SetColors(colors); mesh.SetTriangles(triangles, 0, true);
        display.enabled = true;
    }

    int ComparePriority(CuttableNote a, CuttableNote b)
    {
        int order = Priority(a).CompareTo(Priority(b));
        return order != 0 ? order : a.HitTime.CompareTo(b.HitTime);
    }

    double Priority(CuttableNote note)
    {
        double duration = note.OverrideLingerSeconds > 0 ? note.OverrideLingerSeconds : (note.RequiredCutCount - 1) * priorityCutSeconds;
        if (note.RequiredCutCount > 1 && priorityTime >= note.HitTime && priorityTime < note.HitTime + duration) return 0;
        return System.Math.Abs(note.HitTime - priorityTime);
    }

    void DrawLongProgress(CuttableNote note, float timeLeft, float visibility)
    {
        float x = note.transform.position.x;
        Color tint = note.IsGold ? UISkinPalette.NoteGold : note.RequiredHand == SaberHand.Left ? UISkinPalette.LogoBlue : UISkinPalette.LogoRed;
        tint.a = .85f;
        // 上段は残り時間、下段は残り切断回数。高さを占有せず判定線の手前に収める。
        const float width = 1.1f;
        Bar(x, JudgmentZ - .34f, width + .10f, .16f * visibility, Ink, .006f);
        Bar(x - width * (1 - timeLeft) / 2, JudgmentZ - .34f, width * timeLeft, .065f * visibility, tint, .009f);
        int segments = Mathf.Min(8, Mathf.Max(1, note.RequiredCutCount));
        float cutsLeft = Mathf.Clamp01(note.RemainingCuts / (float)Mathf.Max(1, note.RequiredCutCount));
        for (int i = 0; i < segments; i++)
        {
            float center = x - width / 2 + width * (i + .5f) / segments;
            Bar(center, JudgmentZ - .61f, width / segments * .76f, .075f * visibility, Ink, .006f);
            float fill = Mathf.Clamp01(cutsLeft * segments - i);
            if (fill > 0) Bar(center, JudgmentZ - .61f, width / segments * .65f * fill, .035f * visibility, tint, .012f);
        }
    }
    static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    void Bar(float x, float z, float width, float depth, Color color, float bias)
    {
        int start = vertices.Count;
        Vertex(new Vector3(x - width / 2, surfaceY + bias, z - depth / 2), color);
        Vertex(new Vector3(x + width / 2, surfaceY + bias, z - depth / 2), color);
        Vertex(new Vector3(x + width / 2, surfaceY + bias, z + depth / 2), color);
        Vertex(new Vector3(x - width / 2, surfaceY + bias, z + depth / 2), color);
        triangles.Add(start); triangles.Add(start + 1); triangles.Add(start + 2);
        triangles.Add(start); triangles.Add(start + 2); triangles.Add(start + 3);
    }

    void Arrow(Vector3 position, CutDirection direction, float size, Color color, float bias, bool outline = false)
    {
        int start = vertices.Count;
        var rotation = Quaternion.Euler(0, 0, CutDirectionHelper.ToZRotationDegrees(direction));
        var points = outline ? FlickArrowShape.OutlinePoints : FlickArrowShape.Points;
        for (int i = 0; i < points.Length; i++)
        {
            Vector3 p = rotation * (Vector3)(points[i] * size);
            // 床の遠近で潰れないよう、前後の長さだけ広げる。上矢印は画面奥へ向く。
            Vertex(new Vector3(position.x + p.x, surfaceY + bias, position.z + p.y * 2.2f), outline ? color : color * FlickArrowShape.VertexColor(i));
        }
        foreach (int index in FlickArrowShape.Triangles) triangles.Add(start + index);
    }

    void Vertex(Vector3 world, Color color) { vertices.Add(transform.InverseTransformPoint(world)); colors.Add(color); }
    public void Clear()
    {
        MarkerCount = LongProgressCount = 0;
        candidates.Clear();
        if (mesh != null) mesh.Clear();
        if (display != null) display.enabled = false;
    }
    void OnDisable() { Clear(); }
    void OnDestroy() { UISkinKit.SafeDestroy(mesh); UISkinKit.SafeDestroy(material); }
}
