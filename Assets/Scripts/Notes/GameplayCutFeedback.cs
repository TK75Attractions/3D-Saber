using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Perfectが確定した場所にだけ短い刃の光と小さな光片を出す。
// 画面全体のフラッシュやカメラ揺れ、物理判定は追加しない。
public sealed class GameplayCutFeedback : MonoBehaviour
{
    public const int MaxBursts = 12;
    public const float BurstLifetime = .24f;
    const float SlashLifetime = .075f;
    public const int SparkCount = 8;
    public const int VerticesPerBurst = (SparkCount + 2) * 4;

    struct Burst
    {
        public bool active;
        public Vector3 position;
        public Vector2 direction;
        public Color color;
        public float age, scale, gain;
    }

    readonly Burst[] bursts = new Burst[MaxBursts];
    readonly HashSet<CuttableNote> tracked = new HashSet<CuttableNote>();
    readonly List<Vector3> vertices = new List<Vector3>(MaxBursts * VerticesPerBurst);
    readonly List<Color> colors = new List<Color>(MaxBursts * VerticesPerBurst);
    readonly List<Vector2> uvs = new List<Vector2>(MaxBursts * VerticesPerBurst);
    readonly List<int> triangles = new List<int>(MaxBursts * VerticesPerBurst * 3 / 2);
    NoteSpawner owner;
    Mesh mesh;
    Material material;
    MeshRenderer meshRenderer;
    public int ActiveCount { get; private set; }

    public static GameplayCutFeedback Create(NoteSpawner spawner)
    {
        var go = new GameObject("GameplayCutFeedback");
        go.transform.SetParent(spawner.transform, false);
        var feedback = go.AddComponent<GameplayCutFeedback>();
        feedback.owner = spawner;
        feedback.BuildRenderer();
        return feedback;
    }

    void BuildRenderer()
    {
        // Resourcesから参照し、ビルドでも頂点色を使う専用シェーダーが残るようにする。
        var shader = Resources.Load<Shader>("Effects/GameplayCutAccent");
        if (shader == null) return;
        mesh = new Mesh { name = "GameplayCutFeedback", hideFlags = HideFlags.DontSave };
        mesh.MarkDynamic();
        gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
        material = new Material(shader) { name = "GameplayCutFeedback", hideFlags = HideFlags.DontSave };
        meshRenderer = gameObject.AddComponent<MeshRenderer>();
        meshRenderer.sharedMaterial = material;
        meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
        meshRenderer.receiveShadows = false;
        meshRenderer.lightProbeUsage = LightProbeUsage.Off;
        meshRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        meshRenderer.enabled = false;
    }

    public void Track(CuttableNote note)
    {
        if (note == null || !tracked.Add(note)) return;
        note.OnJudged += HandleCut;
        note.OnMiss += HandleMiss;
    }

    void Untrack(CuttableNote note)
    {
        if (note == null) return;
        note.OnJudged -= HandleCut;
        note.OnMiss -= HandleMiss;
        tracked.Remove(note);
    }

    void HandleMiss(CuttableNote note) { Untrack(note); }

    void HandleCut(CuttableNote note, JudgmentTier tier, Vector3 hitPoint, Vector3 velocity)
    {
        Untrack(note);
        // 部分達成ロングのタイムアウトもOnCutを通知するため、実際に切れた時だけ描く。
        if (tier != JudgmentTier.Perfect || note == null || !note.IsCut || note.IsMissed || !isActiveAndEnabled
            || owner == null || !owner.isActiveAndEnabled || mesh == null) return;
        if (!Finite(hitPoint) || !Finite(velocity)) return;

        Vector3 center = note.transform.position;
        Vector3 size = note.transform.lossyScale;
        float scale = Mathf.Clamp(Mathf.Max(Mathf.Abs(size.x), Mathf.Abs(size.y)), .6f, 1.5f);
        var renderer = note.GetComponent<MeshRenderer>();
        // ノーツ破棄後はTransformを参照しない。正面へ少しだけ出して二分割片とZ競合を避ける。
        Vector3 position = new Vector3(
            Mathf.Clamp(hitPoint.x, center.x - scale * .5f, center.x + scale * .5f),
            Mathf.Clamp(hitPoint.y, center.y - scale * .5f, center.y + scale * .5f),
            renderer != null ? renderer.bounds.min.z - .035f : center.z - .55f);
        Vector2 direction = new Vector2(velocity.x, velocity.y);
        direction = direction.sqrMagnitude > .0001f ? direction.normalized : Vector2.right;
        var visuals = note.GetComponent<NoteVisuals>();
        Color accent = visuals != null ? visuals.baseColor : UISkinPalette.NoteFlick;
        if (note.IsGold) accent = UISkinPalette.NoteGold;
        accent.a = 1f;

        int slot = -1;
        int oldest = 0;
        for (int i = 0; i < bursts.Length; i++)
        {
            if (!bursts[i].active && slot < 0) slot = i;
            if (bursts[i].age > bursts[oldest].age) oldest = i;
            // 同じ場所の同時切りを白く積み重ねず、短い一つのアクセントへまとめる。
            if (bursts[i].active && bursts[i].age < .035f
                && (bursts[i].position - position).sqrMagnitude < .09f)
            {
                slot = i;
                break;
            }
        }
        if (slot < 0) slot = oldest;
        if (!bursts[slot].active) ActiveCount++;
        bursts[slot] = new Burst {
            active = true, position = position, direction = direction, color = accent,
            scale = scale, age = 0f, gain = 1f / Mathf.Sqrt(1f + .12f * (ActiveCount - 1))
        };
        RebuildMesh();
    }

    // NoteSpawner.Tickからだけ呼ぶ。既存の破片と同じTime.deltaTimeで寿命を進める。
    public void Tick(float deltaTime)
    {
        if (!isActiveAndEnabled || ActiveCount == 0) return;
        if (float.IsNaN(deltaTime) || float.IsInfinity(deltaTime)) return;
        for (int i = 0; i < bursts.Length; i++)
        {
            if (!bursts[i].active) continue;
            bursts[i].age += Mathf.Max(0f, deltaTime);
            if (bursts[i].age >= BurstLifetime)
            {
                bursts[i].active = false;
                ActiveCount--;
            }
        }
        RebuildMesh();
    }

    void RebuildMesh()
    {
        if (mesh == null) return;
        vertices.Clear(); colors.Clear(); uvs.Clear(); triangles.Clear();
        foreach (var burst in bursts)
        {
            if (!burst.active) continue;
            Vector3 direction = new Vector3(burst.direction.x, burst.direction.y, 0f);
            Vector3 normal = new Vector3(-direction.y, direction.x, 0f);
            float slash = Mathf.Pow(Mathf.Clamp01(1f - burst.age / SlashLifetime), 2f) * burst.gain;
            if (slash > .001f)
            {
                Vector3 center = burst.position + direction * (burst.age * 1.5f * burst.scale);
                float length = (1.16f + burst.age * 2f) * burst.scale;
                Streak(center, direction, length, .11f * burst.scale, WithAlpha(burst.color, slash * .4f));
                Streak(center, direction, length * .93f, .032f * burst.scale,
                    WithAlpha(Color.Lerp(burst.color, Color.white, .9f), slash * .96f));
            }
            float life = Mathf.Clamp01(1f - burst.age / BurstLifetime);
            for (int i = 0; i < SparkCount; i++)
            {
                float side = (i & 1) == 0 ? -1f : 1f;
                // 主に振った方向へ飛ばし、少数だけ後方へ散らす。細い白い芯は短く減衰する。
                float spread = .18f + (i % 3) * .3f;
                Vector3 outward = (direction * (i < 6 ? 1f : -.6f) + normal * (side * spread)).normalized;
                float speed = 2.6f + (i % 4) * .65f;
                float travel = (.08f + burst.age * speed) * burst.scale;
                Vector3 position = burst.position + outward * travel + Vector3.down * (burst.age * burst.age * 3f);
                Streak(position, (outward + Vector3.down * burst.age).normalized,
                    (.07f + life * (.16f + i % 3 * .045f)) * burst.scale, (.011f + (i % 2) * .004f) * burst.scale,
                    WithAlpha(Color.Lerp(burst.color, Color.white, .5f + life * .35f), life * life * burst.gain * .84f));
            }
        }
        mesh.Clear();
        mesh.SetVertices(vertices); mesh.SetColors(colors); mesh.SetUVs(0, uvs);
        mesh.SetTriangles(triangles, 0, true);
        meshRenderer.enabled = vertices.Count > 0;
    }

    void Streak(Vector3 center, Vector3 direction, float length, float width, Color tint)
    {
        Vector3 half = direction * (length * .5f);
        Vector3 edge = new Vector3(-direction.y, direction.x, 0f) * (width * .5f);
        int first = vertices.Count;
        // ワールド座標をこのメッシュのローカルへ戻し、Spawnerの位置/回転/拡大に依存させない。
        vertices.Add(transform.InverseTransformPoint(center - half - edge));
        vertices.Add(transform.InverseTransformPoint(center + half - edge));
        vertices.Add(transform.InverseTransformPoint(center + half + edge));
        vertices.Add(transform.InverseTransformPoint(center - half + edge));
        for (int i = 0; i < 4; i++) colors.Add(tint);
        uvs.Add(new Vector2(0, 0)); uvs.Add(new Vector2(1, 0));
        uvs.Add(new Vector2(1, 1)); uvs.Add(new Vector2(0, 1));
        triangles.Add(first); triangles.Add(first + 1); triangles.Add(first + 2);
        triangles.Add(first); triangles.Add(first + 2); triangles.Add(first + 3);
    }

    public void ClearEffects()
    {
        for (int i = 0; i < bursts.Length; i++) bursts[i].active = false;
        ActiveCount = 0;
        if (mesh != null) mesh.Clear();
        if (meshRenderer != null) meshRenderer.enabled = false;
    }

    public void ResetState()
    {
        foreach (var note in tracked)
        {
            if (note == null) continue;
            note.OnJudged -= HandleCut;
            note.OnMiss -= HandleMiss;
        }
        tracked.Clear();
        ClearEffects();
    }

    void OnDisable() { ClearEffects(); }
    void OnDestroy()
    {
        ResetState();
        UISkinKit.SafeDestroy(mesh);
        UISkinKit.SafeDestroy(material);
    }

    static Color WithAlpha(Color tint, float alpha) { tint.a = alpha; return tint; }
    static bool Finite(Vector3 value)
    {
        return !float.IsNaN(value.x) && !float.IsInfinity(value.x)
            && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
            && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
    }
}
