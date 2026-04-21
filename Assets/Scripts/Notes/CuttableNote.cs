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
    public float pieceLife = 1.2f;
    public float pieceAngularImpulse = 8f;

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

    public void MarkMiss()
    {
        if (IsCut || IsMissed) return;
        IsMissed = true;
        OnMiss?.Invoke(this);
        gameObject.SetActive(false);
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

        // 切断面の法線：XY 平面内で進行方向に垂直（= 刃が描く面の法線）
        Vector3 cutNormalWorld = Vector3.Cross(motionDir, Vector3.forward).normalized;

        // メッシュローカルへ変換
        Vector3 hitLocal = transform.InverseTransformPoint(hitPoint);
        Vector3 normalLocal = transform.InverseTransformDirection(cutNormalWorld).normalized;
        Plane planeLocal = new Plane(normalLocal, hitLocal);

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
        rb.linearVelocity = launchVel;
        rb.angularVelocity = Random.insideUnitSphere * pieceAngularImpulse;

        var decay = go.AddComponent<SlicePieceDecay>();
        decay.life = pieceLife;
    }
}
