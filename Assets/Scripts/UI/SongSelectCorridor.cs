using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using Object = UnityEngine.Object;

// 選曲専用の小さな3D世界をRTへ描く。本編のカメラ・霧・ポスト処理は変更しない。
public sealed class SongSelectCorridor : MonoBehaviour
{
    static readonly Color[] DownbeatColors = { Color.white, SongSelectSkin.Cyan, new Color(1,.6f,.68f), new Color(.6f,.77f,1) };

    sealed class Geometry
    {
        public readonly List<Vector3> vertices = new List<Vector3>();
        readonly List<Vector2> uv = new List<Vector2>();
        readonly List<Color> colors = new List<Color>();
        readonly List<int> indices = new List<int>();
        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color)
        {
            int i = vertices.Count; vertices.AddRange(new[] { a, b, c, d });
            uv.AddRange(new[] { new Vector2(0,0), new Vector2(1,0), new Vector2(1,1), new Vector2(0,1) });
            for (int n = 0; n < 4; n++) colors.Add(color.linear);
            indices.AddRange(new[] { i, i+1, i+2, i, i+2, i+3 });
        }
        public void Box(Vector3 p, Vector3 size, Color color)
        {
            Vector3 a = p-size*.5f, b = p+size*.5f;
            Quad(new Vector3(a.x,a.y,a.z), new Vector3(b.x,a.y,a.z), new Vector3(b.x,b.y,a.z), new Vector3(a.x,b.y,a.z), color);
            Quad(new Vector3(b.x,a.y,b.z), new Vector3(a.x,a.y,b.z), new Vector3(a.x,b.y,b.z), new Vector3(b.x,b.y,b.z), Shade(color,.7f));
            Quad(new Vector3(a.x,a.y,b.z), new Vector3(a.x,a.y,a.z), new Vector3(a.x,b.y,a.z), new Vector3(a.x,b.y,b.z), Shade(color,.72f));
            Quad(new Vector3(b.x,a.y,a.z), new Vector3(b.x,a.y,b.z), new Vector3(b.x,b.y,b.z), new Vector3(b.x,b.y,a.z), Shade(color,.85f));
            Quad(new Vector3(a.x,b.y,a.z), new Vector3(b.x,b.y,a.z), new Vector3(b.x,b.y,b.z), new Vector3(a.x,b.y,b.z), Shade(color,1.3f));
        }
        public void Beam(Vector3 a, Vector3 b, float width, Color color)
        {
            Vector3 n = Vector3.Cross((b-a).normalized, Vector3.forward).normalized;
            if (n.sqrMagnitude < .1f) n = Vector3.right;
            n *= width*.5f; Quad(a-n, a+n, b+n, b-n, color);
        }
        public Mesh Mesh()
        {
            var mesh = new Mesh { name = "Select corridor geometry" };
            mesh.SetVertices(vertices); mesh.SetUVs(0, uv); mesh.SetColors(colors); mesh.SetTriangles(indices,0); mesh.RecalculateBounds(); return mesh;
        }
        static Color Shade(Color c, float k) => new Color(c.r*k,c.g*k,c.b*k,c.a);
    }
    sealed class LightPair { public Material core, halo; public Color color; }
    readonly List<Material> materials = new List<Material>();
    readonly Dictionary<Material, Material> reflectionMaterials = new Dictionary<Material, Material>();
    readonly List<Mesh> meshes = new List<Mesh>();
    readonly List<Transform> rings = new List<Transform>(), lasers = new List<Transform>();
    readonly List<Transform> reflected = new List<Transform>(), originals = new List<Transform>();
    readonly List<Material> runnerLights = new List<Material>();
    readonly List<float> ringAngles = new List<float>();
    readonly List<double> waveTimes = new List<double>();
    LightPair left, right, ringLight, fanLight, edge;
    Material centerHaze, leftHaze, rightHaze;
    GameObject world;
    Transform geometryRoot;
    Camera view;
    RenderTexture texture;
    SongSelectController controller;
    SongSelectBeatTimeline timeline;
    List<SongSelectBeatTimeline.BeatEvent> beats = new List<SongSelectBeatTimeline.BeatEvent>();
    string selectedSong;
    double previousSongTime = double.NaN, windowStart = -1;
    int nextBeat, barNumber;
    float l, r, wall, ring, fan, glow;
    Color ringColor = Color.white;
    public float CenterPulse { get; private set; }
    public RenderTexture Texture => texture;
    public Camera ViewCamera => view;
    public int FiredBeats { get; private set; }

    public static SongSelectCorridor Build(RectTransform parent, SongSelectController ctl)
    {
        var rect = SongSelectVisuals.Rect(parent, "CorridorBackground", Vector2.zero, new Vector2(1920,1080)); rect.SetAsFirstSibling();
        var image = rect.gameObject.AddComponent<RawImage>(); image.raycastTarget = false;
        var result = rect.gameObject.AddComponent<SongSelectCorridor>(); result.controller = ctl;
        result.Initialize(); image.texture = result.texture; return result;
    }
    Material Material(Color color, int glowMode = 0, bool opaque = false, bool fog = true)
    {
        var m = new Material(Shader.Find("Saber/Song Select Corridor")) { name = "Select corridor light", renderQueue = opaque ? 2000 : 3200 };
        m.SetColor("_Tint", color); m.SetFloat("_Glow", glowMode); m.SetFloat("_Fog", fog ? 1 : 0);
        if (opaque) { m.SetFloat("_SrcBlend",1); m.SetFloat("_DstBlend",0); m.SetFloat("_ZWrite",1); }
        materials.Add(m); return m;
    }
    LightPair Light(Color color)
    {
        return new LightPair { color=color, core=Material(Color.Lerp(color,Color.white,.5f)), halo=Material(new Color(color.r,color.g,color.b,.15f),1) };
    }
    Transform Render(string name, Geometry geometry, Material material, Transform parent, bool mirror = false)
    {
        var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer)); go.layer = 30; go.transform.SetParent(parent,false);
        var mesh = geometry.Mesh(); meshes.Add(mesh); go.GetComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.GetComponent<MeshRenderer>(); renderer.sharedMaterial = material; renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
        if (mirror)
        {
            var copy = new GameObject(name+"Reflection",typeof(MeshFilter),typeof(MeshRenderer)); copy.layer=30; copy.transform.SetParent(geometryRoot,false);
            // 映り込み→半透明の床→実際の光の順で描き、床より上の光を床で暗くしない。
            if (!reflectionMaterials.TryGetValue(material, out var reflection))
            {
                reflection = new Material(material) { name = "Select corridor reflection", renderQueue = 2900 };
                materials.Add(reflection); reflectionMaterials.Add(material, reflection);
            }
            copy.GetComponent<MeshFilter>().sharedMesh=mesh; copy.GetComponent<MeshRenderer>().sharedMaterial=reflection;
            copy.GetComponent<MeshRenderer>().shadowCastingMode=ShadowCastingMode.Off;
            originals.Add(go.transform); reflected.Add(copy.transform);
        }
        return go.transform;
    }
    void Beam(Geometry core, Geometry halo, Vector3 a, Vector3 b, float width, float haloWidth)
    { core.Beam(a,b,width,Color.white); halo.Beam(a,b,haloWidth,Color.white); }
    void Initialize()
    {
        world = new GameObject("SongSelectCorridorWorld"); world.transform.position = new Vector3(20000,0,0);
        // Unityはカメラの前方が+Zなので、-Zを見る試作と左右を合わせる。
        geometryRoot = new GameObject("CorridorGeometry").transform; geometryRoot.SetParent(world.transform,false);
        geometryRoot.localScale = new Vector3(-1,1,1);
        var cameraObject = new GameObject("SongSelectCorridorCamera",typeof(Camera)); cameraObject.transform.SetParent(world.transform,false);
        view = cameraObject.GetComponent<Camera>(); view.transform.localPosition=new Vector3(0,3,10);
        view.transform.localRotation=Quaternion.LookRotation(new Vector3(0,-10.34f,-110));
        view.fieldOfView=60; view.aspect=16f/9; view.nearClipPlane=.1f; view.farClipPlane=440;
        view.clearFlags=CameraClearFlags.SolidColor; view.backgroundColor=new Color(.009f,.015f,.03f); view.cullingMask=1<<30;
        view.depth=-30; view.allowHDR=false; view.allowMSAA=false;
        var data=view.GetUniversalAdditionalCameraData(); data.renderPostProcessing=false; data.renderShadows=false; data.volumeLayerMask=0;
        texture=new RenderTexture(1920,1080,24,RenderTextureFormat.ARGB32) { name="Song Select Corridor",antiAliasing=1 }; texture.Create(); view.targetTexture=texture;
        left=Light(new Color(1,.18f,.30f)); right=Light(new Color(.18f,.55f,1)); ringLight=Light(SongSelectSkin.Cyan); fanLight=Light(Color.white); edge=Light(new Color(.65f,1,1));
        var solid=new Geometry();
        solid.Box(new Vector3(0,-.35f,-190),new Vector3(7,.7f,400),new Color(.039f,.067f,.114f));
        var leftCore=new Geometry(); var leftHalo=new Geometry(); var rightCore=new Geometry(); var rightHalo=new Geometry();
        for(int sign=-1;sign<=1;sign+=2)
            for(int i=0;i<8;i++)
            {
                float z=-16-19*i;
                solid.Box(new Vector3(sign*12.5f,5.9f,z),new Vector3(1.4f,15,7),new Color(.039f,.067f,.125f));
                Beam(sign<0?leftCore:rightCore,sign<0?leftHalo:rightHalo,new Vector3(sign*11.72f,-1.2f,z+3.55f),new Vector3(sign*11.72f,12.8f,z+3.55f),.10f,1.3f);
            }
        Render("Architecture",solid,Material(Color.white,0,true),geometryRoot);
        Render("LeftWallCore",leftCore,left.core,geometryRoot,true); Render("LeftWallHalo",leftHalo,left.halo,geometryRoot,true);
        Render("RightWallCore",rightCore,right.core,geometryRoot,true); Render("RightWallHalo",rightHalo,right.halo,geometryRoot,true);
        var floor=new Geometry(); floor.Quad(new Vector3(-350,-1.6f,50),new Vector3(350,-1.6f,50),new Vector3(350,-1.6f,-650),new Vector3(-350,-1.6f,-650),Color.white);
        var floorMaterial=Material(new Color(1,1,1,.86f)); floorMaterial.SetFloat("_Floor",1); floorMaterial.renderQueue=3100;
        Render("ReflectiveFloor",floor,floorMaterial,geometryRoot);
        var grid=new Geometry();
        foreach(float x in new[]{-34f,-24,-16,-9,9,16,24,34}) grid.Quad(new Vector3(x-.035f,-1.58f,20),new Vector3(x+.035f,-1.58f,20),new Vector3(x+.035f,-1.58f,-300),new Vector3(x-.035f,-1.58f,-300),Color.white);
        for(int i=0;i<24;i++) { float z=-12-13*i; grid.Quad(new Vector3(-100,-1.58f,z),new Vector3(100,-1.58f,z),new Vector3(100,-1.58f,z-.06f),new Vector3(-100,-1.58f,z-.06f),Color.white); }
        var gridMat=Material(new Color(.118f,.239f,.431f,.32f)); gridMat.renderQueue=3110; Render("FloorGrid",grid,gridMat,geometryRoot);
        var edgeCore=new Geometry(); var edgeHalo=new Geometry();
        for(int sign=-1;sign<=1;sign+=2) Beam(edgeCore,edgeHalo,new Vector3(sign*3.5f,.03f,10),new Vector3(sign*3.5f,.03f,-390),.06f,1.1f);
        Render("RunwayCore",edgeCore,edge.core,geometryRoot,true); Render("RunwayHalo",edgeHalo,edge.halo,geometryRoot,true);
        BuildMotion(); BuildHaze(); UpdateReflection();
    }
    void BuildMotion()
    {
        for(int i=0;i<8;i++)
        {
            var root=new GameObject("RotatingRing"+i).transform; root.SetParent(geometryRoot,false); root.localPosition=new Vector3(0,5.5f,-46-14*i);
            rings.Add(root); ringAngles.Add(0);
            var frame=new Geometry(); var core=new Geometry(); var halo=new Geometry();
            Vector3[] corners={new Vector3(-15,-15,0),new Vector3(15,-15,0),new Vector3(15,15,0),new Vector3(-15,15,0)};
            for(int k=0;k<4;k++)
            {
                var a=corners[k]; var b=corners[(k+1)%4]; frame.Beam(a,b,.45f,new Color(.078f,.114f,.176f));
                Beam(core,halo,Vector3.Lerp(a,b,.04f),Vector3.Lerp(a,b,.25f),.09f,1.1f);
                Beam(core,halo,Vector3.Lerp(a,b,.75f),Vector3.Lerp(a,b,.96f),.09f,1.1f);
            }
            Render("RingFrame",frame,Material(Color.white,0,true),root); Render("RingCore",core,ringLight.core,root,true); Render("RingHalo",halo,ringLight.halo,root,true);
        }
        for(int sign=-1;sign<=1;sign+=2) for(int i=0;i<4;i++)
        {
            var pivot=new GameObject("MovingLaser").transform; pivot.SetParent(geometryRoot,false); pivot.localPosition=new Vector3(sign*(22+7*i),-1.6f,-58-24*i); lasers.Add(pivot);
            var core=new Geometry(); var halo=new Geometry(); Beam(core,halo,Vector3.zero,new Vector3(0,150,0),.05f,1.2f);
            Render("LaserCore",core,sign<0?left.core:right.core,pivot,true); Render("LaserHalo",halo,sign<0?left.halo:right.halo,pivot,true);
        }
        var fanPivot=new GameObject("LightFan").transform; fanPivot.SetParent(geometryRoot,false); fanPivot.localPosition=new Vector3(0,4,-175); rings.Add(fanPivot); ringAngles.Add(0);
        var fanCore=new Geometry(); var fanHalo=new Geometry();
        for(int i=0;i<8;i++) { float a=i*Mathf.PI/4; Beam(fanCore,fanHalo,Vector3.zero,new Vector3(Mathf.Cos(a)*160,Mathf.Sin(a)*160,0),.06f,2); }
        Render("FanCore",fanCore,fanLight.core,fanPivot,true); Render("FanHalo",fanHalo,fanLight.halo,fanPivot,true);
        for(int i=0;i<26;i++)
        {
            var wave=new Geometry();
            foreach(int sign in new[]{-1,1}) wave.Quad(new Vector3(sign*3.5f-.16f,.04f,2-7*i),new Vector3(sign*3.5f+.16f,.04f,2-7*i),new Vector3(sign*3.5f+.16f,.04f,.8f-7*i),new Vector3(sign*3.5f-.16f,.04f,.8f-7*i),Color.white);
            var m=Material(new Color(.7f,1,1,0),2); runnerLights.Add(m); Render("BeatRunner",wave,m,geometryRoot,true);
        }
        var random=new System.Random(90127); var particleMaterial=Material(new Color(.4f,.65f,1,.24f),2);
        // 150粒を一つのメッシュにまとめ、上昇はシェーダーで動かす。
        var particleShape=new Geometry(); particleMaterial.SetFloat("_Drift",1);
        for(int i=0;i<150;i++)
        {
            var p=new Vector3((float)random.NextDouble()*90-45,(float)random.NextDouble()*24-1,(float)random.NextDouble()*-170-12);
            particleShape.Quad(p+new Vector3(-.16f,-.16f,0),p+new Vector3(.16f,-.16f,0),p+new Vector3(.16f,.16f,0),p+new Vector3(-.16f,.16f,0),Color.white);
        }
        var dust=Render("Dust",particleShape,particleMaterial,geometryRoot);
        dust.GetComponent<MeshFilter>().sharedMesh.bounds=new Bounds(new Vector3(0,11,-97),new Vector3(94,28,180));
    }
    void BuildHaze()
    {
        centerHaze=Haze(new Vector3(0,3,-205),new Vector2(90,34),new Color(.27f,1,.97f,.22f));
        leftHaze=Haze(new Vector3(-118,9,-210),new Vector2(190,80),new Color(1,.18f,.3f,.2f));
        rightHaze=Haze(new Vector3(118,9,-210),new Vector2(190,80),new Color(.18f,.55f,1,.2f));
    }
    Material Haze(Vector3 center,Vector2 size,Color color)
    {
        var g=new Geometry(); Vector3 a=new Vector3(size.x/2,0,0),b=new Vector3(0,size.y/2,0);
        g.Quad(center-a-b,center+a-b,center+a+b,center-a+b,Color.white);
        var m=Material(color,2,false,false); Render("DistantHaze",g,m,geometryRoot); return m;
    }
    public void Select(string song)
    {
        selectedSong=song; var charts=new List<ChartData>();
        foreach(var difficulty in SongSelectController.StandardDifficulties)
            try { charts.Add(ChartLoader.LoadFromStreamingAssets(song,difficulty)); } catch(Exception e) when(e is System.IO.IOException || e is ArgumentException) { }
        timeline=new SongSelectBeatTimeline(charts); ResetClock();
    }
    void ResetClock()
    { previousSongTime=double.NaN; windowStart=-1; nextBeat=0; waveTimes.Clear(); l=r=wall=ring=fan=glow=CenterPulse=0; FiredBeats=0; }
    void Update()
    {
        if(view==null) return;
        float dt=Mathf.Max(0,Time.unscaledDeltaTime), t=Time.unscaledTime;
        float decay=Mathf.Exp(-dt/.24f); l*=decay; r*=decay; wall*=decay; ring*=decay; fan*=decay; glow*=decay; CenterPulse*=Mathf.Exp(-dt/.3f);
        var preview=controller!=null?controller.ChartPreview:null;
        if(preview!=null && preview.IsPlaying && preview.SongId==selectedSong && !ScreenTransition.IsBusy)
        {
            double time=preview.SongTime;
            if(windowStart!=preview.Window.Start || !double.IsNaN(previousSongTime) && time<previousSongTime-.001)
            {
                ResetClock(); windowStart=preview.Window.Start;
                beats=timeline.Events(windowStart-.000001,windowStart+preview.Window.Duration);
            }
            // 開始予約中の停止した時計では発火しない。
            if(time>windowStart+.001)
                while(nextBeat<beats.Count && beats[nextBeat].Time<=time)
                {
                    var beat=beats[nextBeat++];
                    if(time-beat.Time>.2) continue;
                    Fire(beat, t-(float)(time-beat.Time));
                }
            previousSongTime=time;
        }
        Tint(left,Mathf.Max(wall,l),.55f,.45f); Tint(right,Mathf.Max(wall,r),.55f,.45f);
        ringLight.color=ringColor; Tint(ringLight,ring,.3f,.7f); Tint(fanLight,fan,.12f,.6f); Tint(edge,glow,.6f,.4f);
        SetAlpha(centerHaze,.22f+.20f*glow); SetAlpha(leftHaze,.2f+.22f*Mathf.Max(glow,.5f*l)); SetAlpha(rightHaze,.2f+.22f*Mathf.Max(glow,.5f*r));
        for(int i=0;i<8;i++)
        {
            float current=rings[i].localEulerAngles.z;
            rings[i].localRotation=Quaternion.Euler(0,0,Mathf.LerpAngle(current,ringAngles[i],1-Mathf.Exp(-dt/(.28f+i*.05f))));
        }
        rings[8].localRotation=Quaternion.Euler(0,0,t*1.8f);
        for(int i=0;i<lasers.Count;i++) lasers[i].localRotation=Quaternion.Euler(0,0,(i<4?1:-1)*(.42f+.06f*(i%4)+.14f*Mathf.Sin(t*.33f+1.9f*(i%4)))*Mathf.Rad2Deg);
        for(int i=0;i<runnerLights.Count;i++)
        {
            float intensity=0;
            foreach(var fired in waveTimes) { float age=t-(float)fired-i*.03f; if(age>=0 && age<.3f) intensity=Mathf.Max(intensity,Mathf.Sin(age/.3f*Mathf.PI)); }
            SetAlpha(runnerLights[i],intensity*.9f);
        }
        // 順序を保って後ろから除去し、毎フレームのキャプチャ付き predicate を作らない。
        for(int i=waveTimes.Count-1;i>=0;i--) if(t-waveTimes[i]>1.2) waveTimes.RemoveAt(i);
        view.transform.localPosition=new Vector3(.3f*Mathf.Sin(t*.11f),3+.14f*Mathf.Sin(t*.17f),10);
        UpdateReflection();
    }
    void Fire(SongSelectBeatTimeline.BeatEvent beat,float time)
    {
        FiredBeats++; CenterPulse=1; waveTimes.Add(time);
        if(beat.Downbeat)
        {
            l=r=wall=ring=fan=glow=1; barNumber++;
            ringColor=DownbeatColors[barNumber%4];
            for(int i=0;i<8;i++) ringAngles[i]+=(barNumber%2==0?1:-1)*22.5f;
        }
        else { if(beat.Beat%2==0) l=.85f; else r=.85f; glow=.5f; }
    }
    static void Tint(LightPair pair,float pulse,float baseLight,float gain)
    {
        Color core=Color.Lerp(pair.color,Color.white,.45f+.35f*pulse)*(baseLight+gain*pulse); core.a=1;
        pair.core.SetColor("_Tint",core); pair.halo.SetColor("_Tint",new Color(pair.color.r,pair.color.g,pair.color.b,.08f+.4f*pulse));
    }
    static void SetAlpha(Material m,float alpha) { var c=m.GetColor("_Tint"); c.a=alpha; m.SetColor("_Tint",c); }
    void UpdateReflection()
    {
        foreach (var pair in reflectionMaterials) pair.Value.SetColor("_Tint", pair.Key.GetColor("_Tint"));
        Matrix4x4 reflect=Matrix4x4.TRS(new Vector3(0,-3.2f,0),Quaternion.identity,new Vector3(1,-1,1));
        for(int i=0;i<reflected.Count;i++)
        {
            var original=originals[i]; var copy=reflected[i];
            var p=geometryRoot.InverseTransformPoint(original.position); copy.localPosition=reflect.MultiplyPoint3x4(p);
            var q=Quaternion.Inverse(geometryRoot.rotation)*original.rotation; copy.localRotation=new Quaternion(-q.x,q.y,-q.z,q.w);
            copy.localScale=new Vector3(1,-1,1);
        }
    }
    void OnDestroy()
    {
        if(view!=null) view.targetTexture=null;
        if(texture!=null) { texture.Release(); UISkinKit.SafeDestroy(texture); }
        if(world!=null) UISkinKit.SafeDestroy(world);
        foreach(var mesh in meshes) UISkinKit.SafeDestroy(mesh);
        foreach(var material in materials) UISkinKit.SafeDestroy(material);
    }
}
