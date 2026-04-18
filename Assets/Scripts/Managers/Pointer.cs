using UnityEngine;

public class PlayerController : MonoBehaviour
{

    void Update()
    {
        // InputPointが存在しない場合は何もしない
        if (InputPoint.Instance == null) return;

        // 正規化座標を取得（-1〜1）
        Vector2 inputPos = InputPoint.Instance.NormalizedPosition;

        // そのまま座標として反映
        transform.position = new Vector3(
            inputPos.x ,
            inputPos.y ,
            0f
        );
    }
}