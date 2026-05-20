using UnityEngine;

// 切れるノーツ。タップ・方向指定・ロングをすべてこのコンポーネントで扱う。
// ロングノーツは Cut() を RequiredCutCount 回呼ぶことで完了する。
// 部分カット時は「ひび」を表示し、最終カットで本体をスライス＋砕け散らせる。
public class CuttableNote : MonoBehaviour
{
    public bool IsCut { get; private set; }
    public bool IsJudgeable { get; set; }
    public bool IsMissed { get; private set; }
    public double HitTime { get; set; }

    [Header("Note kind")]
    public CutDirection RequiredDirection = CutDirection.None;
    public int RequiredCutCount = 1;
    public int RemainingCuts = 1;
    // 通算で1回でも誤方向に切ったら false。
    public bool LastCutCorrectDirection { get; private set; } = true;
    // 何回切れたか（達成数）。
    public int CutsAchieved => RequiredCutCount - RemainingCuts;
    public bool IsFinalized { get; private set; }

    // ロングノーツ用の残数表示（TMP）。NoteSpawner が割り付ける。
    public TMPro.TextMeshPro countLabel;

    [Header("Slice physics")]
    public float sliceSeparationImpulse = 2.5f;
    public float saberVelocityScale = 0.35f;
    public float pieceLife = 2.8f;
    public float pieceFadeStart = 1.8f;
    public float pieceAngularImpulse = 4f;
    public bool pieceUseGravity = false;

    [Header("Shatter (long note)")]
    public int shatterDebrisCount = 6;
    public float shatterDebrisSpeed = 3f;

    // 最終カット時に発火（タップなら1回、ロングなら全部切れた瞬間に1回）。
    public event System.Action<CuttableNote, Vector3, Vector3> OnCut;
    // 0回も切れずタイムアウトしたときに発火。部分達成のロングは OnCut（達成率付き）で扱う。
    public event System.Action<CuttableNote> OnMiss;
    // 1カットごとに発火（cutIndex は 0 から始まる達成番号、total は必要回数）。
    // ロングの上行音 SFX 等、各打鍵に紐づく演出に使う。
    public event System.Action<CuttableNote, int, int> OnPartialCut;

    private Vector3 lastHitPoint;
    private Vector3 lastVelocity = Vector3.right;

    public void Cut(Vector3 hitPoint, Vector3 cutVelocity)
    {
        Cut(hitPoint, cutVelocity, CutDirection.None);
    }

    // imuHint：IMU 由来の振り検知方向（無ければ None）。velocity か imuHint のどちらかで一致すれば OK。
    public void Cut(Vector3 hitPoint, Vector3 cutVelocity, CutDirection imuHint)
    {
        if (IsCut || IsMissed || IsFinalized) return;

        // 方向判定（1回でも誤方向なら以降 false 維持）
        Vector2 vXY = new Vector2(cutVelocity.x, cutVelocity.y);
        bool dirOk = CutDirectionHelper.MatchesWithHint(RequiredDirection, vXY, imuHint);
        if (CutsAchieved == 0) LastCutCorrectDirection = dirOk;
        else LastCutCorrectDirection = LastCutCorrectDirection && dirOk;

        RemainingCuts--;
        lastHitPoint = hitPoint;
        lastVelocity = cutVelocity;

        // 各カットでひびを追加＆数字を更新
        AddCrack();
        UpdateCountLabel();

        // 各カットごとに発火（達成番号は 0 始まり）
        OnPartialCut?.Invoke(this, CutsAchieved - 1, RequiredCutCount);

        if (RemainingCuts <= 0)
        {
            // 全カット完了：本体を砕け散らせる
            IsCut = true;
            IsFinalized = true;
            OnCut?.Invoke(this, hitPoint, cutVelocity);
            ShatterAndDestroy(hitPoint, cutVelocity);
        }
    }

    // 判定窓を逃した時のフラグ。
    // 部分達成があった場合はスコア用に OnCut を発火（達成率は ScoreManager 側が読む）。
    public void MarkMiss()
    {
        if (IsCut || IsMissed || IsFinalized) return;
        IsMissed = true;
        IsJudgeable = false;
        IsFinalized = true;
        DimVisual();

        if (CutsAchieved > 0)
        {
            // 部分達成 → ScoreManager で完了率を見て tier を下げる
            OnCut?.Invoke(this, lastHitPoint, lastVelocity);
        }
        else
        {
            OnMiss?.Invoke(this);
        }
    }

    private void UpdateCountLabel()
    {
        if (countLabel == null) return;
        if (RemainingCuts <= 0) countLabel.gameObject.SetActive(false);
        else countLabel.text = RemainingCuts.ToString();
    }

    private void DimVisual()
    {
        var mr = GetComponent<MeshRenderer>();
        if (mr == null || mr.material == null) return;
        Color c;
        if (mr.material.HasProperty("_BaseColor")) c = mr.material.GetColor("_BaseColor");
        else c = mr.material.color;
        c = new Color(c.r * 0.4f, c.g * 0.4f, c.b * 0.4f, c.a);
        if (mr.material.HasProperty("_BaseColor")) mr.material.SetColor("_BaseColor", c);
        else mr.material.color = c;
        if (mr.material.HasProperty("_EmissionColor")) mr.material.SetColor("_EmissionColor", c * 0.2f);
    }

    private void AddCrack()
    {
        // フロント面（-Z 側）にランダムな細い裂け目を1本追加。
        GameObject crack = GameObject.CreatePrimitive(PrimitiveType.Cube);
        crack.name = "Crack";
        crack.transform.SetParent(transform, false);
        // 端まで届く長さ・ランダム角度・ランダム位置
        crack.transform.localPosition = new Vector3(
            Random.Range(-0.3f, 0.3f),
            Random.Range(-0.3f, 0.3f),
            -0.55f);
        crack.transform.localRotation = Quaternion.Euler(0, 0, Random.Range(0f, 360f));
        crack.transform.localScale = new Vector3(0.04f, Random.Range(0.5f, 0.95f), 0.03f);
        var col = crack.GetComponent<BoxCollider>();
        if (col != null) SafeDestroy(col);
        var sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var mat = new Material(sh);
        Color c = Color.white;
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
        else mat.color = c;
        if (mat.HasProperty("_EmissionColor"))
        {
            mat.EnableKeyword("_EMISSION");
            mat.SetColor("_EmissionColor", c * 1.4f);
        }
        crack.GetComponent<MeshRenderer>().sharedMaterial = mat;
    }

    private void ShatterAndDestroy(Vector3 hitPoint, Vector3 cutVelocity)
    {
        bool sliced = TrySpawnSlices(hitPoint, cutVelocity);
        if (RequiredCutCount > 1)
        {
            SpawnDebris(cutVelocity);
        }
        if (sliced)
        {
            SafeDestroyGo(gameObject);
        }
        else
        {
            gameObject.SetActive(false);
        }
    }

    private static void SafeDestroy(Object o)
    {
        if (o == null) return;
        if (Application.isPlaying) Destroy(o);
        else DestroyImmediate(o);
    }

    private static void SafeDestroyGo(GameObject go)
    {
        if (go == null) return;
        if (Application.isPlaying) Destroy(go);
        else DestroyImmediate(go);
    }

    private void SpawnDebris(Vector3 cutVelocity)
    {
        var srcMr = GetComponent<MeshRenderer>();
        Material mat = srcMr != null ? srcMr.sharedMaterial : null;
        for (int i = 0; i < shatterDebrisCount; i++)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name + "_debris";
            var col = go.GetComponent<BoxCollider>();
            if (col != null) SafeDestroy(col);
            go.transform.position = transform.position + Random.insideUnitSphere * 0.3f;
            go.transform.rotation = Random.rotation;
            go.transform.localScale = Vector3.one * Random.Range(0.06f, 0.14f);
            var rb = go.AddComponent<Rigidbody>();
            rb.useGravity = pieceUseGravity;
            rb.linearVelocity = Random.insideUnitSphere * shatterDebrisSpeed + cutVelocity * 0.15f;
            rb.angularVelocity = Random.insideUnitSphere * (pieceAngularImpulse * 1.5f);
            var decay = go.AddComponent<SlicePieceDecay>();
            decay.life = pieceLife;
            decay.fadeStart = pieceFadeStart;
            // 親由来マテリアルを複製して破片に渡す（親破棄後もマゼンタにならないように）
            if (mat != null) decay.SetOwnedMaterial(new Material(mat));
        }
    }

    private bool TrySpawnSlices(Vector3 hitPoint, Vector3 cutVelocity)
    {
        MeshFilter mf = GetComponent<MeshFilter>();
        MeshRenderer mr = GetComponent<MeshRenderer>();
        if (mf == null || mr == null || mf.sharedMesh == null) return false;

        Vector3 motionDir = cutVelocity;
        motionDir.z = 0f;
        if (motionDir.sqrMagnitude < 0.0001f) return false;
        motionDir.Normalize();

        Vector3 cutNormalWorld = Vector3.Cross(motionDir, Vector3.forward).normalized;
        Vector3 hitLocal = transform.InverseTransformPoint(hitPoint);
        Vector3 normalLocal = transform.InverseTransformDirection(cutNormalWorld).normalized;

        Bounds b = mf.sharedMesh.bounds;
        float halfExtent = Mathf.Max(b.extents.x, Mathf.Max(b.extents.y, b.extents.z)) * 0.7f;
        float signed = Vector3.Dot(hitLocal - b.center, normalLocal);
        signed = Mathf.Clamp(signed, -halfExtent, halfExtent);
        Vector3 planePoint = b.center + normalLocal * signed;
        Plane planeLocal = new Plane(normalLocal, planePoint);

        if (!MeshSlicer.Slice(mf.sharedMesh, planeLocal, out Mesh above, out Mesh below))
        {
            return false;
        }

        Vector3 separationWorld = cutNormalWorld * sliceSeparationImpulse
                                  + cutVelocity * saberVelocityScale;
        SpawnPiece(above, mr.sharedMaterial, separationWorld);
        SpawnPiece(below, mr.sharedMaterial, -separationWorld);
        return true;
    }

    private void SpawnPiece(Mesh mesh, Material mat, Vector3 launchVel)
    {
        var go = new GameObject(name + "_piece");
        go.transform.position = transform.position;
        go.transform.rotation = transform.rotation;
        go.transform.localScale = transform.lossyScale;

        var mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();

        var col = go.AddComponent<MeshCollider>();
        col.convex = true;
        col.sharedMesh = mesh;

        var rb = go.AddComponent<Rigidbody>();
        rb.mass = 0.2f;
        rb.linearDamping = 0.1f;
        rb.angularDamping = 0.2f;
        rb.useGravity = pieceUseGravity;
        rb.linearVelocity = launchVel;
        rb.angularVelocity = Random.insideUnitSphere * pieceAngularImpulse;

        var decay = go.AddComponent<SlicePieceDecay>();
        decay.life = pieceLife;
        decay.fadeStart = pieceFadeStart;
        // 親由来マテリアルを複製してスライス片に渡す（親破棄後もマゼンタにならないように）
        if (mat != null) decay.SetOwnedMaterial(new Material(mat));
    }
}
