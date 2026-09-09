using System.Linq;
using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public class SongSelectVisualTests
{
    GameObject root;
    [SetUp] public void Setup() { root = new GameObject("SelectStyleTest",typeof(RectTransform),typeof(Canvas)); }
    [TearDown] public void Cleanup() { Object.DestroyImmediate(root); }

    [TestCase(true)]
    [TestCase(false)]
    public void ActionsUseSamePanelAndFont(bool primary)
    {
        var rect=SongSelectVisuals.Rect(root.transform,"Action",Vector2.zero,new Vector2(300,76));
        var button=rect.gameObject.AddComponent<Button>();
        SongSelectVisuals.StyleAction(button,"プレイ開始",primary);
        Assert.IsInstanceOf<SongSelectPanelGraphic>(button.targetGraphic);
        Assert.IsTrue(button.targetGraphic.raycastTarget);
        Assert.AreEqual(Selectable.Transition.None,button.transition);
        var text=button.GetComponentInChildren<TextMeshProUGUI>();
        Assert.AreEqual("Oxanium-ExtraBold",text.font.name);
        Assert.IsTrue(text.font.HasCharacter('開',true,true));
        Assert.AreEqual(1f,button.GetComponent<SaberDwellTarget>().dwellSeconds);
        Assert.IsFalse(text.raycastTarget);
    }

    [Test]
    public void RestylingKeepsClickAction()
    {
        var rect=SongSelectVisuals.Rect(root.transform,"Action",Vector2.zero,new Vector2(300,76));
        var button=rect.gameObject.AddComponent<Button>();int clicks=0;
        button.onClick.AddListener(()=>clicks++);
        SongSelectVisuals.StyleAction(button,"START",true);
        button.onClick.Invoke();Assert.AreEqual(1,clicks);
    }

    [Test]
    public void DisabledActionDoesNotBecomeHighlightedOnHover()
    {
        var rect=SongSelectVisuals.Rect(root.transform,"Action",Vector2.zero,new Vector2(300,76));
        var button=rect.gameObject.AddComponent<Button>();
        var style=SongSelectVisuals.StyleAction(button,"START",true);
        button.interactable=false;style.Refresh();style.OnPointerEnter(null);
        Assert.AreEqual(SongSelectVisuals.Disabled,button.GetComponentInChildren<TextMeshProUGUI>().color);
        Assert.AreEqual(SongSelectVisuals.Surface,button.targetGraphic.color);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void DifficultySelectionKeepsLayoutStable(int index)
    {
        var rect=SongSelectVisuals.Rect(root.transform,"Difficulty",Vector2.zero,new Vector2(190,92));
        var button=rect.gameObject.AddComponent<Button>();
        var item=rect.gameObject.AddComponent<DifficultyTileItem>();
        item.Build(button,SongSelectSkin.DifficultyColor(index),SongSelectSkin.DifficultyDisplayName(index,"Normal"),10);
        item.SetSelected(true,true);
        Assert.AreEqual(Vector2.zero,((RectTransform)rect.Find("Content")).anchoredPosition);
        Assert.IsInstanceOf<SongSelectPanelGraphic>(button.targetGraphic);
        Assert.AreEqual(SongSelectSkin.DifficultyColor(index),rect.Find("Content/SelectionLine").GetComponent<SongSelectPanelGraphic>().color);
        Assert.AreEqual("LV 10",rect.Find("Content/Level").GetComponent<TextMeshProUGUI>().text);
        item.SetLevel(0);
        Assert.AreEqual("LV --",rect.Find("Content/Level").GetComponent<TextMeshProUGUI>().text);
        Assert.AreEqual(.5f,rect.GetComponent<CanvasGroup>().alpha);
    }

    [TestCase(typeof(SongSelectPanelGraphic))]
    [TestCase(typeof(SongSelectBackdropGraphic))]
    [TestCase(typeof(SongSelectCoverGraphic))]
    public void ProceduralGraphicsHaveFiniteGeometry(System.Type type)
    {
        var rect=SongSelectVisuals.Rect(root.transform,"Geometry",Vector2.zero,new Vector2(300,200));
        var graphic=(Graphic)rect.gameObject.AddComponent(type);
        Assert.IsNotNull(graphic.canvasRenderer,"独自パネルにも実描画用 CanvasRenderer が必要");
        using(var vertices=new VertexHelper())
        {
            type.GetMethod("OnPopulateMesh",BindingFlags.NonPublic|BindingFlags.Instance,null,new[]{typeof(VertexHelper)},null).Invoke(graphic,new object[]{vertices});
            Assert.Greater(vertices.currentVertCount,0);
            var mesh=new Mesh();
            try
            {
                vertices.FillMesh(mesh);
                Assert.IsTrue(mesh.vertices.All(v=>!float.IsNaN(v.sqrMagnitude)&&!float.IsInfinity(v.sqrMagnitude)));
                Assert.IsTrue(mesh.triangles.All(i=>i>=0&&i<mesh.vertexCount));
                Assert.That(mesh.bounds.size.x,Is.LessThanOrEqualTo(301));
                Assert.That(mesh.bounds.size.y,Is.LessThanOrEqualTo(201));
            }
            finally{Object.DestroyImmediate(mesh);}
        }
    }

    [Test]
    public void NavigationStaysAlignedAfterAspectRatioChange()
    {
        var cameraObject=new GameObject("NavCamera",typeof(Camera));cameraObject.transform.SetParent(root.transform,false);
        var camera=cameraObject.GetComponent<Camera>();camera.transform.position=new Vector3(0,0,-10);camera.aspect=16f/9;
        var navObject=new GameObject("Nav");navObject.transform.SetParent(root.transform,false);
        var nav=navObject.AddComponent<SongSelectSlashNav>();nav.Init(null,camera);
        try
        {
            camera.aspect=4f/3;nav.Tick(0);
            var up=camera.WorldToViewportPoint(nav.UpNote.transform.position);
            var down=camera.WorldToViewportPoint(nav.DownNote.transform.position);
            Assert.That(up.x,Is.EqualTo(nav.upViewport.x).Within(.001f));
            Assert.That(up.y,Is.EqualTo(nav.upViewport.y).Within(.001f));
            Assert.That(down.x,Is.EqualTo(nav.downViewport.x).Within(.001f));
            Assert.That(down.y,Is.EqualTo(nav.downViewport.y).Within(.001f));
        }
        finally
        {
            Object.DestroyImmediate(nav.UpNote.gameObject);Object.DestroyImmediate(nav.DownNote.gameObject);
        }
    }
}
