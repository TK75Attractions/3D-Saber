using UnityEngine;

// 切断後のメッシュ片を一定時間後に消す。フェードアウトも任意でかける。
public class SlicePieceDecay : MonoBehaviour
{
    public float life = 1.2f;
    public float fadeStart = 0.6f;
    private float age;
    private MeshRenderer mr;
    private Material runtimeMat;

    void Awake()
    {
        mr = GetComponent<MeshRenderer>();
    }

    void Update()
    {
        age += Time.deltaTime;
        float fadeRange = Mathf.Max(0.0001f, life - fadeStart);
        float alpha = Mathf.Clamp01(1f - (age - fadeStart) / fadeRange);
        if (mr != null && age > fadeStart)
        {
            if (runtimeMat == null)
            {
                runtimeMat = mr.material;
            }
            if (runtimeMat.HasProperty("_BaseColor"))
            {
                Color c = runtimeMat.GetColor("_BaseColor");
                c.a = alpha;
                runtimeMat.SetColor("_BaseColor", c);
            }
            else if (runtimeMat.HasProperty("_Color"))
            {
                Color c = runtimeMat.color;
                c.a = alpha;
                runtimeMat.color = c;
            }
        }
        if (age >= life) Destroy(gameObject);
    }
}
