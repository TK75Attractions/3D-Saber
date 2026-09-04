using UnityEngine;

// ノーツの「切る瞬間」を読みやすくする視覚キュー。
// 3D の Z 接近はスクリーン上ではサイズ変化でしか知覚できず残り時間が読めないので、
// 判定面(judgeZ)上の着地位置に、スクリーン空間で線形に読める枠を2枚重ねる：
//   1. 固定枠(GhostRoot)    ：ノーツとピッタリ同サイズの枠。着地する「場所」のアンカー
//   2. 収縮枠(ApproachRoot)：大きく現れて HitTime ちょうどで固定枠と同サイズへ線形収縮する
//      → 2枚の枠がピッタリ重なった瞬間 = 切る瞬間(残り時間は収縮の残り量で直読みできる)
//   3. 判定窓内ハイライト  ：切れる時間帯は両枠を白く光らせ、本体の発光もブースト
// ノーツ側に付いていた接近リングは、壁側の2枚と情報が重複して視線が散るため既定で廃止
// (buildRing=true でオプトイン復活。純関数はそのまま残す)。
// NoteSpawner が生成し、毎フレーム Tick(dt, ...) で駆動する(GManager 主体パターン)。
public class NoteTimingCue : MonoBehaviour
{
    [Header("Ring (ノーツ側の接近リング。既定オフ)")]
    public bool buildRing = false;
    public float ringStartScale = 2.4f;
    public float ringEndScale = 1.10f;
    // approachTime のうち、残り時間がこの割合を切ったらリングが見え始める
    [Range(0.1f, 1f)] public float ringVisiblePortion = 0.65f;
    public float ringMaxAlpha = 0.9f;
    public float ringThickness = 0.035f;

    [Header("Ghost (判定面の着地枠 = 固定枠 + 収縮枠)")]
    // 着地の「時間+場所」アンカー。常時表示だと密な譜面で重なってノイズになるため、
    // ghostVisibleSeconds(残り時間)を切ってから2枚同時に浮かび上がる短時間方式。枠のみで塗りは持たない。
    public bool buildGhost = true;
    // 残りこの秒数を切ったら両枠が浮かび上がる(HitTime ちょうどで不透明度最大・収縮枠が等倍)
    public float ghostVisibleSeconds = 0.7f;
    // 固定枠: 常にノーツと同サイズ(ghostBaseScale)。少し太めで「ここに来る」を示す
    public float ghostMaxAlpha = 0.7f;
    public float ghostThickness = 0.05f;
    // 収縮枠: 出現直後の大きさ(固定枠比)。HitTime ちょうどで 1.0(固定枠とピッタリ)へ収束する。
    public float ghostStartScale = 2.4f;
    public float approachMaxAlpha = 0.9f;
    public float approachThickness = 0.035f;
    // 判定面ガイドと Z-fight しないよう、わずかに手前に置く(収縮枠はさらに少し手前)
    public float ghostZBias = -0.03f;
    public float approachZBias = -0.06f;

    [Header("In-window highlight")]
    public Color readyColor = Color.white;
    public float inWindowEmissionBoost = 2.4f;
    public float missFadeSeconds = 0.25f;

    public bool InWindow { get; private set; }
    public Transform RingRoot => ringRoot;
    public GameObject GhostRoot => ghostRoot;
    public GameObject ApproachRoot => approachRoot;
    // 固定枠のスケール(=収縮枠の収束先)。テストから検査できるよう公開。
    public Vector3 GhostBaseScale => ghostBaseScale;

    private CuttableNote note;
    private NoteVisuals visuals;
    private Color baseColor = Color.white;
    private Transform ringRoot;
    private GameObject ghostRoot;
    private GameObject approachRoot;
    private Vector3 ghostBaseScale = Vector3.one; // ノーツと同サイズ(=収束先)のスケール
    private Material ringMat;
    private Material ghostMat;
    private Material approachMat;
    private float endFadeAlpha = 1f;
    private bool initialized;

    // NoteSpawner.SpawnOne から呼ぶ。judgeZ は着地枠の Z 位置。
    public void Initialize(CuttableNote ownerNote, float judgeZ)
    {
        note = ownerNote != null ? ownerNote : GetComponent<CuttableNote>();
        visuals = GetComponent<NoteVisuals>();
        if (visuals != null)
        {
            baseColor = visuals.baseColor;
        }
        else
        {
            var mr = GetComponent<MeshRenderer>();
            if (mr != null && mr.sharedMaterial != null)
            {
                baseColor = mr.sharedMaterial.HasProperty("_BaseColor")
                    ? mr.sharedMaterial.GetColor("_BaseColor")
                    : mr.sharedMaterial.color;
            }
        }

        if (buildRing) BuildRing();
        if (buildGhost) BuildGhost(judgeZ);
        initialized = true;
    }

    void OnDestroy()
    {
        if (ghostRoot != null) SafeDestroyGo(ghostRoot);
        if (approachRoot != null) SafeDestroyGo(approachRoot);
        SafeDestroy(ringMat);
        SafeDestroy(ghostMat);
        SafeDestroy(approachMat);
    }

    // NoteSpawner.UpdateLive から毎フレーム呼ばれる。
    // dt = HitTime - songTime（正 = まだ先、負 = 過ぎた）
    public void Tick(double dt, float approachTime, float earlyWindow, float lateWindow)
    {
        if (!initialized) return;

        if (note != null && (note.IsMissed || note.IsCut))
        {
            TickAfterEnd();
            return;
        }

        bool inWindow = IsInWindow(dt, earlyWindow, lateWindow);
        InWindow = inWindow;
        if (visuals != null) visuals.SetEmissionBoost(inWindow ? inWindowEmissionBoost : 1f);

        // (オプトイン)接近リング：HitTime ちょうどで輪郭サイズに収束
        if (ringRoot != null)
        {
            float s = ComputeRingScale(dt, approachTime, ringStartScale, ringEndScale);
            ringRoot.localScale = new Vector3(s, s, 1f);
            float a = ComputeRingAlpha(dt, approachTime, ringVisiblePortion) * ringMaxAlpha;
            Color c = inWindow ? readyColor : Color.Lerp(baseColor, Color.white, 0.25f);
            ApplyColor(ringMat, c, a, inWindow ? 2.6f : 1.5f);
        }

        // 着地枠：残り ghostVisibleSeconds を切ってから2枚同時にフェードイン。
        // 固定枠は等倍のまま、収縮枠だけが HitTime ちょうどで固定枠と同サイズへ線形収縮する。
        float ramp = ComputeGhostAlpha(dt, ghostVisibleSeconds);
        Color gc = inWindow ? readyColor : baseColor;
        if (ghostRoot != null)
        {
            float ga = inWindow ? ghostMaxAlpha : ramp * ghostMaxAlpha;
            ApplyColor(ghostMat, gc, ga, inWindow ? 2.4f : 1.4f);
        }
        if (approachRoot != null)
        {
            float aa = inWindow ? approachMaxAlpha : ramp * approachMaxAlpha;
            ApplyColor(approachMat, gc, aa, inWindow ? 2.6f : 1.5f);
            float gs = ComputeGhostScale(dt, ghostVisibleSeconds, ghostStartScale);
            approachRoot.transform.localScale = new Vector3(
                ghostBaseScale.x * gs, ghostBaseScale.y * gs, ghostBaseScale.z);
        }
    }

    // Miss / Cut 後はすっとフェードアウトして消す（ノーツ本体は別途流れる/砕ける）。
    private void TickAfterEnd()
    {
        InWindow = false;
        if (visuals != null) visuals.SetEmissionBoost(1f);
        endFadeAlpha = Mathf.Clamp01(endFadeAlpha - Time.deltaTime / Mathf.Max(0.01f, missFadeSeconds));
        float a = endFadeAlpha;
        if (ringRoot != null)
        {
            if (a <= 0f && ringRoot.gameObject.activeSelf) ringRoot.gameObject.SetActive(false);
            else ApplyColor(ringMat, baseColor, a * 0.4f, 0.5f);
        }
        if (ghostRoot != null)
        {
            if (a <= 0f && ghostRoot.activeSelf) ghostRoot.SetActive(false);
            else ApplyColor(ghostMat, baseColor, a * 0.3f, 0.5f);
        }
        if (approachRoot != null)
        {
            if (a <= 0f && approachRoot.activeSelf) approachRoot.SetActive(false);
            else ApplyColor(approachMat, baseColor, a * 0.3f, 0.5f);
        }
    }

    // ---- 純関数（テストから直接叩く） ----

    // dt=approachTime → startScale、dt=0 → endScale の線形収束。dt<0 は endScale を維持。
    public static float ComputeRingScale(double dt, float approachTime, float startScale, float endScale)
    {
        if (approachTime <= 0.0001f) return endScale;
        float t = 1f - Mathf.Clamp01((float)(dt / approachTime));
        return Mathf.Lerp(startScale, endScale, t);
    }

    // 残り時間が approachTime * visiblePortion を切ってから smoothstep でフェードイン。dt<=0 で 1。
    public static float ComputeRingAlpha(double dt, float approachTime, float visiblePortion)
    {
        if (approachTime <= 0.0001f) return 1f;
        float visibleStart = approachTime * Mathf.Clamp01(visiblePortion);
        if (visibleStart <= 0.0001f) return dt <= 0.0 ? 1f : 0f;
        if (dt >= visibleStart) return 0f;
        if (dt <= 0.0) return 1f;
        float t = 1f - (float)(dt / visibleStart);
        return Mathf.Clamp01(t * t * (3f - 2f * t));
    }

    // 判定窓: 早め earlyWindow 秒前 〜 遅め lateWindow 秒後（両端含む）。
    public static bool IsInWindow(double dt, float earlyWindow, float lateWindow)
    {
        return dt <= earlyWindow && dt >= -lateWindow;
    }

    // 収縮枠の大きさ(固定枠比)。出現時 startScale → HitTime ちょうどで 1.0 へ線形収縮。
    // 線形なので「あとどれくらいで叩くか」がスクリーン上の残り縮み量で直読みできる。
    // HitTime を過ぎたら 1.0(固定枠とピッタリ)を維持する。
    public static float ComputeGhostScale(double dt, float visibleSeconds, float startScale)
    {
        if (visibleSeconds <= 0.0001f) return 1f;
        float t = 1f - Mathf.Clamp01((float)(dt / visibleSeconds)); // 0=出現直後, 1=HitTime
        return Mathf.Lerp(startScale, 1f, t);
    }

    // 着地枠の不透明度。残り visibleSeconds を切ってから smoothstep で 0→1(HitTime 以降は 1 を維持)。
    // 短時間表示にすることで、密な譜面でも判定面に枠が重なり続けない。
    public static float ComputeGhostAlpha(double dt, float visibleSeconds)
    {
        if (visibleSeconds <= 0.0001f) return dt <= 0.0 ? 1f : 0f;
        if (dt >= visibleSeconds) return 0f;
        if (dt <= 0.0) return 1f;
        float t = 1f - (float)(dt / visibleSeconds);
        return Mathf.Clamp01(t * t * (3f - 2f * t));
    }

    // ---- 構築 ----

    private void BuildRing()
    {
        var root = new GameObject("TimingRing");
        root.transform.SetParent(transform, false);
        // 前面(-Z)のさらに少し手前。ロングノーツの Z 伸長は親の z scale で吸収される。
        root.transform.localPosition = new Vector3(0f, 0f, -0.62f);
        root.transform.localRotation = Quaternion.identity;
        root.transform.localScale = Vector3.one * ringStartScale;
        ringRoot = root.transform;

        ringMat = MakeCueMaterial();
        BuildFrame(root.transform, 0.5f, ringThickness, ringMat);
        ApplyColor(ringMat, baseColor, 0f, 1.5f);
    }

    private void BuildGhost(float judgeZ)
    {
        Vector3 p = transform.position;
        // ノーツのワールド XY サイズに合わせる（prefab の scale 0.8 等を吸収。Z 伸長は無視）
        Vector3 ls = transform.lossyScale;
        ghostBaseScale = new Vector3(Mathf.Abs(ls.x) * 1.04f, Mathf.Abs(ls.y) * 1.04f, 1f);

        // 固定枠: ノーツと同サイズで動かない(着地位置のアンカー)
        ghostRoot = new GameObject(name + "_LandingGhost");
        ghostRoot.transform.position = new Vector3(p.x, p.y, judgeZ + ghostZBias);
        ghostRoot.transform.localScale = ghostBaseScale;
        ghostMat = MakeCueMaterial();
        BuildFrame(ghostRoot.transform, 0.5f, ghostThickness, ghostMat);
        ApplyColor(ghostMat, baseColor, 0f, 1.2f);

        // 収縮枠: 出現時は大きく(ghostStartScale倍)。Tick が HitTime に向けて固定枠へ収縮させる。
        approachRoot = new GameObject(name + "_ApproachGhost");
        approachRoot.transform.position = new Vector3(p.x, p.y, judgeZ + approachZBias);
        approachRoot.transform.localScale = new Vector3(
            ghostBaseScale.x * ghostStartScale, ghostBaseScale.y * ghostStartScale, 1f);
        approachMat = MakeCueMaterial();
        BuildFrame(approachRoot.transform, 0.5f, approachThickness, approachMat);
        ApplyColor(approachMat, baseColor, 0f, 1.2f);
        // 内側の塗り(GhostFill)は廃止:判定面付近を霞ませる原因だった。枠だけで場所は十分伝わる。
    }

    // 中心 (0,0)・半幅 half の正方形枠を 4 本の薄い Cube で組む。
    private static void BuildFrame(Transform parent, float half, float thickness, Material mat)
    {
        MakeBar(parent, "Top", new Vector3(0f, half, 0f), new Vector3(half * 2f + thickness, thickness, 0.02f), mat);
        MakeBar(parent, "Bottom", new Vector3(0f, -half, 0f), new Vector3(half * 2f + thickness, thickness, 0.02f), mat);
        MakeBar(parent, "Left", new Vector3(-half, 0f, 0f), new Vector3(thickness, half * 2f + thickness, 0.02f), mat);
        MakeBar(parent, "Right", new Vector3(half, 0f, 0f), new Vector3(thickness, half * 2f + thickness, 0.02f), mat);
    }

    private static void MakeBar(Transform parent, string name, Vector3 localPos, Vector3 localScale, Material mat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = localScale;
        StripCollider(go);
        go.GetComponent<MeshRenderer>().sharedMaterial = mat;
    }

    private static Material MakeCueMaterial()
    {
        var sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var m = new Material(sh);
        if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);
        if (m.HasProperty("_Blend")) m.SetFloat("_Blend", 0f);
        if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 0f);
        m.renderQueue = 3000;
        if (m.HasProperty("_EmissionColor")) m.EnableKeyword("_EMISSION");
        return m;
    }

    // 発光はアルファに連動させ、フェードアウト時に光だけ残らないようにする。
    private static void ApplyColor(Material m, Color c, float alpha, float emission)
    {
        if (m == null) return;
        Color baseC = c;
        baseC.a = Mathf.Clamp01(alpha);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", baseC);
        else m.color = baseC;
        if (m.HasProperty("_EmissionColor"))
        {
            float e = emission * Mathf.Clamp01(alpha * 1.4f);
            m.SetColor("_EmissionColor", new Color(c.r, c.g, c.b, 1f) * e);
        }
    }

    private static void StripCollider(GameObject go)
    {
        var col = go.GetComponent<Collider>();
        if (col != null) SafeDestroy(col);
    }

    private static void SafeDestroy(Object o)
    {
        if (o == null) return;
        if (Application.isPlaying) Destroy(o);
        else DestroyImmediate(o);
    }

    private static void SafeDestroyGo(GameObject go)
    {
        if (go == null) return;
        if (Application.isPlaying) Destroy(go);
        else DestroyImmediate(go);
    }
}
