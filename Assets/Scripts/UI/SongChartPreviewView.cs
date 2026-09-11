using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using Object=UnityEngine.Object;

// 選曲UIから離れた描画専用の3D空間。譜面と本編NoteVisualsを使うが判定・スコア・効果音には接続しない。
public sealed class SongChartPreviewView : IDisposable
{
    public const int PreviewLayer=30;
    public const float WorldDepth=10000;
    private sealed class Entry
    {
        public NoteData data;
        public double hit,linger;
        public GameObject body;
        public CuttableNote note;
        public TextMeshPro label;
        public Vector3 scale;
        public int remaining;
    }
    private readonly RectTransform panel;
    private readonly RawImage image;
    private readonly TextMeshProUGUI heading,clock;
    private readonly RectTransform progress;
    private readonly GameObject root;
    private readonly Transform noteRoot;
    private readonly Camera camera;
    private readonly RenderTexture texture;
    private readonly FloorRenderer floor;
    private readonly Material gateMaterial;
    private readonly List<Entry> entries=new List<Entry>();
    private readonly List<Entry> live=new List<Entry>();
    private readonly HashSet<Material> arrowMaterials=new HashSet<Material>();
    private StagePerformanceTimeline timeline;
    private SongPreviewWindow window;
    private float coordScale,approach;
    private int next;
    private double lastTime,lastHit=-100;
    private bool disposed;
    public bool IsVisible => panel!=null && panel.gameObject.activeSelf;
    public RenderTexture Texture => texture;
    public GameObject WorldRoot => root;
    public int VisibleNoteCount => live.Count;
    public int ExcerptNoteCount => entries.Count;
    public double DisplayedSongTime { get; private set; }
    public Camera PreviewCamera => camera;

    public SongChartPreviewView(RectTransform mount)
    {
        panel=mount;
        var viewport=SongSelectVisuals.Rect(panel,"ChartPreviewImage",new Vector2(0,-2),new Vector2(572,276));
        image=viewport.gameObject.AddComponent<RawImage>(); image.raycastTarget=false;
        heading=SongSelectVisuals.Label(panel,"PreviewHeading","CHART PREVIEW",14,new Vector2(-60,146),new Vector2(448,20),SongSelectVisuals.Muted);
        clock=SongSelectVisuals.Label(panel,"PreviewClock","",13,new Vector2(206,146),new Vector2(152,20),SongSelectVisuals.Muted,TextAlignmentOptions.MidlineRight);
        progress=SongSelectVisuals.Rect(panel,"PreviewProgress",new Vector2(-286,-147),new Vector2(572,3));
        progress.pivot=new Vector2(0,.5f);
        var bar=progress.gameObject.AddComponent<Image>(); bar.color=new Color(.45f,.83f,.96f); bar.raycastTarget=false;
        texture=new RenderTexture(768,384,24,RenderTextureFormat.ARGB32){name="SongSelectChartPreview",antiAliasing=1}; texture.Create(); image.texture=texture;
        root=new GameObject("SongSelectPreviewWorld"); root.transform.position=new Vector3(0,0,WorldDepth);
        noteRoot=new GameObject("PreviewNotes").transform; noteRoot.SetParent(root.transform,false);
        var cameraObject=new GameObject("ChartPreviewCamera"); cameraObject.transform.SetParent(root.transform,false);
        camera=cameraObject.AddComponent<Camera>(); camera.transform.localPosition=new Vector3(0,1.6f,-7);
        camera.transform.localRotation=Quaternion.Euler(6,0,0); camera.fieldOfView=60; camera.aspect=2;
        camera.nearClipPlane=.1f; camera.farClipPlane=100; camera.clearFlags=CameraClearFlags.SolidColor;
        camera.backgroundColor=new Color(.018f,.032f,.048f); camera.cullingMask=1<<PreviewLayer;
        camera.targetTexture=texture; camera.depth=-20; camera.allowMSAA=false; camera.allowHDR=false;
        var cameraData=camera.GetUniversalAdditionalCameraData(); cameraData.renderPostProcessing=false; cameraData.renderShadows=false; cameraData.volumeLayerMask=0;
        var stage=new GameObject("PreviewStage"); stage.transform.SetParent(root.transform,false);
        floor=stage.AddComponent<FloorRenderer>(); floor.randomizeOnPlay=false; floor.Build(StageTheme.ObsidianRelay);
        gateMaterial=new Material(Shader.Find("Universal Render Pipeline/Unlit")){name="PreviewGate"};
        gateMaterial.SetColor("_BaseColor",new Color(.18f,.37f,.44f));
        // 判定面は小窓でも判別できる薄いコーナー枠。背景と違い、仮想カット時だけ短く光らせる。
        foreach(int x in new[]{-1,1}) foreach(int y in new[]{-1,1})
        {
            Box("GateCorner",new Vector3(x*4.95f,y*2.75f,0),new Vector3(1.1f,.045f,.04f));
            Box("GateCorner",new Vector3(x*5.5f,y*2.25f,0),new Vector3(.045f,1,.04f));
        }
        SetLayer(root); Hide();
    }

    public void Prepare(ChartData chart,SongPreviewWindow excerpt,StagePerformanceTimeline performance,string difficulty)
    {
        ClearNotes(); window=excerpt; timeline=performance; coordScale=chart.coordScale;
        approach=Mathf.Clamp(GameSession.NoteApproachTime,.5f,5f);
        foreach(var note in chart.notes.Where(n=>SongPreviewWindow.Intersects(chart,n,window,approach)))
            entries.Add(new Entry{data=note,hit=SongPreviewWindow.NoteTime(chart,note),linger=SongPreviewWindow.Linger(note)});
        entries.Sort((a,b)=>a.hit.CompareTo(b.hit)); next=0; lastTime=window.Start; lastHit=-100;
        heading.text="AUTO PREVIEW  /  "+(string.Equals(difficulty,"Hard",StringComparison.OrdinalIgnoreCase)?"MASTER":(difficulty??"NORMAL").ToUpperInvariant());
        root.SetActive(true); Tick(window.Start); camera.enabled=false;
    }

    public void Show() { if(disposed) return; root.SetActive(true); panel.gameObject.SetActive(true); camera.enabled=true; }
    public void Hide()
    {
        if(disposed) return;
        if(camera!=null) camera.enabled=false;
        if(panel!=null) panel.gameObject.SetActive(false);
        ClearNotes(); if(root!=null) root.SetActive(false);
    }

    public void Tick(double songTime)
    {
        if(disposed || !window.IsValid || double.IsNaN(songTime) || double.IsInfinity(songTime)) return;
        // 通常再生は単調増加。確認用の巻き戻しは同じ抜粋を再構築して再現する。
        if(songTime<lastTime-.001)
        {
            foreach(var e in live) DestroyEntry(e);
            live.Clear(); next=0; lastHit=-100;
        }
        lastTime=DisplayedSongTime=songTime;
        while(next<entries.Count && entries[next].hit-approach<=songTime)
        {
            var e=entries[next++]; if(e.hit+e.linger+.12<songTime) continue;
            Spawn(e); live.Add(e);
        }
        for(int i=live.Count-1;i>=0;i--)
        {
            var e=live[i]; double elapsed=songTime-e.hit;
            if(elapsed>e.linger+.12) { DestroyEntry(e); live.RemoveAt(i); continue; }
            float z=elapsed<0?(float)(-elapsed*20/approach):e.linger>0?-(float)Math.Min(1,elapsed/e.linger):0;
            e.body.transform.localPosition=new Vector3(e.data.x*coordScale,e.data.y*coordScale,z);
            int remaining=e.data.count>1 && elapsed>=0 ? Math.Max(0,e.data.count-1-(int)Math.Floor(elapsed/Math.Max(.001,e.linger/(e.data.count-1)))) : elapsed>=0?0:Math.Max(1,e.data.count);
            if(remaining<e.remaining) { lastHit=songTime; e.remaining=remaining; e.note.RemainingCuts=remaining; }
            if(e.label!=null)
            {
                e.label.transform.position=e.body.transform.position+LongNoteCountStyle.WorldOffset;
                e.label.text=Math.Max(0,remaining).ToString(); e.label.gameObject.SetActive(remaining>0);
            }
            float shrink=elapsed>e.linger?Mathf.Clamp01(1-(float)((elapsed-e.linger)/.12)):1;
            e.body.transform.localScale=e.scale*Mathf.Max(.001f,shrink);
        }
        float flash=Mathf.Clamp01(1-(float)((songTime-lastHit)/.14));
        gateMaterial.SetColor("_BaseColor",Color.Lerp(new Color(.18f,.37f,.44f),new Color(.72f,.93f,1f),flash));
        floor.Tick(songTime,timeline!=null?timeline.Evaluate(songTime):0);
        progress.sizeDelta=new Vector2(572*Mathf.Clamp01((float)((songTime-window.Start)/window.Duration)),3);
        int seconds=Mathf.FloorToInt((float)songTime); clock.text=$"{seconds/60}:{seconds%60:00}  /  {window.Duration:0}s";
    }

    private void Spawn(Entry e)
    {
        var go=GameObject.CreatePrimitive(PrimitiveType.Cube); go.name="PreviewNote"; go.SetActive(false);
        go.transform.SetParent(noteRoot,false); DisableColliders(go);
        var note=go.AddComponent<CuttableNote>(); note.enabled=false; note.IsJudgeable=false;
        note.HitTime=e.hit; note.RequiredHand=SaberHandHelper.FromColor(e.data.color);
        note.RequiredCutCount=note.RemainingCuts=Math.Max(1,e.data.count); note.RequiredDirection=CutDirectionHelper.Parse(e.data.direction);
        note.IsGold=string.Equals(e.data.color,"gold",StringComparison.OrdinalIgnoreCase);
        var visuals=go.AddComponent<NoteVisuals>(); visuals.inheritColorFromMainRenderer=false; visuals.baseColor=SaberHandHelper.HandColor(SaberHand.Right);
        e.scale=new Vector3(.8f,.8f,.8f*(e.data.count>1?Mathf.Clamp(1+(float)e.linger/.7f,1,6):1));
        if(DisplaySettings.ProjectorMode) { e.scale.x*=ProjectorMode.NoteScale; e.scale.y*=ProjectorMode.NoteScale; }
        go.transform.localScale=e.scale; go.SetActive(true);
        if(note.RequiredDirection!=CutDirection.None)
        {
            NoteSpawner.BuildArrow(go.transform,note.RequiredDirection);
            foreach(var renderer in go.transform.Find("Arrow").GetComponentsInChildren<Renderer>()) arrowMaterials.Add(renderer.sharedMaterial);
        }
        if(e.data.count>1)
        {
            var label=new GameObject("PreviewLongCount"); label.transform.SetParent(noteRoot,false);
            e.label=label.AddComponent<TextMeshPro>(); LongNoteCountStyle.Apply(e.label); SetLayer(label);
        }
        e.body=go; e.note=note; e.remaining=Math.Max(1,e.data.count);
        foreach(var other in live)
            if(other.note!=null && Math.Abs(other.hit-e.hit)<=NoteSpawner.SimultaneousEpsilonSeconds)
            {
                var link=SimultaneousNoteLink.Create(other.note,note,noteRoot); SetLayer(link.gameObject);
            }
        DisableColliders(go); SetLayer(go);
    }

    private void Box(string name,Vector3 position,Vector3 scale)
    {
        var go=GameObject.CreatePrimitive(PrimitiveType.Cube); go.name=name; go.transform.SetParent(root.transform,false);
        go.transform.localPosition=position; go.transform.localScale=scale; go.GetComponent<Renderer>().sharedMaterial=gateMaterial; DisableColliders(go);
    }
    private static void SetLayer(GameObject go) { foreach(var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer=PreviewLayer; }
    private static void DisableColliders(GameObject go) { foreach(var c in go.GetComponentsInChildren<Collider>(true)) { c.enabled=false; Destroy(c); } }
    private static void Destroy(Object value) { if(value==null) return; if(Application.isPlaying) Object.Destroy(value); else Object.DestroyImmediate(value); }
    private void DestroyEntry(Entry e)
    {
        if(e.body!=null) { e.body.SetActive(false); Destroy(e.body); e.body=null; }
        if(e.label!=null) { Destroy(e.label.gameObject); e.label=null; }
    }
    private void ClearNotes()
    {
        foreach(var e in live) DestroyEntry(e); live.Clear(); entries.Clear(); next=0;
        if(noteRoot!=null) foreach(Transform child in noteRoot) { child.gameObject.SetActive(false); Destroy(child.gameObject); }
        foreach(var material in arrowMaterials) Destroy(material); arrowMaterials.Clear();
    }
    public void Dispose()
    {
        if(disposed) return; Hide(); disposed=true;
        if(image!=null) image.texture=null;
        if(camera!=null) camera.targetTexture=null;
        texture.Release(); Destroy(texture); Destroy(gateMaterial); Destroy(root);
        if(panel!=null) Destroy(panel.gameObject);
    }
}
