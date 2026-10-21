using System.Text.RegularExpressions;
using NUnit.Framework;
using TMPro;
using UnityEngine;

// 版の表示(仕様面の点検 14)。どの画面でも右下に小さく薄く「v1.0」。更新のたびに 1.0.1(軽微)・1.1(中くらい)と上げる。
public class GameVersionTests
{
    [Test]
    public void VersionIsMajorMinorOrMajorMinorPatch()
    {
        StringAssert.IsMatch(@"^\d+\.\d+(\.[1-9]\d*)?$", GameVersion.Number, "1.0・1.0.1・1.1 の形(下の桁の 0 は書かない)");
        Assert.AreEqual("v" + GameVersion.Number, GameVersion.Label);
    }

    [Test]
    public void LabelSitsSmallAndFaintInTheBottomRightCorner()
    {
        var label = GameVersionLabel.Create();
        try
        {
            var text = label.Text;
            Assert.AreEqual(GameVersion.Label, text.text);
            var rt = text.rectTransform;
            Assert.AreEqual(new Vector2(1, 0), rt.anchorMin);
            Assert.AreEqual(new Vector2(1, 0), rt.anchorMax);
            Assert.AreEqual(new Vector2(1, 0), rt.pivot, "右下の角から内側へ");
            Assert.AreEqual(new Vector2(-GameVersionLabel.Margin.x, GameVersionLabel.Margin.y), rt.anchoredPosition);
            Assert.AreEqual(TextAlignmentOptions.BottomRight, text.alignment);
            Assert.LessOrEqual(text.fontSize, 20f, "小さく");
            Assert.That(text.color.a, Is.InRange(.2f, .45f), "薄く(でも台を見比べるときに読める)");
            Assert.False(text.raycastTarget, "照準やクリックを横取りしない");
            var canvas = label.GetComponent<Canvas>();
            Assert.AreEqual(RenderMode.ScreenSpaceOverlay, canvas.renderMode);
            Assert.Less(canvas.sortingOrder, 950, "運営の知らせ(OperatorNotice)と幕の下");
            Assert.Greater(canvas.sortingOrder, 505, "プレイ中の HUD の上");
        }
        finally { Object.DestroyImmediate(label.gameObject); }
    }
}
