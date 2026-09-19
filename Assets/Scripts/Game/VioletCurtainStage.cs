using UnityEngine;
using UnityEngine.Rendering;

// 紫の側廊を覆う不透明な折り幕。曲区間の開度だけで動き、判定やBPMには応答しない。
[ExecuteAlways]
public sealed class VioletCurtainStage : MonoBehaviour
{
    private const int Windows = 4, Columns = 6, Rows = 4;
    private const float HalfWidth = 1.82f, FoldedWidth = .36f;
    private const float Top = 4.72f, Height = 3.72f;
    public const int VertexCount = Windows * 2 * (Columns * Rows + Columns + 1) * 12;
    private readonly Vector3[] centers = new Vector3[Windows];
    private Vector3[] vertices, normals;
    private Mesh curtainMesh;
    private Material curtainMaterial;
    private bool built;
    private int write;
    public float Opening { get; private set; }
    public double LastTickSeconds { get; private set; }
    public int WindowCount => built ? Windows : 0;
    public Vector3 WindowCenter(int index) => index >= 0 && index < Windows ? centers[index] : Vector3.zero;

    public static VioletCurtainStage Ensure(FloorRenderer floor)
    {
        if (floor == null || floor.ActiveTheme != StageTheme.VioletVault || !floor.addSideArchitecture ||
            !Finite(floor.floorY) || !Finite(floor.minZ) || !Finite(floor.maxZ) || !Finite(floor.distantExtension) ||
            floor.maxZ + Mathf.Max(0, floor.distantExtension) < floor.minZ + 18) return null;
        var existing = floor.GetComponentInChildren<VioletCurtainStage>(true);
        if (existing != null) return existing;
        var shader = Resources.Load<Shader>("Stage/ObsidianMetal");
        if (shader == null) return null;
        var go = new GameObject("VioletCurtains");
        go.transform.SetParent(floor.transform, false);
        var stage = go.AddComponent<VioletCurtainStage>();
        stage.Build(floor, shader);
        return stage;
    }

    private void Build(FloorRenderer floor, Shader shader)
    {
        for (int window = 0; window < Windows; window++)
        {
            int side = window < 2 ? -1 : 1;
            bool near = window == 0 || window == 3;
            centers[window] = new Vector3(side * 7.52f, floor.floorY + Top - Height * .5f,
                floor.minZ + (near ? 9 : 15));
        }
        vertices = new Vector3[VertexCount];
        normals = new Vector3[VertexCount];
        var indices = new int[VertexCount];
        for (int i = 0; i < indices.Length; i++) indices[i] = i;
        curtainMaterial = new Material(shader) { name = "VioletCurtains/MatteFabric", hideFlags = HideFlags.DontSave };
        curtainMaterial.SetColor("_BaseColor", new Color(.115f, .075f, .155f, 1));
        curtainMaterial.SetColor("_EmissionColor", Color.black);
        curtainMaterial.SetFloat("_Smoothness", .07f);
        curtainMaterial.SetFloat("_Metallic", 0);
        curtainMaterial.SetFloat("_MotionStyle", 0);
        curtainMesh = new Mesh { name = "VioletCurtains/FoldedCloth", hideFlags = HideFlags.DontSave };
        curtainMesh.MarkDynamic();
        var filter = gameObject.AddComponent<MeshFilter>();
        filter.sharedMesh = curtainMesh;
        var renderer = gameObject.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = curtainMaterial;
        renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off; renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        built = true;
        Draw();
        curtainMesh.SetTriangles(indices, 0);
        curtainMesh.bounds = new Bounds(new Vector3(0, floor.floorY + 2.90f, floor.minZ + 12), new Vector3(15.6f, 4.5f, 10.2f));
    }

    public void Tick(double songSeconds, float opening)
    {
        if (!built || !isActiveAndEnabled || double.IsNaN(songSeconds) || double.IsInfinity(songSeconds)) return;
        LastTickSeconds = System.Math.Max(0, songSeconds);
        float value = LastTickSeconds <= 0 || !Finite(opening) ? 0 : Mathf.Clamp01(opening);
        value *= DisplaySettings.ReducedEffects ? .30f : 1;
        if (Opening == value) return;
        Opening = value;
        Draw();
    }

    public void Clear()
    {
        LastTickSeconds = 0;
        if (!built || Opening == 0) return;
        Opening = 0;
        Draw();
    }

    private void Draw()
    {
        write = 0;
        for (int window = 0; window < Windows; window++)
            for (int half = 0; half < 2; half++)
            {
                for (int row = 0; row < Rows; row++)
                    for (int col = 0; col < Columns; col++)
                        DoubleQuad(Point(window, half, row, col), Point(window, half, row + 1, col),
                            Point(window, half, row + 1, col + 1), Point(window, half, row, col + 1));
                // レールを滑る吊り帯。上端から布までをつなぎ、折り畳み中も浮かせない。
                for (int col = 0; col <= Columns; col++)
                {
                    Vector3 bottom = Point(window, half, 0, col);
                    Vector3 top = new Vector3(Mathf.Sign(centers[window].x) * 7.65f, bottom.y + .15f, bottom.z);
                    Vector3 edge = Vector3.forward * .016f;
                    DoubleQuad(top - edge, bottom - edge, bottom + edge, top + edge);
                }
            }
        curtainMesh.SetVertices(vertices);
        curtainMesh.SetNormals(normals);
    }

    private Vector3 Point(int window, int half, int row, int col)
    {
        Vector3 center = centers[window];
        float side = Mathf.Sign(center.x), end = half == 0 ? -1 : 1;
        float u = col / (float)Columns, v = row / (float)Rows;
        float width = Mathf.Lerp(HalfWidth, FoldedWidth, Opening);
        float z = center.z + end * (HalfWidth - u * width);
        // 山折りと谷折りを深めながら幅を畳む。布の基底高さと厚みはLOWでも同じ。
        float depth = Mathf.Lerp(.055f, .30f, Opening) * (col % 2);
        float x = side * (7.52f - depth * (.88f + .12f * v));
        float y = center.y + Height * .5f - Height * v + .035f * v * (col % 2);
        return new Vector3(x, y, z);
    }

    private void DoubleQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        Triangle(a, b, c); Triangle(a, c, d);
        Triangle(a, c, b); Triangle(a, d, c);
    }

    private void Triangle(Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 normal = Vector3.Cross(b - a, c - a).normalized;
        vertices[write] = a; vertices[write + 1] = b; vertices[write + 2] = c;
        normals[write] = normals[write + 1] = normals[write + 2] = normal;
        write += 3;
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private void OnDisable() { Clear(); }
    private void OnDestroy()
    {
        Release(curtainMesh); Release(curtainMaterial);
        curtainMesh = null; curtainMaterial = null;
        vertices = normals = null;
        built = false;
    }
    private static void Release(Object item)
    {
        if (item == null) return;
        if (Application.isPlaying) Destroy(item); else DestroyImmediate(item);
    }
}
