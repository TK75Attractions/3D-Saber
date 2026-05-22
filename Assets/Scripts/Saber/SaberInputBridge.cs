using UnityEngine;

// 棒1（InputPoint.Stick1）の Canvas 座標を 3D ワールド XY に変換してセーバー位置に反映。
// InputPoint が無い／データ未到着の場合はマウスフォールバック（任意）。
public class SaberInputBridge : MonoBehaviour
{
    [Header("Source")]
    public bool useInputPoint = true;
    public bool fallbackToMouse = false;
    // UDP データがこの秒数以上止まったら、マウスフォールバックに自動で切り替える。
    public float inputPointStaleSeconds = 1.0f;
    public Camera targetCamera;
    [Header("Debug")]
    public bool debugCoordinates = false;

    [Header("Coordinate")]
    // 譜面ソフトと同じスケール（pixel → world）。判定面 11×6 / ボード 1920×1080 なら ≒0.00573。
    public float pixelsToWorld = 0.00573f;
    public float fixedZ = 0f;
    public bool clampToBounds = true;
    public Vector2 minBounds = new Vector2(-5.5f, -3f);
    public Vector2 maxBounds = new Vector2(5.5f, 3f);
    [Header("Smoothing")]
    public bool enableSmoothing = true;
    public float smoothingTau = 0.08f;
    Vector3 smoothedPosition;
    bool hasSmoothedPosition = false;

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
            var ip = InputPoint.Instance;
            Vector3 targetWorld;
            // InputPoint の LocalPosition は、正規化入力でもピクセル入力でも最終的なローカル座標として扱う。
            // ここでさらに pixelsToWorld を掛けると二重変換になるので、そのまま使う。
            targetWorld = new Vector3(ip.LocalPosition.x, ip.LocalPosition.y, fixedZ);

            if (debugCoordinates)
            {
                Debug.Log($"[SaberInputBridge] LocalPosition={ip.LocalPosition} Normalized={ip.NormalizedPosition} targetWorld={targetWorld}");
            }
            ApplySmoothedPosition(targetWorld);
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
        ApplySmoothedPosition(new Vector3(worldXY.x, worldXY.y, fixedZ));
    }

    private void ApplySmoothedPosition(Vector3 targetPosition)
    {
        if (clampToBounds)
        {
            targetPosition.x = Mathf.Clamp(targetPosition.x, minBounds.x, maxBounds.x);
            targetPosition.y = Mathf.Clamp(targetPosition.y, minBounds.y, maxBounds.y);
        }

        if (!enableSmoothing)
        {
            smoothedPosition = targetPosition;
            hasSmoothedPosition = true;
            transform.position = targetPosition;
            return;
        }

        if (!hasSmoothedPosition)
        {
            smoothedPosition = targetPosition;
            hasSmoothedPosition = true;
            transform.position = targetPosition;
            return;
        }

        float dt = Time.deltaTime;
        float alpha = dt / (smoothingTau + dt);
        smoothedPosition = Vector3.Lerp(smoothedPosition, targetPosition, alpha);
        transform.position = smoothedPosition;
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
