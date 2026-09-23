using UnityEngine;

// 衝突のない見た目専用の飛散。物理形状の生成・剛体登録をせず、同じ初速から移動する。
public class SlicePieceDecay : MonoBehaviour
{
    public float life = 1.2f, fadeStart = .6f;
    float age, damping;
    Vector3 velocity, angularVelocity;
    bool gravity, released;
    MeshRenderer mr;
    Material ownedMat;
    Mesh ownedMesh;
    Color baseColor;
    // Perfect 時の縁の発光。元の発光色へ時間で戻す。発光プロパティの無い材質では何もしない。
    Color baseEmission, flashColor;
    bool hasEmission;
    float flashAge = float.PositiveInfinity, flashSeconds = .3f;
    internal NoteFragmentPool Pool;
    internal void PrepareForRent() { released=false; }
    public Mesh ReusableMesh => ownedMesh != null ? ownedMesh : ownedMesh = new Mesh { name="NoteSlice", hideFlags=HideFlags.DontSave };
    void Awake() { mr=GetComponent<MeshRenderer>(); }
    public void SetOwnedMesh(Mesh mesh)
    {
        if(ownedMesh != null && ownedMesh != mesh) UISkinKit.SafeDestroy(ownedMesh);
        ownedMesh=mesh;
    }
    public void SetOwnedMaterial(Material material)
    {
        if(ownedMat != null && ownedMat != material) UISkinKit.SafeDestroy(ownedMat);
        ownedMat=material; ApplyMaterial();
    }
    internal void CopyMaterial(Material source)
    {
        if(source == null) return;
        if(ownedMat == null) ownedMat=new Material(source);
        else { if(ownedMat.shader != source.shader) ownedMat.shader=source.shader; ownedMat.CopyPropertiesFromMaterial(source); }
        ApplyMaterial();
    }
    void ApplyMaterial()
    {
        if(mr == null) mr=GetComponent<MeshRenderer>();
        if(mr != null) mr.sharedMaterial=ownedMat;
        baseColor=ownedMat != null && ownedMat.HasProperty("_BaseColor") ? ownedMat.GetColor("_BaseColor") : Color.white;
        hasEmission=ownedMat != null && ownedMat.HasProperty("_EmissionColor");
        baseEmission=hasEmission ? ownedMat.GetColor("_EmissionColor") : Color.black;
        flashAge=float.PositiveInfinity;
    }
    public bool IsFlashing => flashAge < flashSeconds;
    // 切断片の縁を短く発光させる(GameplayCutFeedback が Perfect のときだけ呼ぶ)。
    public void Flash(Color color, float seconds)
    {
        if(released || !hasEmission || ownedMat == null) return;
        flashColor=color; flashSeconds=Mathf.Max(.01f,seconds); flashAge=0;
        ownedMat.EnableKeyword("_EMISSION");
        ownedMat.SetColor("_EmissionColor",flashColor*2.2f);
    }
    void StepFlash(float dt)
    {
        if(!IsFlashing) return;
        flashAge+=dt;
        float k=1-Mathf.Clamp01(flashAge/flashSeconds);
        ownedMat.SetColor("_EmissionColor",IsFlashing ? Color.Lerp(baseEmission,flashColor*2.2f,k*k) : baseEmission);
    }
    public void Launch(NoteFragmentPool pool, Vector3 speed, Vector3 spin, bool useGravity, float duration, float fade, float drag)
    {
        // 通常の切断片とロングの細片を共通で速く飛ばす。寿命と回転は維持する。
        Pool=pool; velocity=speed * 1.9f; angularVelocity=spin; gravity=useGravity; life=duration; fadeStart=fade;
        damping=drag; age=0; released=false; flashAge=float.PositiveInfinity; gameObject.SetActive(true);
    }
    void Update() { Step(Time.deltaTime); }
    public void Step(float dt)
    {
        if(released || dt < 0 || float.IsNaN(dt) || float.IsInfinity(dt)) return;
        age+=dt;
        StepFlash(dt);
        if(gravity) velocity+=Physics.gravity*dt;
        velocity*=Mathf.Exp(-damping*dt); transform.position+=velocity*dt;
        transform.Rotate(angularVelocity*(Mathf.Rad2Deg*dt),Space.World);
        angularVelocity*=Mathf.Exp(-.2f*dt);
        if(ownedMat != null && age > fadeStart)
        {
            var color=baseColor; color.a*=Mathf.Clamp01(1-(age-fadeStart)/Mathf.Max(.0001f,life-fadeStart));
            if(ownedMat.HasProperty("_BaseColor")) ownedMat.SetColor("_BaseColor",color);
            else if(ownedMat.HasProperty("_Color")) ownedMat.SetColor("_Color",color);
        }
        if(age >= life) Release();
    }
    public void Release()
    {
        if(released) return; released=true;
        if(Pool != null) Pool.Return(this); else UISkinKit.SafeDestroy(gameObject);
    }
    void OnDestroy() { UISkinKit.SafeDestroy(ownedMat); UISkinKit.SafeDestroy(ownedMesh); }
}
