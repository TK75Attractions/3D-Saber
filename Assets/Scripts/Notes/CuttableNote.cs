using UnityEngine;

// 切れるノーツ。Cut() で実際にメッシュを切断して両半分を物理で飛ばす。
public class CuttableNote : MonoBehaviour
{
    public bool IsCut { get; private set; }
    public bool IsJudgeable { get; set; }
    public bool IsMissed { get; private set; }
    public double HitTime { get; set; }

    [Header("Slice physics")]
    public float sliceSeparationImpulse = 2.5f;
    public float saberVelocityScale = 0.35f;
    public float pieceLife = 2.8f;
    public float pieceFadeStart = 1.8f;
    public float pieceAngularImpulse = 4f;
    public bool pieceUseGravity = false;

    public event System.Action<CuttableNote, Vector3, Vector3> OnCut;
    public event System.Action<CuttableNote> OnMiss;

    public void Cut(Vector3 hitPoint, Vector3 cutVelocity)
    {
        if (IsCut || IsMissed) return;
        IsCut = true;
        OnCut?.Invoke(this, hitPoint, cutVelocity);

        if (!TrySpawnSlices(hitPoint, cutVelocity))
        {
            gameObject.SetActive(false);
            return;
        }
        Destroy(gameObject);
    }

    // 判定窓を逃した時のフラグ立て。視覚的には消さず、そのまま後ろに流す。
    public void MarkMiss()
    {
        if (IsCut || IsMissed) return;
        IsMissed = true;
        IsJudgeable = false;
        // 見た目だけ「失敗した」と分かるよう色を落とす
        var mr = GetComponent<MeshRenderer>();
        if (mr != null && mr.material != null)
        {
            Color c;
            if (mr.material.HasProperty("_BaseColor")) c = mr.material.GetColor("_BaseColor");
            else c = mr.material.color;
            c = new Color(c.r * 0.4f, c.g * 0.4f, c.b * 0.4f, c.a);
            if (mr.material.HasProperty("_BaseColor")) mr.material.SetColor("_BaseColor", c);
            else mr.material.color = c;
            if (mr.material.HasProperty("_EmissionColor")) mr.material.SetColor("_EmissionColor", c * 0.2f);
        }
        OnMiss?.Invoke(this);
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

        // 切断面の法線：XY 平面内で進行方向に垂直
        Vector3 cutNormalWorld = Vector3.Cross(motionDir, Vector3.forward).normalized;

        // メッシュローカルへ変換
        Vector3 hitLocal = transform.InverseTransformPoint(hitPoint);
        Vector3 normalLocal = transform.InverseTransformDirection(cutNormalWorld).normalized;

        // セーバーの通過点がノーツの外側のことが多いので、
        // 平面をノーツのバウンディング内に寄せる（必ず切断が成立するように）。
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
        mr.sharedMaterial = mat;

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
    }
}
