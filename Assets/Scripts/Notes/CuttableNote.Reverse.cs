using System.Collections.Generic;
using UnityEngine;

// Manic Sonic の裏譜面 R の描画先(F の FallVisual・W の WindVisual と同じ作り)。判定の根元(transform)・着地の枠・床の印は動かさない。
//   ReverseVisual: 本体の描画先と、芯・縁・光の板・矢印・ひびを移した子。鏡門のノーツは門を通るまで x だけ負の拡縮にして、
//     本当の位置の左右の鏡像に見せる(矢印も鏡)。本体の MeshRenderer は enabled のまま forceRenderingOff で隠す
//     (切断のメッシュ・光の保護・当たりの矩形は本体の立方体のまま使える。enabled を落とすと「見えないノーツ」として保護から外れる)。
//   HalfA・HalfB: 本体を割れ目で切った2片(材質は本体と同じなので、光の強弱・ミスの暗さもそのまま付いてくる)。
//     メッシュは割れ目の向き 8 種 × 2 片を最初に1回だけ MeshSlicer で作り、全ノーツで共有する。VisualTransform の MeshFilter には入れない
//     (TrySpawnSlices が半分だけを切ってしまうため)。
//   破片(最大6個、芯の材質)と、閉じる瞬間の割れ目の光(ノーツの上の小さな光。画面の光の予算は使わない)。
// プールは R のノーツとほかのノーツで分ける(NoteSpawner.Key)。出現のたびに ResetReverse で、ずれ・負の拡縮・すき間・破片を必ず戻す。
public partial class CuttableNote
{
    Transform reverseVisual;
    MeshRenderer reverseBody;
    MeshFilter reverseBodyFilter;
    Transform reverseHalfA, reverseHalfB;
    MeshFilter reverseHalfAFilter, reverseHalfBFilter;
    MeshRenderer reverseHalfARenderer, reverseHalfBRenderer;
    readonly List<Transform> reverseShards = new List<Transform>();
    readonly List<MeshRenderer> reverseShardRenderers = new List<MeshRenderer>();
    Transform reverseSeam;
    MeshRenderer reverseSeamRenderer;
    MaterialPropertyBlock reverseSeamBlock;
    bool reverseHalvesReady;
    int reverseSeed;
    static readonly int ReverseSeamColorId = Shader.PropertyToID("_BaseColor");
    static Material reverseSeamMaterial;
    // 割れた2片のメッシュ(元のメッシュごと、割れ目の向き 8 種 × 2 片)。
    static readonly Dictionary<Mesh, Mesh[]> reverseHalfMeshes = new Dictionary<Mesh, Mesh[]>();

    // この出現が R の見た目(2片・鏡)を使うか。
    public bool HasReverse { get; private set; }
    // 鏡門のノーツ(門を通るまで左右の鏡像の側に見える)。
    public bool IsMirror { get; private set; }
    // 出現時に決めた、すき間(ワールド)・破片の数・渦・割れ目の向き(ReverseRules.CrackIndex)。
    public float ReverseGap { get; private set; }
    public int ReverseShardCount { get; private set; }
    public float ReverseSpin { get; private set; }
    public int ReverseCrack { get; private set; }
    // 今のコマの見え方(テスト用)。
    public float ReverseGapShown { get; private set; }
    public int ReverseShardsShown { get; private set; }
    public float ReverseSeamShown { get; private set; }
    public bool ReverseHalvesShown => reverseHalfA != null && reverseHalfA.gameObject.activeSelf;
    public Transform ReverseHalfA => reverseHalfA;
    public Transform ReverseHalfB => reverseHalfB;
    public IReadOnlyList<Transform> ReverseShards => reverseShards;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetReverseMeshes()
    {
        foreach (var pair in reverseHalfMeshes)
            if (pair.Value != null)
                foreach (var mesh in pair.Value) if (mesh != null) UISkinKit.SafeDestroy(mesh);
        reverseHalfMeshes.Clear();
        reverseSeamMaterial = null;
    }

    // 本体のメッシュを割れ目 index で2つに切った片(A は ReverseRules.CrackNormal の側)。最初に使うときに1回だけ作り、全ノーツで共有する。
    // 読めないメッシュ・切れなかったときは false(本体をそのまま描く)。
    public static bool TryReverseHalves(Mesh source, int index, out Mesh first, out Mesh second)
    {
        first = second = null;
        if (source == null || !source.isReadable) return false;
        index = ((index % ReverseRules.CrackAngleCount) + ReverseRules.CrackAngleCount) % ReverseRules.CrackAngleCount;
        if (!reverseHalfMeshes.TryGetValue(source, out var halves) || halves == null)
            reverseHalfMeshes[source] = halves = new Mesh[ReverseRules.CrackAngleCount * 2];
        if (halves[index * 2] == null || halves[index * 2 + 1] == null)
        {
            Vector2 normal = ReverseRules.CrackNormal(index);
            var plane = new Plane(new Vector3(normal.x, normal.y, 0f), source.bounds.center);
            var above = new Mesh { name = "ReverseHalfA" + index, hideFlags = HideFlags.DontSave };
            var below = new Mesh { name = "ReverseHalfB" + index, hideFlags = HideFlags.DontSave };
            if (!MeshSlicer.SliceInto(source, plane, above, below))
            {
                UISkinKit.SafeDestroy(above);
                UISkinKit.SafeDestroy(below);
                return false;
            }
            halves[index * 2] = above;
            halves[index * 2 + 1] = below;
        }
        first = halves[index * 2];
        second = halves[index * 2 + 1];
        return true;
    }

    // 本体の描画先を子へ移す(1回だけ)。再利用時に足された縁・矢印・ひびも毎回拾う。着地の枠は根元の兄弟なので含まれない。
    internal void EnsureReverseVisual()
    {
        if (reverseVisual == null)
        {
            var source = GetComponent<MeshRenderer>();
            var sourceMesh = GetComponent<MeshFilter>();
            var go = new GameObject("ReverseVisual", typeof(MeshFilter), typeof(MeshRenderer));
            go.layer = gameObject.layer;
            reverseVisual = go.transform;
            reverseVisual.SetParent(transform, false);
            reverseBody = go.GetComponent<MeshRenderer>();
            reverseBodyFilter = go.GetComponent<MeshFilter>();
            if (sourceMesh != null) reverseBodyFilter.sharedMesh = sourceMesh.sharedMesh;
            if (source != null)
            {
                // メッシュと材質の所有者は従来のまま。描画先だけを子へ移す。
                CopyReverseRenderer(source, reverseBody);
                reverseBody.enabled = source.enabled;
                var properties = new MaterialPropertyBlock();
                source.GetPropertyBlock(properties);
                reverseBody.SetPropertyBlock(properties);
                var visuals = Visuals != null ? Visuals : GetComponent<NoteVisuals>();
                if (visuals != null) visuals.MoveBodyRenderer(source, reverseBody);
                source.enabled = false;
            }
            else reverseBody.enabled = false;
            reverseHalfA = MakeReversePart("HalfA", out reverseHalfAFilter, out reverseHalfARenderer);
            reverseHalfB = MakeReversePart("HalfB", out reverseHalfBFilter, out reverseHalfBRenderer);
        }

        var children = new List<Transform>();
        foreach (Transform child in transform)
            if (child != reverseVisual && child.GetComponentInChildren<Renderer>(true) != null)
                children.Add(child);
        foreach (var child in children) child.SetParent(reverseVisual, false);
        reverseVisual.gameObject.SetActive(true);
    }

    Transform MakeReversePart(string name, out MeshFilter filter, out MeshRenderer renderer)
    {
        var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
        go.layer = gameObject.layer;
        go.transform.SetParent(reverseVisual, false);
        filter = go.GetComponent<MeshFilter>();
        renderer = go.GetComponent<MeshRenderer>();
        if (reverseBody != null) CopyReverseRenderer(reverseBody, renderer);
        go.SetActive(false);
        return go.transform;
    }

    static void CopyReverseRenderer(MeshRenderer source, MeshRenderer target)
    {
        target.sharedMaterials = source.sharedMaterials;
        target.shadowCastingMode = source.shadowCastingMode;
        target.receiveShadows = source.receiveShadows;
        target.lightProbeUsage = source.lightProbeUsage;
        target.reflectionProbeUsage = source.reflectionProbeUsage;
        target.probeAnchor = source.probeAnchor;
        target.motionVectorGenerationMode = source.motionVectorGenerationMode;
        target.renderingLayerMask = source.renderingLayerMask;
        target.sortingLayerID = source.sortingLayerID;
        target.sortingOrder = source.sortingOrder;
    }

    // 出現時に1回(NoteSpawner.PrepareReverseNote)。見せ場の区間・割れ目の向き・鏡は出現の時点で決め、飛行中に表を読み直さない。
    internal void ConfigureReverse(int crack, int shards, float gap, float spin, bool mirror, int seed)
    {
        EnsureReverseVisual();
        HasReverse = true;
        IsMirror = mirror;
        ReverseCrack = ((crack % ReverseRules.CrackAngleCount) + ReverseRules.CrackAngleCount) % ReverseRules.CrackAngleCount;
        ReverseShardCount = Mathf.Clamp(shards, 0, ReverseRules.MaxShards);
        ReverseGap = float.IsNaN(gap) || float.IsInfinity(gap) ? 0f : Mathf.Clamp(gap, 0f, ReverseRules.MaxGap);
        ReverseSpin = float.IsNaN(spin) || float.IsInfinity(spin) ? 0f : spin;
        reverseSeed = seed;

        // 2片のメッシュ(割れ目の向き)と材質(本体と同じ)。並び順も本体にそろえる。
        Mesh first = null, second = null;
        reverseHalvesReady = reverseBodyFilter != null
            && TryReverseHalves(reverseBodyFilter.sharedMesh, ReverseCrack, out first, out second);
        if (reverseHalvesReady)
        {
            reverseHalfAFilter.sharedMesh = first;
            reverseHalfBFilter.sharedMesh = second;
        }
        if (reverseBody != null)
        {
            CopyReverseRenderer(reverseBody, reverseHalfARenderer);
            CopyReverseRenderer(reverseBody, reverseHalfBRenderer);
        }
        EnsureReverseShards(ReverseShardCount);
        EnsureReverseSeam();
        // 割れ目の線に沿った細い光(縦の細い板を割れ目の角度だけ右回りに倒す)。
        reverseSeam.localRotation = Quaternion.Euler(0f, 0f, -ReverseRules.CrackDegrees(ReverseCrack));
    }

    void EnsureReverseShards(int count)
    {
        if (count <= reverseShards.Count) return;
        Material material = ReverseShardMaterial();
        while (reverseShards.Count < count)
        {
            var go = new GameObject("ReverseShard" + reverseShards.Count, typeof(MeshFilter), typeof(MeshRenderer));
            go.layer = gameObject.layer;
            go.transform.SetParent(reverseVisual, false);
            go.GetComponent<MeshFilter>().sharedMesh = NoteFragmentPool.CubeMesh;
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            if (reverseBody != null) { renderer.sortingLayerID = reverseBody.sortingLayerID; renderer.sortingOrder = reverseBody.sortingOrder; }
            go.SetActive(false);
            reverseShards.Add(go.transform);
            reverseShardRenderers.Add(renderer);
        }
    }

    // 破片は芯の材質(矢印のノーツは芯が無いので縁、無ければ本体)。材質の持ち主は NoteVisuals のまま。
    Material ReverseShardMaterial()
    {
        foreach (string name in new[] { "InnerCore", "EdgeRails", "EdgeTop" })
        {
            var child = reverseVisual != null ? reverseVisual.Find(name) : null;
            var renderer = child != null ? child.GetComponent<MeshRenderer>() : null;
            if (renderer != null && renderer.sharedMaterial != null) return renderer.sharedMaterial;
        }
        return reverseBody != null ? reverseBody.sharedMaterial : null;
    }

    // 割れ目の光の材質(全ノーツで共有。色と濃さはノーツごとの MaterialPropertyBlock)。矢印(3021〜)より先に描く。
    static Material SeamMaterial
    {
        get
        {
            if (reverseSeamMaterial != null) return reverseSeamMaterial;
            var shader = Resources.Load<Shader>("Effects/NoteGuide");
            if (shader != null)
                reverseSeamMaterial = new Material(shader) { name = "ReverseSeam", hideFlags = HideFlags.DontSave, renderQueue = 3019 };
            return reverseSeamMaterial;
        }
    }

    void EnsureReverseSeam()
    {
        if (reverseSeam != null) return;
        var go = new GameObject("ReverseSeam", typeof(MeshFilter), typeof(MeshRenderer));
        go.layer = gameObject.layer;
        reverseSeam = go.transform;
        reverseSeam.SetParent(reverseVisual, false);
        // 本体(半幅 0.5)の前面(z −0.5)のすぐ手前。矢印(z −0.57)より奥。
        reverseSeam.localPosition = new Vector3(0f, 0f, -.52f);
        reverseSeam.localScale = new Vector3(.05f, 1.02f, .01f);
        go.GetComponent<MeshFilter>().sharedMesh = NoteFragmentPool.CubeMesh;
        reverseSeamRenderer = go.GetComponent<MeshRenderer>();
        reverseSeamRenderer.sharedMaterial = SeamMaterial;
        reverseSeamRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        reverseSeamRenderer.receiveShadows = false;
        reverseSeamBlock = new MaterialPropertyBlock();
        go.SetActive(false);
    }

    // 毎コマ(NoteSpawner.ApplyReverse)。gap は2片のすき間(ワールド)、shardReach は破片の中心からの距離(ReverseRules.ShardReach、負は出さない)、
    // mirrorOffsetX・mirrorScaleX は鏡の見た目の横のずれ(ワールド)と拡縮、seam は割れ目の光(0〜1)。
    internal void SetReverse(float gap, float shardReach, float mirrorOffsetX, float mirrorScaleX, float seam)
    {
        if (!HasReverse || reverseVisual == null) return;
        // 鏡: 見た目の子だけを本当の x の鏡像へ。拡縮・傾きのある出現中でも、ずれはワールドの横方向にそろえる。
        reverseVisual.localPosition = mirrorOffsetX == 0f ? Vector3.zero
            : transform.InverseTransformVector(new Vector3(mirrorOffsetX, 0f, 0f));
        reverseVisual.localScale = new Vector3(mirrorScaleX, 1f, 1f);

        bool open = reverseHalvesReady && !IsCut && gap > ReverseRules.ClosedGap;
        ReverseGapShown = open ? gap : 0f;
        if (reverseBody != null) reverseBody.forceRenderingOff = open;
        SetReverseActive(reverseHalfA, open);
        SetReverseActive(reverseHalfB, open);
        if (open)
        {
            Vector2 normal = ReverseRules.CrackNormal(ReverseCrack);
            var local = new Vector3(normal.x, normal.y, 0f);
            // 子の座標の1は、ワールドではこの長さ(鏡の負の拡縮・押しつぶれも含む)。
            float length = reverseVisual.TransformVector(local).magnitude;
            Vector3 offset = length > 1e-5f ? local * (gap * .5f / length) : Vector3.zero;
            reverseHalfA.localPosition = offset;
            reverseHalfB.localPosition = -offset;
        }

        int shown = 0;
        bool shards = shardReach >= 0f && !IsCut && !IsMissed;
        Vector3 lossy = reverseVisual.lossyScale;
        for (int i = 0; i < reverseShards.Count; i++)
        {
            bool visible = shards && i < ReverseShardCount;
            SetReverseActive(reverseShards[i], visible);
            if (!visible) continue;
            Vector2 world = ReverseRules.ShardOffset(i, ReverseShardCount, shardReach, ReverseSpin, reverseSeed);
            var shard = reverseShards[i];
            shard.localPosition = reverseVisual.InverseTransformVector(new Vector3(world.x, world.y, 0f));
            float size = ReverseRules.ShardSize * ReverseRules.ShardScale(shardReach);
            shard.localScale = new Vector3(size / Mathf.Max(1e-4f, Mathf.Abs(lossy.x)),
                size / Mathf.Max(1e-4f, Mathf.Abs(lossy.y)), size / Mathf.Max(1e-4f, Mathf.Abs(lossy.z)));
            float turn = 1f - shardReach;
            shard.localRotation = Quaternion.Euler(35f + 240f * turn, 25f + 47f * i, 160f * turn + 360f * ReverseSpin * turn);
            shown++;
        }
        ReverseShardsShown = shown;

        var seamMaterial = seam > 0f ? SeamMaterial : null;
        bool lit = seam > 0f && !IsCut && !IsMissed && reverseSeamRenderer != null && seamMaterial != null;
        ReverseSeamShown = lit ? seam : 0f;
        if (reverseSeam != null) SetReverseActive(reverseSeam, lit);
        if (lit)
        {
            if (reverseSeamRenderer.sharedMaterial != seamMaterial) reverseSeamRenderer.sharedMaterial = seamMaterial;
            reverseSeamBlock.SetColor(ReverseSeamColorId, new Color(1f, 1f, 1f, Mathf.Clamp01(seam)));
            reverseSeamRenderer.SetPropertyBlock(reverseSeamBlock);
        }
    }

    // 出現のたびに(ResetForSpawn)。2片・破片・光を隠し、ずれと負の拡縮を戻し、本体を描く。子の作りはそのまま再利用する。
    internal void ResetReverse()
    {
        HasReverse = false;
        IsMirror = false;
        ReverseGap = ReverseSpin = 0f;
        ReverseShardCount = ReverseCrack = 0;
        ReverseGapShown = ReverseSeamShown = 0f;
        ReverseShardsShown = 0;
        reverseSeed = 0;
        if (reverseVisual == null) return;
        reverseVisual.localPosition = Vector3.zero;
        reverseVisual.localScale = Vector3.one;
        if (reverseBody != null) reverseBody.forceRenderingOff = false;
        SetReverseActive(reverseHalfA, false);
        SetReverseActive(reverseHalfB, false);
        foreach (var shard in reverseShards) SetReverseActive(shard, false);
        SetReverseActive(reverseSeam, false);
    }

    static void SetReverseActive(Transform target, bool active)
    {
        if (target != null && target.gameObject.activeSelf != active) target.gameObject.SetActive(active);
    }
}
