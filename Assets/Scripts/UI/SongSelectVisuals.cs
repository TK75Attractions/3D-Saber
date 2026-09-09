using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// 曲選択専用のデザイン定義。他の画面やノーツの見た目には波及させない。
public static class SongSelectVisuals
{
    public static readonly Color Background = new Color(.027f,.045f,.061f);
    public static readonly Color Surface = new Color(.065f,.098f,.124f);
    public static readonly Color Raised = new Color(.094f,.143f,.173f);
    public static readonly Color Edge = new Color(.21f,.33f,.38f);
    public static readonly Color Accent = new Color(.38f,.81f,.83f);
    public static readonly Color Text = new Color(.91f,.96f,.98f);
    public static readonly Color Muted = new Color(.52f,.66f,.72f);
    public static readonly Color Disabled = new Color(.33f,.42f,.47f);
    public static readonly Color[] Difficulty = { new Color(.48f,.83f,.63f), new Color(.40f,.70f,.96f), new Color(1f,.43f,.48f) };

    public static RectTransform Rect(Transform parent, string name, Vector2 position, Vector2 size)
    {
        var go = new GameObject(name, typeof(RectTransform)); go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>(); rt.anchoredPosition = position; rt.sizeDelta = size; return rt;
    }
    public static SongSelectPanelGraphic Panel(Transform parent, string name, Vector2 position, Vector2 size, Color fill, Color edge, float cut = 10f)
    {
        var rt = Rect(parent, name, position, size); var graphic = rt.gameObject.AddComponent<SongSelectPanelGraphic>();
        graphic.color = fill; graphic.borderColor = edge; graphic.cornerCut = cut; graphic.raycastTarget = false; return graphic;
    }
    public static TextMeshProUGUI Label(Transform parent, string name, string value, float size, Vector2 position, Vector2 dimensions,
        Color color, TextAlignmentOptions alignment = TextAlignmentOptions.MidlineLeft, bool strong = false)
    {
        var t = UISkinKit.MakeTMP(parent, name, value, size, color, alignment, position, dimensions,
            strong ? FontStyles.Bold : FontStyles.Normal, 0f, UISkinKit.FontAsset(strong ? "Oxanium-ExtraBold" : "Oxanium-Bold"));
        t.overflowMode = TextOverflowModes.Ellipsis; return t;
    }
    public static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero;
    }
    public static SongSelectActionStyle StyleAction(Button button, string label, bool primary)
    {
        foreach (Transform child in button.transform) child.gameObject.SetActive(false);
        var image = button.GetComponent<Image>(); if (image != null) image.enabled = false;
        var oldHover = button.GetComponent<UIHoverEffect>(); if (oldHover != null) oldHover.enabled = false;
        var face = Panel(button.transform, "ActionSurface", Vector2.zero, Vector2.zero, Surface, Edge, 9f);
        Stretch(face.rectTransform); face.raycastTarget = true;
        button.targetGraphic = face; button.transition = Selectable.Transition.None;
        var text = Label(button.transform, "ActionLabel", label, primary ? 28 : 20, Vector2.zero, Vector2.zero,
            Text, TextAlignmentOptions.Center, true); Stretch(text.rectTransform);
        var style = button.GetComponent<SongSelectActionStyle>() ?? button.gameObject.AddComponent<SongSelectActionStyle>();
        style.Initialize(button, face, text, primary);
        var dwell = button.GetComponent<SaberDwellTarget>() ?? button.gameObject.AddComponent<SaberDwellTarget>();
        dwell.progressColor = Accent; dwell.dwellSeconds = 1f;
        return style;
    }
}

// 面取りの形状・線幅をリスト、難易度、操作ボタンで共有する。
[RequireComponent(typeof(CanvasRenderer))]
public class SongSelectPanelGraphic : MaskableGraphic
{
    public Color borderColor = SongSelectVisuals.Edge;
    public float cornerCut = 10f;
    public float borderWidth = 1f;
    public void SetColors(Color fill, Color edge) { color = fill; borderColor = edge; SetVerticesDirty(); }
    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear(); Rect r = rectTransform.rect;
        float inset = Mathf.Clamp(borderWidth, 0, Mathf.Min(r.width,r.height)*.2f);
        var outer = Points(r, cornerCut); var inner = Points(new Rect(r.x+inset,r.y+inset,r.width-2*inset,r.height-2*inset), Mathf.Max(0,cornerCut-inset*.5f));
        for (int i = 0; i < 8; i++)
        {
            int next = (i+1)%8; Triangle(vh, r.center, inner[i], inner[next], color);
            Quad(vh, outer[i], outer[next], inner[next], inner[i], borderColor);
        }
    }
    static Vector2[] Points(Rect r, float cut)
    {
        float c=Mathf.Clamp(cut,0,Mathf.Min(r.width,r.height)*.3f);
        return new[] { new Vector2(r.xMin+c,r.yMax),new Vector2(r.xMax-c,r.yMax),new Vector2(r.xMax,r.yMax-c),
            new Vector2(r.xMax,r.yMin+c),new Vector2(r.xMax-c,r.yMin),new Vector2(r.xMin+c,r.yMin),
            new Vector2(r.xMin,r.yMin+c),new Vector2(r.xMin,r.yMax-c) };
    }
    static void Triangle(VertexHelper vh, Vector2 a, Vector2 b, Vector2 c, Color col)
    {
        int i=vh.currentVertCount; vh.AddVert(a,col,Vector2.zero); vh.AddVert(b,col,Vector2.zero); vh.AddVert(c,col,Vector2.zero); vh.AddTriangle(i,i+1,i+2);
    }
    public static void Quad(VertexHelper vh, Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color col)
    {
        int i=vh.currentVertCount; vh.AddVert(a,col,Vector2.zero); vh.AddVert(b,col,Vector2.zero); vh.AddVert(c,col,Vector2.zero); vh.AddVert(d,col,Vector2.zero);
        vh.AddTriangle(i,i+1,i+2); vh.AddTriangle(i,i+2,i+3);
    }
}

// 毎フレームの脈動を持たず、実際のホバー/選択状態だけに反応する。
public class SongSelectActionStyle : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, ISelectHandler, IDeselectHandler
{
    Button button; SongSelectPanelGraphic face; TextMeshProUGUI label; bool primary, hover, focused;
    public void Initialize(Button b, SongSelectPanelGraphic p, TextMeshProUGUI t, bool main)
    { button=b; face=p; label=t; primary=main; Refresh(); }
    public void Refresh()
    {
        if (button==null || face==null || label==null) return;
        bool active=button.interactable; bool raised=active && (hover || focused);
        Color fill=active && primary ? Color.Lerp(SongSelectVisuals.Accent,SongSelectVisuals.Surface,.20f) : SongSelectVisuals.Surface;
        if (raised) fill=Color.Lerp(fill,SongSelectVisuals.Accent,primary?.12f:.15f);
        face.SetColors(fill,active && (primary || raised)?SongSelectVisuals.Accent:SongSelectVisuals.Edge);
        label.color=!active?SongSelectVisuals.Disabled:primary?SongSelectVisuals.Background:SongSelectVisuals.Text;
    }
    public void OnPointerEnter(PointerEventData e) { hover=true; Refresh(); }
    public void OnPointerExit(PointerEventData e) { hover=false; Refresh(); }
    public void OnSelect(BaseEventData e) { focused=true; Refresh(); }
    public void OnDeselect(BaseEventData e) { focused=false; Refresh(); }
}

// カバー未設定時も無地の色板にしない。画面内のベクター図形だけで共通ジャケットを描く。
[RequireComponent(typeof(CanvasRenderer))]
public class SongSelectCoverGraphic : MaskableGraphic
{
    public Color accent = SongSelectVisuals.Accent;
    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear(); var r=rectTransform.rect;
        SongSelectPanelGraphic.Quad(vh,new Vector2(r.xMin,r.yMin),new Vector2(r.xMin,r.yMax),new Vector2(r.xMax,r.yMax),new Vector2(r.xMax,r.yMin),SongSelectVisuals.Surface);
        for(int i=0;i<6;i++)
        {
            float x=r.xMin+r.width*(.08f+i*.115f);
            float h=r.height*(.22f+(i%3)*.11f);
            var c=Color.Lerp(SongSelectVisuals.Raised,accent,.10f+i*.028f);
            SongSelectPanelGraphic.Quad(vh,new Vector2(x,r.yMin),new Vector2(x+h*.48f,r.yMin+h),new Vector2(x+h*.48f+7,r.yMin+h),new Vector2(x+7,r.yMin),c);
        }
        for(int i=0;i<3;i++)
        {
            float x=r.xMin+r.width*(.30f+i*.17f),y=r.yMin+r.height*(.29f+i*.09f);
            float w=r.width*.13f,h=r.height*.39f;
            SongSelectPanelGraphic.Quad(vh,new Vector2(x,y),new Vector2(x+w,y+h),new Vector2(x+w+7,y+h),new Vector2(x+7,y),Color.Lerp(accent,Color.white,i*.15f));
        }
    }
}

[RequireComponent(typeof(CanvasRenderer))]
public class SongSelectBackdropGraphic : MaskableGraphic
{
    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear(); var r=rectTransform.rect;
        vh.AddVert(new Vector2(r.xMin,r.yMin),new Color(.061f,.103f,.126f),Vector2.zero);
        vh.AddVert(new Vector2(r.xMin,r.yMax),SongSelectVisuals.Background,Vector2.zero);
        vh.AddVert(new Vector2(r.xMax,r.yMax),SongSelectVisuals.Background,Vector2.zero);
        vh.AddVert(new Vector2(r.xMax,r.yMin),new Color(.061f,.103f,.126f),Vector2.zero);
        vh.AddTriangle(0,1,2);vh.AddTriangle(0,2,3);
        // プレイ画面と同系の、通路端だけにある奥行きの線。
        for(int s=-1;s<=1;s+=2)
            for(int i=0;i<5;i++)
            {
                float x=s*(r.width*.10f+i*r.width*.095f);
                Color c=new Color(.23f,.43f,.49f,.11f);
                SongSelectPanelGraphic.Quad(vh,new Vector2(x,r.yMin),new Vector2(x*.45f,r.yMin+r.height*.32f),
                    new Vector2(x*.45f+1,r.yMin+r.height*.32f),new Vector2(x+1,r.yMin),c);
            }
    }
}
