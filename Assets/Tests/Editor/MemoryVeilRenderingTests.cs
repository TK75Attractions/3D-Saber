using NUnit.Framework;
using TMPro;
using UnityEngine;

public class MemoryVeilRenderingTests
{
    [Test]
    public void SaberOrderIncludesLaterChildrenAndRestoresSharedMaterials()
    {
        var shader = Resources.Load<Shader>("Effects/GameplayCutAccent");
        Assert.NotNull(shader);
        var source = new Material(shader);
        int sourceQueue = source.renderQueue;
        var root = new GameObject("MemorySaber");
        var other = new GameObject("Unrelated");
        try
        {
            var mouse = root.AddComponent<MouseSaberInput>();
            var bladeObject = new GameObject("Blade");
            bladeObject.transform.SetParent(root.transform, false);
            var blade = bladeObject.AddComponent<MeshRenderer>();
            blade.sharedMaterial = source;
            blade.sortingOrder = 7;

            var unrelated = other.AddComponent<MeshRenderer>();
            unrelated.sharedMaterial = source;
            unrelated.sortingOrder = 19;

            mouse.DrawAboveMemoryVeil = true;
            Assert.AreEqual(MemoryVeilLayers.AboveSortingOrder, blade.sortingOrder);
            Assert.AreEqual(MemoryVeilLayers.AboveRenderQueue, blade.sharedMaterial.renderQueue);
            Assert.AreNotSame(source, blade.sharedMaterial);
            Assert.AreEqual(sourceQueue, source.renderQueue, "共用材質を書き換えない");
            Assert.AreSame(source, unrelated.sharedMaterial);
            Assert.AreEqual(19, unrelated.sortingOrder);

            var lateObject = new GameObject("LaterOutline");
            lateObject.transform.SetParent(root.transform, false);
            var late = lateObject.AddComponent<LineRenderer>();
            late.sharedMaterial = source;
            late.sortingOrder = 11;
            SaberMemoryVeilOrder.RefreshFor(late);

            Assert.AreEqual(MemoryVeilLayers.AboveSortingOrder, late.sortingOrder);
            Assert.AreEqual(MemoryVeilLayers.AboveRenderQueue, late.sharedMaterial.renderQueue);

            mouse.DrawAboveMemoryVeil = false;
            Assert.AreEqual(7, blade.sortingOrder);
            Assert.AreEqual(11, late.sortingOrder);
            Assert.AreSame(source, blade.sharedMaterial);
            Assert.AreSame(source, late.sharedMaterial);
            Assert.AreEqual(sourceQueue, source.renderQueue);
        }
        finally
        {
            Object.DestroyImmediate(root);
            Object.DestroyImmediate(other);
            Object.DestroyImmediate(source);
        }
    }

    [Test]
    public void LongCaptionsFitAndReplacementStillUsesTheSongClock()
    {
        try
        {
            FirstNoteHandHintView.Show(MemoryVeilRules.RuleCaption, RushESecretState.Amber, 0, 3);
            var first = GameObject.Find("FirstHandHint").GetComponentInChildren<TextMeshProUGUI>();
            Assert.AreEqual(1440, first.rectTransform.sizeDelta.x);
            Assert.IsTrue(first.enableAutoSizing);
            FirstNoteHandHintView.Apply(1, false);
            Assert.AreEqual(1, first.alpha, 1e-6);

            FirstNoteHandHintView.Show("中級：まね", RushESecretState.Amber, 2, 4);
            var second = GameObject.Find("FirstHandHint").GetComponentInChildren<TextMeshProUGUI>();
            Assert.AreEqual("中級：まね", second.text);
            Assert.AreEqual(2, FirstNoteHandHintView.CurrentBegin);
            Assert.AreEqual(4, FirstNoteHandHintView.CurrentEnd);
            FirstNoteHandHintView.Apply(3, true);
            Assert.AreEqual(0, second.alpha);
            FirstNoteHandHintView.Apply(3, false);
            Assert.AreEqual(1, second.alpha);
            FirstNoteHandHintView.Apply(4, false);
            Assert.IsNull(GameObject.Find("FirstHandHint"));
        }
        finally { FirstNoteHandHintView.Hide(); }
    }

    [Test]
    public void NoiseShaderIsIncludedAsAResource()
    {
        Assert.NotNull(Resources.Load<Shader>("Effects/MemoryVeilNoise"));
    }
}
