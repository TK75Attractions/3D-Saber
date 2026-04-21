using UnityEngine;
using UnityEngine.InputSystem;

// マウスカーソル位置を Z=fixedZ の平面に投影してセーバー位置にする。
// 実機セーバー（UDP）の代わりのテスト入力。
// 平面レイキャストなのでカメラの角度・位置が変わっても追従する。
public class MouseSaberInput : MonoBehaviour
{
    public Camera targetCamera;
    public float fixedZ = 0f;
    // 追従を少し滑らかにしたい場合の係数（0=即時、1=動かない）。既定は即時追従。
    [Range(0f, 0.95f)] public float smoothing = 0f;
    public bool clampToBounds = true;
    public Vector2 minBounds = new Vector2(-8f, -4.5f);
    public Vector2 maxBounds = new Vector2(8f, 4.5f);
    public bool yieldIfPointerActive = false;

    void Awake()
    {
        if (targetCamera == null) targetCamera = Camera.main;
    }

    void Update()
    {
        if (yieldIfPointerActive && InputPoint.Instance != null) return;
        if (targetCamera == null)
        {
            targetCamera = Camera.main;
            if (targetCamera == null) return;
        }

        Vector2 screen = ReadMouseScreen();
        if (!TryProjectToPlane(screen, fixedZ, targetCamera, out Vector3 worldPos)) return;

        if (clampToBounds)
        {
            worldPos.x = Mathf.Clamp(worldPos.x, minBounds.x, maxBounds.x);
            worldPos.y = Mathf.Clamp(worldPos.y, minBounds.y, maxBounds.y);
        }
        worldPos.z = fixedZ;

        if (smoothing > 0f)
        {
            transform.position = Vector3.Lerp(worldPos, transform.position, smoothing);
        }
        else
        {
            transform.position = worldPos;
        }
    }

    public static Vector2 ReadMouseScreen()
    {
        Mouse m = Mouse.current;
        if (m != null) return m.position.ReadValue();
        return new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
    }

    // スクリーン座標を指定 Z 平面に投影。テストから直接叩けるよう静的。
    public static bool TryProjectToPlane(Vector2 screenPos, float planeZ, Camera cam, out Vector3 worldPos)
    {
        worldPos = Vector3.zero;
        if (cam == null) return false;
        Ray ray = cam.ScreenPointToRay(new Vector3(screenPos.x, screenPos.y, 0f));
        Plane plane = new Plane(Vector3.forward, new Vector3(0f, 0f, planeZ));
        if (!plane.Raycast(ray, out float dist)) return false;
        worldPos = ray.GetPoint(dist);
        return true;
    }
}
