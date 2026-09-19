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
    }
    public void Launch(NoteFragmentPool pool, Vector3 speed, Vector3 spin, bool useGravity, float duration, float fade, float drag)
    {
        Pool=pool; velocity=speed; angularVelocity=spin; gravity=useGravity; life=duration; fadeStart=fade;
        damping=drag; age=0; released=false; gameObject.SetActive(true);
    }
    void Update() { Step(Time.deltaTime); }
    public void Step(float dt)
    {
        if(released || dt < 0 || float.IsNaN(dt) || float.IsInfinity(dt)) return;
        age+=dt;
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
