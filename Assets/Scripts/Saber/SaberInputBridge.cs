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
    // どちらの棒を読むか。1 = InputPoint の棒1(port 5005) / 2 = 棒2(port 5006)。
    // 2本セーバープレイでは棒ごとに Bridge を1つずつ持つ。
    public int stickIndex = 1;
    public Camera targetCamera;

    // 今フレームの位置ソースがマウスフォールバックか。
    // SaberCutJudge はこれを見て、マウス時は手の区別(SaberHand)を Any 扱いにする。
    public bool UsingMouseFallback { get; private set; }
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

    [Header("Menu remap")]
    // メニュー画面用: 判定面(±sourceHalfExtents)基準の入力座標を、targetCamera が
    // z=fixedZ 平面上に映す範囲全体へ写像する(セーバーが画面端まで届くように)。
    // 棒の長さ・角度は保つ(中点だけ写像し、端点は平行移動)。ゲーム本編では false のまま。
    public bool remapToCameraView = false;
    public Vector2 sourceHalfExtents = new Vector2(5.5f, 3f);
    Vector2 viewCenter;
    Vector2 viewHalfExtents;
    bool viewResolved;

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

    // この Bridge が読む棒のデータが「最近」届いているか(棒1/棒2で別管理)。
    private bool IsStickRecentlyActive()
    {
        var ip = InputPoint.Instance;
        if (ip == null) return false;
        return stickIndex == 2
            ? ip.IsRecentlyActive2(inputPointStaleSeconds)
            : ip.IsRecentlyActive(inputPointStaleSeconds);
    }

    void Update()
    {
        bool consumed = false;

        // UDP データが「最近」来てるなら、それを使う。無音ならマウスフォールバック(なければ非表示)。
        if (useInputPoint && IsStickRecentlyActive())
        {
            var ip = InputPoint.Instance;
            if (useBladeMode)
            {
                Vector2 endA = stickIndex == 2 ? ip.LocalStickRawA2 : ip.LocalStickRawA;
                Vector2 endB = stickIndex == 2 ? ip.LocalStickRawB2 : ip.LocalStickRawB;
                Vector3 a = new Vector3(endA.x, endA.y, fixedZ);
                Vector3 b = new Vector3(endB.x, endB.y, fixedZ);
                if (remapToCameraView && ResolveViewExtents())
                {
                    // 中点だけ写像して端点は平行移動(棒の長さ・角度を保つ)
                    Vector3 mid = (a + b) * 0.5f;
                    Vector3 mapped = RemapPoint(mid, sourceHalfExtents, viewCenter, viewHalfExtents, fixedZ);
                    Vector3 shift = mapped - mid;
                    a += shift;
                    b += shift;
                }
                ApplyBladeImmediate(a, b);
            }
            else
            {
                Vector2 mid = stickIndex == 2 ? ip.LocalPosition2 : ip.LocalPosition;
                Vector3 p = new Vector3(mid.x, mid.y, fixedZ);
                if (remapToCameraView && ResolveViewExtents())
                {
                    p = RemapPoint(p, sourceHalfExtents, viewCenter, viewHalfExtents, fixedZ);
                }
                ApplyPositionImmediate(p);
            }
            if (debugCoordinates)
            {
                Debug.Log($"[SaberInputBridge] stick{stickIndex} endA={WorldEndA} endB={WorldEndB}");
            }
            UsingMouseFallback = false;
            consumed = true;
        }

        if (!consumed && fallbackToMouse)
        {
            if (targetCamera == null) targetCamera = Camera.main;
            if (targetCamera == null) return;
            Vector2 mouseScreen = MouseSaberInput.ReadMouseScreen();
            if (MouseSaberInput.TryProjectToPlane(mouseScreen, fixedZ, targetCamera, out Vector3 world))
            {
                ApplyMouseWorld(world);
                consumed = true;
            }
        }

        // データ源が何も無い(棒2が未接続など):古い線を残さず非表示にする。
        if (!consumed) HideBlade();
    }

    // マウスを z=fixedZ 平面へ投影したワールド座標からセーバー位置(と擬似ブレード)を作る。
    // マウスは既にカメラの可視範囲全体に投影されているので写像は不要だが、クランプは
    // 可視範囲(remap 時)で行う必要がある。以前はここで判定面の固定範囲 ±5.5×±3 に再クランプ
    // していたため、選曲画面(可視範囲 ≒ ±10×±6)でマウスの赤い線が画面の約 75% で止まっていた。
    // Update から呼ぶほか、テストから直接叩ける(Mouse.current を介さない)。
    public void ApplyMouseWorld(Vector3 world)
    {
        if (remapToCameraView) ResolveViewExtents();
        // マウスは smoothing で滑らかに（中点だけ）→ 擬似ブレードは滑らかにした中点の周りに置く。
        ApplyPosition(new Vector2(world.x, world.y));
        if (useBladeMode)
        {
            float half = fallbackBladeLength * 0.5f;
            Vector3 mid = transform.position;
            PublishBlade(new Vector3(mid.x - half, mid.y, fixedZ), new Vector3(mid.x + half, mid.y, fixedZ));
        }
        UsingMouseFallback = true;
    }

    private void HideBlade()
    {
        UsingMouseFallback = false;
        HasBlade = false;
        if (bladeLine != null && bladeLine.enabled) bladeLine.enabled = false;
    }

    // ブレードの色を実行時に変更する(手の色分け用)。生成済みのマテリアル/ラインにも反映する。
    public void SetBladeColor(Color c)
    {
        bladeColor = c;
        if (bladeMaterialOwned != null)
        {
            if (bladeMaterialOwned.HasProperty("_BaseColor")) bladeMaterialOwned.SetColor("_BaseColor", c);
            else bladeMaterialOwned.color = c;
        }
        if (bladeLine != null)
        {
            bladeLine.startColor = c;
            bladeLine.endColor = c;
        }
    }

    private void ApplyBladeImmediate(Vector3 rawA, Vector3 rawB)
    {
        // 端点を個別にクランプすると、画面端で片側だけ止まって中点(=ポインタ位置・速度の基準)が
        // 刃長の 1/4 だけ内側へ戻り、「右端まで届かない」原因になっていた。
        // 中点だけを範囲内に収め、端点は同じ量だけ平行移動する(刃長・角度は不変)。
        Vector3 a = rawA;
        Vector3 b = rawB;
        if (clampToBounds)
        {
            (a, b) = ClampBladeKeepingLength(rawA, rawB, EffectiveMinBounds, EffectiveMaxBounds);
        }

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

    // リマップ有効時はカメラの可視範囲、通常時は判定面の範囲でクランプする。
    Vector2 EffectiveMinBounds => remapToCameraView && viewResolved ? viewCenter - viewHalfExtents : minBounds;
    Vector2 EffectiveMaxBounds => remapToCameraView && viewResolved ? viewCenter + viewHalfExtents : maxBounds;

    // 刃(線分 a-b)の中点を min..max に収め、端点は同じ量だけ平行移動する。純関数。
    // 中点が範囲内なら何も変えない(端が画面外にはみ出ても刃は縮めない)。
    public static (Vector3 a, Vector3 b) ClampBladeKeepingLength(Vector3 a, Vector3 b, Vector2 min, Vector2 max)
    {
        Vector3 mid = (a + b) * 0.5f;
        Vector3 shift = new Vector3(
            Mathf.Clamp(mid.x, min.x, max.x) - mid.x,
            Mathf.Clamp(mid.y, min.y, max.y) - mid.y,
            0f);
        return (a + shift, b + shift);
    }

    // カメラが z=fixedZ 平面上に映す範囲を求めてキャッシュする(メニューのカメラは静止前提)。
    bool ResolveViewExtents()
    {
        if (viewResolved) return true;
        if (targetCamera == null) targetCamera = Camera.main;
        viewResolved = TryComputePlaneView(targetCamera, fixedZ, out viewCenter, out viewHalfExtents);
        return viewResolved;
    }

    // カメラが z=planeZ 平面上に映す範囲(中心と半幅)。ビューポート対角2点の投影で求める。純関数。
    public static bool TryComputePlaneView(Camera cam, float planeZ, out Vector2 center, out Vector2 halfExtents)
    {
        center = Vector2.zero;
        halfExtents = Vector2.zero;
        if (cam == null) return false;
        // ScreenPointToRay は「画面全体」のピクセル座標を取るので、カメラの描画矩形(pixelRect)の
        // 対角2点を使う。(0,0)〜(pixelWidth,pixelHeight) だとレターボックス等で rect が
        // オフセットされたときに写像が横(縦)へずれ、片側の端に届かなくなる。
        Rect pr = cam.pixelRect;
        if (!MouseSaberInput.TryProjectToPlane(new Vector2(pr.xMin, pr.yMin), planeZ, cam, out Vector3 bl)) return false;
        if (!MouseSaberInput.TryProjectToPlane(new Vector2(pr.xMax, pr.yMax), planeZ, cam, out Vector3 tr)) return false;
        center = new Vector2((bl.x + tr.x) * 0.5f, (bl.y + tr.y) * 0.5f);
        halfExtents = new Vector2(Mathf.Abs(tr.x - bl.x) * 0.5f, Mathf.Abs(tr.y - bl.y) * 0.5f);
        return halfExtents.x > 0.001f && halfExtents.y > 0.001f;
    }

    // 判定面(±sourceHalf)基準の座標を、カメラ可視範囲(viewCenter±viewHalf)へ写像する。純関数。
    public static Vector3 RemapPoint(Vector3 v, Vector2 sourceHalf, Vector2 viewCenter, Vector2 viewHalf, float z)
    {
        float nx = sourceHalf.x > 0.001f ? v.x / sourceHalf.x : 0f;
        float ny = sourceHalf.y > 0.001f ? v.y / sourceHalf.y : 0f;
        return new Vector3(
            viewCenter.x + Mathf.Clamp(nx, -1f, 1f) * viewHalf.x,
            viewCenter.y + Mathf.Clamp(ny, -1f, 1f) * viewHalf.y,
            z);
    }

    private void ApplyPosition(Vector2 worldXY)
    {
        ApplySmoothedPosition(new Vector3(worldXY.x, worldXY.y, fixedZ));
    }

    // 中点のクランプ。リマップ有効時はカメラの可視範囲、通常時は判定面の範囲(ブレードと同じ規則)。
    private Vector3 ClampMid(Vector3 p)
    {
        if (!clampToBounds) return p;
        Vector2 min = EffectiveMinBounds;
        Vector2 max = EffectiveMaxBounds;
        p.x = Mathf.Clamp(p.x, min.x, max.x);
        p.y = Mathf.Clamp(p.y, min.y, max.y);
        return p;
    }

    private void ApplyPositionImmediate(Vector3 targetPosition)
    {
        targetPosition = ClampMid(targetPosition);

        smoothedPosition = targetPosition;
        hasSmoothedPosition = true;
        transform.position = targetPosition;
        HasBlade = false; // 単点モードでは線分判定を使わない
    }

    private void ApplySmoothedPosition(Vector3 targetPosition)
    {
        targetPosition = ClampMid(targetPosition);

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

    // テスト用：マウスフォールバック状態を直接差し込む（EffectiveHand の検証用モック）。
    public void OverrideMouseFallback(bool usingMouse)
    {
        UsingMouseFallback = usingMouse;
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
