using UnityEngine;

// ターゲットの世界位置に固定オフセットで追従する。
// 親子関係を持たないので、親の scale 変動（ロングノーツの Z 拡張など）に引きずられない。
// ターゲットが破棄されたら自分も消える。
public class FollowTransformWorldOffset : MonoBehaviour
{
    public Transform target;
    public Vector3 worldOffset;

    void LateUpdate()
    {
        if (target == null)
        {
            if (Application.isPlaying) Destroy(gameObject);
            else DestroyImmediate(gameObject);
            return;
        }
        transform.position = target.position + worldOffset;
    }
}
