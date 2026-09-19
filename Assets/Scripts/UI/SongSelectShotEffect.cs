using UnityEngine;

// 発射音と12個の立体破片を一組だけ再利用する。シーン終了時に音・メッシュ・素材を解放。
public sealed class SongSelectShotEffect : MonoBehaviour
{
    public const float Duration = .48f;
    const int Count = 12;
    Mesh mesh;
    Material material;
    AudioSource source;
    AudioClip shot;
    readonly Vector3[] vertices = new Vector3[Count * 24];
    readonly Color[] colors = new Color[Count * 24];
    readonly Vector3[] uvs = new Vector3[Count * 24];
    readonly int[] triangles = new int[Count * 36];
    readonly Vector3[] starts = new Vector3[Count], velocities = new Vector3[Count];
    readonly Quaternion[] rotations = new Quaternion[Count];
    static readonly Vector3[] Cube = { new Vector3(-1,-1,-1),new Vector3(1,-1,-1),new Vector3(1,1,-1),new Vector3(-1,1,-1),
        new Vector3(-1,-1,1),new Vector3(1,-1,1),new Vector3(1,1,1),new Vector3(-1,1,1) };
    static readonly int[] Faces = { 0,3,2,1, 1,2,6,5, 5,6,7,4, 4,7,3,0, 3,7,6,2, 4,0,1,5 };
    float age = Duration, size;
    Color tint;
    public bool IsActive => age < Duration;
    public AudioClip Clip => shot;

    void Awake()
    {
        mesh = new Mesh { name = "MenuShotFragments" }; mesh.MarkDynamic();
        gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
        material = new Material(Resources.Load<Shader>("Effects/GameplayCutAccent")) { name = "MenuShotMaterial" };
        gameObject.AddComponent<MeshRenderer>().sharedMaterial = material;
        source = gameObject.AddComponent<AudioSource>(); source.playOnAwake = false;
        for (int i = 0; i < Count * 6; i++)
        {
            int v = i * 4, t = i * 6;
            triangles[t]=v; triangles[t+1]=v+1; triangles[t+2]=v+2;
            triangles[t+3]=v; triangles[t+4]=v+2; triangles[t+5]=v+3;
        }
        for (int i = 0; i < uvs.Length; i++) uvs[i] = new Vector3(.5f, .5f, 1);
    }

    public void Play(CuttableNote note)
    {
        var visual = note.GetComponent<NoteVisuals>();
        tint = visual != null ? visual.baseColor : SongSelectVisuals.Accent;
        size = Mathf.Max(.08f, note.transform.lossyScale.x) * .19f;
        for (int i = 0; i < Count; i++)
        {
            float angle = i * 2.399963f;
            Vector3 direction = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), (i % 3 - 1) * .38f).normalized;
            starts[i] = note.transform.position + direction * size * .65f;
            velocities[i] = direction * size * (8 + i % 4 * 2);
            rotations[i] = note.transform.rotation * Quaternion.Euler(i * 37, i * 71, i * 23);
        }
        age = 0; Tick(0);
        if (shot == null) shot = BuildSound();
        source.PlayOneShot(shot, .42f);
    }

    // 低い衝撃に、急降下する電子音と短い破砕音を重ねる。乱数は局所で固定する。
    static AudioClip BuildSound()
    {
        const int rate = 44100;
        var samples = new float[(int)(rate * .28f)];
        var noise = new System.Random(71);
        double phase = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            float t = i / (float)rate;
            phase += 2 * System.Math.PI * (150 + 1700 * Mathf.Exp(-t * 29)) / rate;
            float zap = (float)System.Math.Sin(phase) * Mathf.Exp(-t * 18) * .55f;
            float thump = Mathf.Sin(2 * Mathf.PI * 95 * t) * Mathf.Exp(-t * 36) * .4f;
            float crack = ((float)noise.NextDouble() * 2 - 1) * Mathf.Exp(-t * 32) * .24f;
            samples[i] = Mathf.Clamp((zap + thump + crack) * Mathf.Min(1, t * 1400) * Mathf.Clamp01((.28f - t) / .03f), -.95f, .95f);
        }
        var clip = AudioClip.Create("menu_aim_shot", samples.Length, 1, rate, false); clip.SetData(samples, 0); return clip;
    }

    void Update() { Tick(Time.unscaledDeltaTime); }
    public void Tick(float dt)
    {
        if (age >= Duration) return;
        age += dt;
        if (age >= Duration) { mesh.Clear(); return; }
        float fade = Mathf.Clamp01((Duration - age) / .25f);
        int visible = DisplaySettings.ReducedEffects ? 6 : Count;
        for (int i = 0; i < Count; i++)
        {
            Vector3 center = starts[i] + velocities[i] * age + Vector3.down * age * age * .5f;
            Quaternion rotation = rotations[i] * Quaternion.Euler(age * 180, age * 240, age * 120);
            for (int f = 0; f < 6; f++)
                for (int c = 0; c < 4; c++)
                {
                    int v = i * 24 + f * 4 + c;
                    Vector3 corner = Vector3.Scale(Cube[Faces[f * 4 + c]], new Vector3(1, .72f, .6f));
                    vertices[v] = center + rotation * corner * size * (1 - age * .5f);
                    Color shade = tint * (f == 0 ? 1.15f : .55f + f * .08f);
                    shade.a = i < visible ? fade : 0; colors[v] = shade;
                }
        }
        mesh.vertices = vertices; mesh.colors = colors; mesh.SetUVs(0, uvs); mesh.triangles = triangles; mesh.RecalculateBounds();
    }
    void OnDestroy() { UISkinKit.SafeDestroy(mesh); UISkinKit.SafeDestroy(material); UISkinKit.SafeDestroy(shot); }
}
