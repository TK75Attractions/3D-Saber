using UnityEngine;

// セーバーポインタの滞留時間をボタン単位で上書きする設定。
public class SaberDwellTarget : MonoBehaviour
{
    [Min(0.05f)] public float dwellSeconds = 1f;
    public Color progressColor = Color.cyan;
}
