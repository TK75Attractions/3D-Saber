using UnityEngine;

// タイトル画面に浮かぶ「切ってスタート」ノーツ。
// 本編と同じ CuttableNote + NoteVisuals + MeshSlicer をそのまま使うので、
// 切ると本編同様にスライス+破片が飛び散る。最初のカットが OnSlashed を1回だけ発火する。
// ゆっくり上下に浮遊し、わずかにヨー回転して立体感を出す。
public class TitleStartNote : MonoBehaviour
{
    public float bobAmplitude = 0.12f;
    public float bobHz = 0.55f;
    public float yawAmplitudeDeg = 14f;
    public float yawHz = 0.35f;

    public CuttableNote Note { get; private set; }
    public event System.Action OnSlashed;

    private Vector3 basePos;
    private float age;
    private bool fired;

    public static TitleStartNote Build(Vector3 position, Color color)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = "TitleStartNote";
        go.transform.position = position;
        go.transform.localScale = Vector3.one * 1.1f;

        // ベース色を仕込む(NoteVisuals.Awake が継承して結晶外観を構築する)
        var mr = go.GetComponent<MeshRenderer>();
        var sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var mat = new Material(sh);
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        else if (mat.HasProperty("_Color")) mat.color = color;
        mr.sharedMaterial = mat;

        var note = go.AddComponent<CuttableNote>();
        note.IsJudgeable = true;

        go.AddComponent<NoteVisuals>();
        // NoteVisuals が自前マテリアルへ差し替え済みなら、仕込み用の一時マテリアルは破棄してよい
        if (mr.sharedMaterial != mat)
        {
            if (Application.isPlaying) Destroy(mat);
            else DestroyImmediate(mat);
        }

        var self = go.AddComponent<TitleStartNote>();
        self.Note = note;
        self.basePos = position;
        note.OnCut += self.HandleCut;
        return self;
    }

    void Awake()
    {
        if (Note == null) Note = GetComponent<CuttableNote>();
        if (basePos == Vector3.zero) basePos = transform.position;
    }

    void Update()
    {
        if (fired) return;
        age += Time.deltaTime;
        float bob = Mathf.Sin(age * bobHz * 2f * Mathf.PI) * bobAmplitude;
        transform.position = basePos + new Vector3(0f, bob, 0f);
        float yaw = Mathf.Sin(age * yawHz * 2f * Mathf.PI) * yawAmplitudeDeg;
        transform.rotation = Quaternion.Euler(0f, yaw, 0f);
    }

    private void HandleCut(CuttableNote n, Vector3 point, Vector3 velocity)
    {
        if (fired) return;
        fired = true;
        OnSlashed?.Invoke();
    }

    // キーボード(Enter/Space)からの疑似スラッシュ。実カットと同じ砕け散り演出を通る。
    public void SlashProgrammatically()
    {
        if (Note == null || Note.IsCut || Note.IsMissed) return;
        Note.Cut(transform.position, new Vector3(9f, 3f, 0f));
    }
}
