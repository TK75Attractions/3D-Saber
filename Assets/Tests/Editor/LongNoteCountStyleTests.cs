using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEngine;

public class LongNoteCountStyleTests
{
    private GameObject label;
    private GameObject noteObject;
    [TearDown]
    public void Cleanup()
    {
        if (label != null) Object.DestroyImmediate(label);
        if (noteObject != null) Object.DestroyImmediate(noteObject);
    }

    private TextMeshPro MakeText(string digits)
    {
        label = new GameObject("CountLabelTest");
        var text = label.AddComponent<TextMeshPro>();
        text.text = digits;
        LongNoteCountStyle.Apply(text);
        return text;
    }

    [TestCase("2")]
    [TestCase("9")]
    [TestCase("12")]
    [TestCase("50")]
    [TestCase("100")]
    public void Digits_UseHeavyFontWithoutClippingOrWrapping(string digits)
    {
        var text = MakeText(digits);
        text.ForceMeshUpdate(true, true);
        Assert.AreEqual(LongNoteCountStyle.FontName, text.font.name);
        Assert.AreEqual(LongNoteCountStyle.NumberSize, text.fontSize);
        Assert.AreEqual(TextWrappingModes.NoWrap, text.textWrappingMode);
        Assert.AreEqual(TextOverflowModes.Overflow, text.overflowMode);
        Assert.AreEqual(digits.Length, text.textInfo.characterCount);
        for (int i = 0; i < digits.Length; i++) Assert.IsTrue(text.textInfo.characterInfo[i].isVisible);
    }

    [Test]
    public void Material_IsIsolatedFromSharedHudFontAndIsReleased()
    {
        var font = UISkinKit.FontAsset(LongNoteCountStyle.FontName);
        float originalWidth = font.material.GetFloat("_OutlineWidth");
        Color originalColor = font.material.GetColor("_OutlineColor");
        var text = MakeText("12");
        var owned = text.fontSharedMaterial;
        Assert.AreNotSame(font.material, owned);
        Assert.AreEqual(LongNoteCountStyle.StrokeWidth, owned.GetFloat("_OutlineWidth"));
        Assert.AreEqual(originalWidth, font.material.GetFloat("_OutlineWidth"));
        Assert.AreEqual(originalColor, font.material.GetColor("_OutlineColor"));
        LongNoteCountStyle.Apply(text);
        Assert.AreSame(owned, text.fontSharedMaterial);
        Assert.IsTrue(owned.IsKeywordEnabled("UNDERLAY_ON"));
        Object.DestroyImmediate(label);
        Assert.IsTrue(owned == null);
        Assert.IsNotNull(font.material);
    }

    [Test]
    public void Spawner_LabelIsUnscaledAndCountBehaviorIsUnchanged()
    {
        noteObject = new GameObject("LongNoteTest");
        var note = noteObject.AddComponent<CuttableNote>();
        note.RequiredCutCount = note.RemainingCuts = 50;
        noteObject.transform.position = new Vector3(1f, -.5f, 12f);
        noteObject.transform.localScale = new Vector3(1f, 1f, 6f);
        typeof(NoteSpawner).GetMethod("BuildCountLabel", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { noteObject.transform, note });
        label = note.countLabel.gameObject;
        Assert.IsNull(label.transform.parent);
        Assert.AreEqual(Vector3.one, label.transform.localScale);
        Assert.AreEqual(Quaternion.identity, label.transform.rotation);
        Assert.AreEqual(noteObject.transform.position + LongNoteCountStyle.WorldOffset, label.transform.position);
        Assert.AreEqual(new Vector3(1f, 1f, 6f), noteObject.transform.localScale);
        note.Cut(Vector3.zero, Vector3.right);
        Assert.AreEqual("49", note.countLabel.text);
        Assert.AreEqual(49, note.RemainingCuts);
        Assert.IsFalse(note.IsFinalized);
    }
}
