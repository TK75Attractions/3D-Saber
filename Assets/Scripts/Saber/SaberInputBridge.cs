using UnityEngine;

// 棒1（InputPoint.Stick1）の Canvas 座標を 3D ワールド XY に変換してセーバー位置に反映。
// ブレードモード: InputPoint の 2 端点（LocalStickRawA/B）をそのまま線分として使い、
//   transform.position は中点、世界座標の端点を WorldEndA/B に公開する。
//   SaberCutJudge は HasBlade を見て、線分でノーツ判定する。
// 単点モード（useBladeMode=false）: 従来通り LocalPosition（中点）を transform.position に書く。
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

    [Header("Blade (line) mode")]
    // true なら 2 端点ベースのブレードとして扱う。表示も判定も線になる。
    public bool useBladeMode = true;
    public float bladeWidth = 0.12f;
    public Color bladeColor = new Color(0.4f, 1f, 1f, 1f);
    // マウスフォールバック時に出す擬似ブレードの長さ（カーソル中心に水平配置）。
    public float fallbackBladeLength = 1.0f;
    LineRenderer bladeLine;
    Material bladeMaterialOwned;

    // SaberCutJudge から参照される世界座標の端点。
    public Vector3 WorldEndA { get; private set; }
    public Vector3 WorldEndB { get; private set; }
    // ブレードデータが有効か（線分判定を使うか）。
    public bool HasBlade { get; private set; }

    void Awake()
    {
        if (targetCamera == null) targetCamera = Camera.main;
        if (useBladeMode) EnsureBladeLine();
    }

    void OnDestroy()
    {
        if (bladeMaterialOwned != null)
        {
            if (Application.isPlaying) Destroy(bladeMaterialOwned);
            else DestroyImmediate(bladeMaterialOwned);
            bladeMaterialOwned = null;
        }
    }

    void EnsureBladeLine()
    {
        if (bladeLine != null) return;
        bladeLine = GetComponent<LineRenderer>();
        if (bladeLine == null) bladeLine = gameObject.AddComponent<LineRenderer>();
        bladeLine.useWorldSpace = true;
        bladeLine.positionCount = 2;
        bladeLine.startWidth = bladeWidth;
        bladeLine.endWidth = bladeWidth;
        bladeLine.numCapVertices = 4;
        bladeLine.numCornerVertices = 0;
        bladeLine.alignment = LineAlignment.View;
        if (bladeLine.sharedMaterial == null)
        {
            var sh = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            bladeMaterialOwned = new Material(sh);
            if (bladeMaterialOwned.HasProperty("_BaseColor")) bladeMaterialOwned.SetColor("_BaseColor", bladeColor);
            else bladeMaterialOwned.color = bladeColor;
            bladeLine.sharedMaterial = bladeMaterialOwned;
        }
        bladeLine.startColor = bladeColor;
        bladeLine.endColor = bladeColor;
    }

    void Update()
    {
        bool consumed = false;

        // UDP データが「最近」来てるなら、それを使う。1 秒以上無音ならマウスフォールバック。
        if (useInputPoint && InputPoint.Instance != null
            && InputPoint.Instance.IsRecentlyActive(inputPointStaleSeconds))
        {
            var ip = InputPoint.Instance;
            if (useBladeMode)
            {
                Vector3 rawA = new Vector3(ip.LocalStickRawA.x, ip.LocalStickRawA.y, fixedZ);
                Vector3 rawB = new Vector3(ip.LocalStickRawB.x, ip.LocalStickRawB.y, fixedZ);
                ApplyBladeImmediate(rawA, rawB);
            }
            else
            {
                Vector3 targetWorld = new Vector3(ip.LocalPosition.x, ip.LocalPosition.y, fixedZ);
                ApplyPositionImmediate(targetWorld);
            }
            if (debugCoordinates)
            {
                Debug.Log($"[SaberInputBridge] mid={ip.LocalPosition} endA={ip.LocalStickRawA} endB={ip.LocalStickRawB}");
            }
            consumed = true;
        }

        if (!consumed && fallbackToMouse)
        {
            if (targetCamera == null) targetCamera = Camera.main;
            if (targetCamera == null) return;
            Vector2 mouseScreen = MouseSaberInput.ReadMouseScreen();
            if (MouseSaberInput.TryProjectToPlane(mouseScreen, fixedZ, targetCamera, out Vector3 world))
            {
                if (useBladeMode)
                {
                    float half = fallbackBladeLength * 0.5f;
                    Vector3 a = new Vector3(world.x - half, world.y, fixedZ);
                    Vector3 b = new Vector3(world.x + half, world.y, fixedZ);
                    // マウスは smoothing で滑らかに（中点だけ）→ 擬似ブレードも中点に合わせて配置し直す。
                    ApplyPosition(new Vector2(world.x, world.y));
                    Vector3 mid = transform.position;
                    Vector3 sa = new Vector3(mid.x - half, mid.y, fixedZ);
                    Vector3 sb = new Vector3(mid.x + half, mid.y, fixedZ);
                    PublishBlade(sa, sb);
                }
                else
                {
                    ApplyPosition(new Vector2(world.x, world.y));
                }
            }
        }
    }

    private void ApplyBladeImmediate(Vector3 rawA, Vector3 rawB)
    {
        Vector3 a = ClampPoint(rawA);
        Vector3 b = ClampPoint(rawB);

        // UDP の 2 端点はスムージングなしで即時反映（既存の中点 UDP 流儀と一致）。
        // 中点を transform.position に書くことで SaberTracker が速度を拾える。
        Vector3 mid = (a + b) * 0.5f;
        smoothedPosition = mid;
        hasSmoothedPosition = true;
        transform.position = mid;

        // A→B 方向に Z 軸回転。
        Vector3 dir = b - a;
        if (dir.sqrMagnitude > 0.0001f)
        {
            float ang = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
            transform.rotation = Quaternion.Euler(0f, 0f, ang);
        }

        PublishBlade(a, b);
    }

    private void PublishBlade(Vector3 a, Vector3 b)
    {
        WorldEndA = a;
        WorldEndB = b;
        HasBlade = true;

        if (bladeLine == null) EnsureBladeLine();
        if (bladeLine != null)
        {
            bladeLine.SetPosition(0, a);
            bladeLine.SetPosition(1, b);
            bladeLine.startWidth = bladeWidth;
            bladeLine.endWidth = bladeWidth;
            bladeLine.startColor = bladeColor;
            bladeLine.endColor = bladeColor;
            if (!bladeLine.enabled) bladeLine.enabled = true;
        }
    }

    Vector3 ClampPoint(Vector3 v)
    {
        if (!clampToBounds) return v;
        v.x = Mathf.Clamp(v.x, minBounds.x, maxBounds.x);
        v.y = Mathf.Clamp(v.y, minBounds.y, maxBounds.y);
        return v;
    }

    private void ApplyPosition(Vector2 worldXY)
    {
        ApplySmoothedPosition(new Vector3(worldXY.x, worldXY.y, fixedZ));
    }

    private void ApplyPositionImmediate(Vector3 targetPosition)
    {
        if (clampToBounds)
        {
            targetPosition.x = Mathf.Clamp(targetPosition.x, minBounds.x, maxBounds.x);
            targetPosition.y = Mathf.Clamp(targetPosition.y, minBounds.y, maxBounds.y);
        }

        smoothedPosition = targetPosition;
        hasSmoothedPosition = true;
        transform.position = targetPosition;
        HasBlade = false; // 単点モードでは線分判定を使わない
    }

    private void ApplySmoothedPosition(Vector3 targetPosition)
    {
        if (clampToBounds)
        {
            targetPosition.x = Mathf.Clamp(targetPosition.x, minBounds.x, maxBounds.x);
            targetPosition.y = Mathf.Clamp(targetPosition.y, minBounds.y, maxBounds.y);
        }

        if (!enableSmoothing || !hasSmoothedPosition)
        {
            smoothedPosition = targetPosition;
            hasSmoothedPosition = true;
            transform.position = targetPosition;
            HasBlade = false;
            return;
        }

        float dt = Time.deltaTime;
        float alpha = dt / (smoothingTau + dt);
        smoothedPosition = Vector3.Lerp(smoothedPosition, targetPosition, alpha);
        transform.position = smoothedPosition;
        HasBlade = false;
    }

    // テスト・外部から線分を直接差し込むための入口（モック用途）。
    public void OverrideBlade(Vector3 a, Vector3 b)
    {
        WorldEndA = a;
        WorldEndB = b;
        HasBlade = true;
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
