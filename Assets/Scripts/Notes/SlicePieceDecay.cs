using UnityEngine;

// 切断後のメッシュ片を一定時間後に消す。フェードアウトも任意でかける。
// 親ノーツが破棄されると元マテリアルも破棄されるため、ここで「自前のマテリアル」を保持する。
// CuttableNote.SpawnPiece/SpawnDebris から SetOwnedMaterial で渡される想定。
public class SlicePieceDecay : MonoBehaviour
{
    public float life = 1.2f;
    public float fadeStart = 0.6f;
    private float age;
    private MeshRenderer mr;
    private Material ownedMat;

    void Awake()
    {
        mr = GetComponent<MeshRenderer>();
    }

    void OnDestroy()
    {
        if (ownedMat != null)
        {
            if (Application.isPlaying) Destroy(ownedMat);
            else DestroyImmediate(ownedMat);
            ownedMat = null;
        }
    }

    // 親（CuttableNote）由来のマテリアルから複製を作って渡す呼び出し点用。
    // mr.sharedMaterial にも同時に設定する。
    public void SetOwnedMaterial(Material instanceCopy)
    {
        ownedMat = instanceCopy;
        if (mr == null) mr = GetComponent<MeshRenderer>();
        if (mr != null) mr.sharedMaterial = ownedMat;
    }

    void Update()
    {
        age += Time.deltaTime;
        float fadeRange = Mathf.Max(0.0001f, life - fadeStart);
        float alpha = Mathf.Clamp01(1f - (age - fadeStart) / fadeRange);
        if (mr != null && age > fadeStart && ownedMat != null)
        {
            if (ownedMat.HasProperty("_BaseColor"))
            {
                Color c = ownedMat.GetColor("_BaseColor");
                c.a = alpha;
                ownedMat.SetColor("_BaseColor", c);
            }
            else if (ownedMat.HasProperty("_Color"))
            {
                Color c = ownedMat.color;
                c.a = alpha;
                ownedMat.color = c;
            }
        }
        if (age >= life) Destroy(gameObject);
    }
}
