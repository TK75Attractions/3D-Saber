using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

// 2026-10-28 スマホで印の QR のカード(ChartMarkPhoneCard)。カードのテクスチャの上でも QR が向きどおり(上下逆・鏡像でない)か、
// アドレスが無いときの知らせ、F5 で出す単独のカードの出し入れと置き場所。
public class ChartMarkPhoneCardTests
{
    const string Url = "http://192.168.137.1:5020/m?t=0123456789abcdef0123456789abcdef";
    string previousUrl;
    GameObject canvas;

    [SetUp]
    public void SetUp()
    {
        previousUrl = ChartMarkPhoneLink.Url;
        canvas = new GameObject("ChartMarkPhoneCardTestCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
        canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
    }

    [TearDown]
    public void TearDown()
    {
        ChartMarkPhoneCard.HideStandalone();
        Object.DestroyImmediate(canvas);
        ChartMarkPhoneLink.Url = previousUrl;
    }

    [Test]
    public void TheCodeOnTheCardIsTheAddressTheRightWayUp()
    {
        ChartMarkPhoneLink.Url = Url;
        var card = ChartMarkPhoneCard.AttachTo(canvas.transform);
        Assert.IsTrue(card.ShowsCode);
        StringAssert.Contains(Url, card.AddressText);
        var modules = QrCode.Encode(Url);
        int size = modules.GetLength(0), quiet = ChartMarkPhoneCard.QuietModules;
        var texture = card.CodeTexture;
        Assert.AreEqual(size + quiet * 2, texture.width);
        Assert.AreEqual(texture.width, texture.height);
        var pixels = texture.GetPixels32();
        // テクスチャの行は下から数える。QR の上の行(y=0)がテクスチャのいちばん上の行に来れば、画面でも上下が合う。
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var pixel = pixels[(texture.height - 1 - quiet - y) * texture.width + quiet + x];
                Assert.AreEqual(modules[x, y], pixel.r < 128, "モジュール (" + x + ", " + y + ")");
            }
        Assert.AreEqual(255, pixels[0].r, "周りの余白は白");
    }

    [Test]
    public void WithoutAnAddressTheCardSaysWhatToDo()
    {
        ChartMarkPhoneLink.Url = null;
        var card = ChartMarkPhoneCard.AttachTo(canvas.transform);
        Assert.IsFalse(card.ShowsCode);
        Assert.AreEqual(ChartMarkPhoneCard.NoAddress, card.MessageText);
        ChartMarkPhoneLink.Url = Url;
        card.Refresh();
        Assert.IsTrue(card.ShowsCode, "アドレスが入ったら作る");
        Assert.AreEqual(Url, card.ShownUrl);
    }

    [Test]
    public void BesideThePanelTheCardShrinksOnNarrowScreensSoItDoesNotCoverThePanel()
    {
        ChartMarkPhoneLink.Url = Url;
        const float panelHalf = 620f;
        // 16:9(参照 1920 幅)はそのまま、16:10(1920×1200 を参照解像度で見ると幅 1822)は縮める。
        foreach (float width in new[] { 1920f, 1822f })
        {
            var parent = new GameObject("Screen", typeof(RectTransform));
            parent.transform.SetParent(canvas.transform, false);
            var area = (RectTransform)parent.transform;
            area.sizeDelta = new Vector2(width, 1080f);
            var card = ChartMarkPhoneCard.AttachTo(area, panelHalf);
            var rt = (RectTransform)card.transform;
            float scale = rt.localScale.x;
            float left = width * .5f + rt.anchoredPosition.x - ChartMarkPhoneCard.Width * scale * .5f;
            float right = width * .5f + rt.anchoredPosition.x + ChartMarkPhoneCard.Width * scale * .5f;
            Assert.GreaterOrEqual(left, panelHalf + 19.9f, width + ": 欄と重ならない");
            Assert.LessOrEqual(right, width * .5f - 19.9f, width + ": 画面の右端の内側");
            Assert.IsTrue(card.ShowsCode);
            if (width >= 1920f) Assert.AreEqual(1f, scale, 1e-4f, "16:9 では縮めない");
            else Assert.Less(scale, 1f);
            Object.DestroyImmediate(parent);
        }
    }

    [Test]
    public void TheF5CardTogglesAndSitsInsideTheRightEdge()
    {
        ChartMarkPhoneLink.Url = Url;
        Assert.IsTrue(ChartMarkPhoneCard.ToggleStandalone());
        Assert.IsTrue(ChartMarkPhoneCard.StandaloneVisible);
        var rt = (RectTransform)ChartMarkPhoneCard.Standalone.transform;
        Assert.AreEqual(new Vector2(1f, .5f), rt.anchorMin, "画面の右端が基準(16:10 でも切れない)");
        Assert.Less(rt.anchoredPosition.x + ChartMarkPhoneCard.Width * .5f, 0f, "右端の内側");
        foreach (var graphic in ChartMarkPhoneCard.Standalone.GetComponentsInChildren<Graphic>(true))
            Assert.IsFalse(graphic.raycastTarget, "結果画面の操作を止めない: " + graphic.name);
        Assert.IsFalse(ChartMarkPhoneCard.ToggleStandalone());
        Assert.IsFalse(ChartMarkPhoneCard.StandaloneVisible);
    }
}
