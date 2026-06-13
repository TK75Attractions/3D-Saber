using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class SongRowFXTests
{
    private readonly List<GameObject> created = new List<GameObject>();

    [TearDown]
    public void Cleanup()
    {
        foreach (var go in created)
        {
            if (go != null) Object.DestroyImmediate(go);
        }
        created.Clear();
    }

    private (SongRowFX fx, Image fill, RectTransform barRT, Image barImg, TextMeshProUGUI label) MakeRow()
    {
        var row = new GameObject("Row", typeof(RectTransform), typeof(Image));
        created.Add(row);

        var bar = new GameObject("Bar", typeof(RectTransform), typeof(Image));
        bar.transform.SetParent(row.transform, false);
        var barRT = bar.GetComponent<RectTransform>();
        barRT.sizeDelta = new Vector2(5f, 0f);

        var labelGo = new GameObject("Label", typeof(RectTransform));
        labelGo.transform.SetParent(row.transform, false);
        var label = labelGo.AddComponent<TextMeshProUGUI>();
        label.text = "ElDorado";

        var fx = row.AddComponent<SongRowFX>();
        fx.fill = row.GetComponent<Image>();
        fx.accentBar = bar.GetComponent<Image>();
        fx.accentBarRT = barRT;
        fx.labelTMP = label;
        return (fx, fx.fill, barRT, fx.accentBar, label);
    }

    // 状態遷移を十分な時間ぶん進める(60fps相当 × 約2.5秒)
    private static void Settle(SongRowFX fx)
    {
        for (int i = 0; i < 150; i++) fx.Evaluate(1f / 60f);
    }

    [Test]
    public void Select_GrowsBar_AndWidensSpacing_AndBrightensFill()
    {
        var (fx, fill, barRT, barImg, label) = MakeRow();
        fx.SetSelected(true);
        Settle(fx);

        Assert.AreEqual(fx.barHeight, barRT.sizeDelta.y, 1f, "バーが規定の高さまで伸びる");
        Assert.AreEqual(1f, barImg.color.a, 0.02f, "バーが完全に不透明になる");
        Assert.AreEqual(fx.baseSpacing + fx.selectedSpacingAdd, label.characterSpacing, 0.2f,
            "文字間隔が選択時の値に収束する(パンチは減衰済み)");
        Assert.AreEqual(fx.fillSelected.r, fill.color.r, 0.05f, "塗りが選択色に近づく");
        Assert.AreEqual(Color.white.r, label.color.r, 0.05f, "ラベルが白に近づく");
    }

    [Test]
    public void Deselect_ReturnsToNormalState()
    {
        var (fx, fill, barRT, barImg, label) = MakeRow();
        fx.SetSelected(true);
        Settle(fx);
        fx.SetSelected(false);
        Settle(fx);

        Assert.AreEqual(0f, barRT.sizeDelta.y, 1f, "バーは畳まれる");
        Assert.AreEqual(0f, barImg.color.a, 0.02f, "バーは透明に戻る");
        Assert.AreEqual(fx.baseSpacing, label.characterSpacing, 0.2f, "文字間隔は基準値に戻る");
        Assert.AreEqual(fx.fillNormal.r, fill.color.r, 0.05f, "塗りは通常色に戻る");
    }

    [Test]
    public void SelectionPunch_SpikesSpacing_ThenSettles()
    {
        var (fx, _, _, _, label) = MakeRow();
        fx.SetSelected(true);
        fx.Evaluate(1f / 60f); // 直後はパンチで大きく開く
        float early = label.characterSpacing;
        Settle(fx);
        float settled = label.characterSpacing;

        Assert.Greater(early, settled, "選択直後は文字間隔がスパイクする");
        Assert.AreEqual(fx.baseSpacing + fx.selectedSpacingAdd, settled, 0.2f);
    }

    [Test]
    public void Hover_ScalesRow_AndBacksOff()
    {
        var (fx, _, _, _, _) = MakeRow();
        fx.OnPointerEnter(null);
        Settle(fx);
        Assert.AreEqual(fx.hoverScale, fx.transform.localScale.x, 0.005f, "ホバーで拡大");

        fx.OnPointerExit(null);
        Settle(fx);
        Assert.AreEqual(1f, fx.transform.localScale.x, 0.005f, "離れたら等倍に戻る");
    }

    [Test]
    public void LegacyLabelFallback_ColorAnimates()
    {
        // 日本語曲名などで TMP 化できない行は legacy Text を動かす
        var row = new GameObject("Row", typeof(RectTransform), typeof(Image));
        created.Add(row);
        var labelGo = new GameObject("Label", typeof(RectTransform), typeof(Text));
        labelGo.transform.SetParent(row.transform, false);
        var legacy = labelGo.GetComponent<Text>();
        legacy.text = "日本語の曲";

        var fx = row.AddComponent<SongRowFX>();
        fx.fill = row.GetComponent<Image>();
        fx.labelLegacy = legacy;

        fx.SetSelected(true);
        Settle(fx);
        Assert.AreEqual(Color.white.r, legacy.color.r, 0.05f, "legacy ラベルでも色が選択色に近づく");
    }
}
