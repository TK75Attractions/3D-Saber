using UnityEngine;

// 棒1（InputPoint.Stick1）の Canvas 座標を 3D ワールド XY に変換してセーバー位置に反映。
// InputPoint が無い／データ未到着の場合はマウスフォールバック（任意）。
public class SaberInputBridge : MonoBehaviour
{
    [Header("Source")]
    public bool useInputPoint = true;
    public bool fallbackToMouse = true;
    // UDP データがこの秒数以上止まったら、マウスフォールバックに自動で切り替える。
    public float inputPointStaleSeconds = 1.0f;
    public Camera targetCamera;

    [Header("Coordinate")]
    // 譜面ソフトと同じスケール（pixel → world）。判定面 11×6 / ボード 1920×1080 なら ≒0.00573。
    public float pixelsToWorld = 0.00573f;
    public float fixedZ = 0f;
    public bool clampToBounds = true;
    public Vector2 minBounds = new Vector2(-5.5f, -3f);
    public Vector2 maxBounds = new Vector2(5.5f, 3f);

    void Awake()
    {
        if (targetCamera == null) targetCamera = Camera.main;
    }

    void Update()
    {
        // UDP データが「最近」来てるなら、それを使う。1 秒以上無音ならマウスフォールバック。
        if (useInputPoint && InputPoint.Instance != null
            && InputPoint.Instance.IsRecentlyActive(inputPointStaleSeconds))
        {
            Vector2 pixel = InputPoint.Instance.LocalPosition;
            Vector2 worldXY = pixel * pixelsToWorld;
            ApplyPosition(worldXY);
            return;
        }
        if (fallbackToMouse)
        {
            if (targetCamera == null) targetCamera = Camera.main;
            if (targetCamera == null) return;
            Vector2 mouseScreen = MouseSaberInput.ReadMouseScreen();
            if (MouseSaberInput.TryProjectToPlane(mouseScreen, fixedZ, targetCamera, out Vector3 world))
            {
                ApplyPosition(new Vector2(world.x, world.y));
            }
        }
    }

    private void ApplyPosition(Vector2 worldXY)
    {
        if (clampToBounds)
        {
            worldXY.x = Mathf.Clamp(worldXY.x, minBounds.x, maxBounds.x);
            worldXY.y = Mathf.Clamp(worldXY.y, minBounds.y, maxBounds.y);
        }
        transform.position = new Vector3(worldXY.x, worldXY.y, fixedZ);
    }

    // テスト用に純関数化。
    public Vector3 ComputeWorld(Vector2 pixel)
    {
        Vector2 worldXY = pixel * pixelsToWorld;
        if (clampToBounds)
        {
            worldXY.x = Mathf.Clamp(worldXY.x, minBounds.x, maxBounds.x);
            worldXY.y = Mathf.Clamp(worldXY.y, minBounds.y, maxBounds.y);
        }
        return new Vector3(worldXY.x, worldXY.y, fixedZ);
    }
}
