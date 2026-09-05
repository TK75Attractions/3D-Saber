using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

// ロングの残数表示だけのスタイル。共有フォントの材質を変更せず、スコア表示から分離する。
[ExecuteAlways]
public sealed class LongNoteCountStyle : MonoBehaviour
{
    public const string FontName = "Oxanium-ExtraBold";
    public const float NumberSize = 13.5f;
    public const float StrokeWidth = .20f;
    public static readonly Vector3 WorldOffset = new Vector3(0f, .90f, -.04f);
    public static readonly Color TopColor = new Color(.94f, .99f, 1f, 1f);
    public static readonly Color BottomColor = new Color(.48f, .80f, 1f, 1f);
    public static readonly Color StrokeColor = new Color(.012f, .023f, .052f, 1f);
    private Material ownedMaterial;

    public static void Apply(TextMeshPro text)
    {
        if (text == null) return;
        var owner = text.GetComponent<LongNoteCountStyle>();
        if (owner == null) owner = text.gameObject.AddComponent<LongNoteCountStyle>();
        var font = UISkinKit.FontAsset(FontName);
        if (font != null) text.font = font;
        text.fontSize = NumberSize;
        text.fontStyle = FontStyles.Normal; // フォント自体が極太なので疑似ボールドは重ねない。
        text.alignment = TextAlignmentOptions.Center;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Overflow;
        text.color = Color.white;
        text.enableVertexGradient = true;
        text.colorGradient = new VertexGradient(TopColor, TopColor, BottomColor, BottomColor);
        text.extraPadding = true;
        text.isOrthographic = false;
        text.rectTransform.sizeDelta = new Vector2(2.6f, 1.3f);
        if (owner.ownedMaterial == null && text.fontSharedMaterial != null)
        {
            owner.ownedMaterial = new Material(text.fontSharedMaterial) { name = "LongCount/OxaniumIce" };
            var material = owner.ownedMaterial;
            SetColor(material, "_OutlineColor", StrokeColor);
            SetFloat(material, "_OutlineWidth", StrokeWidth);
            SetFloat(material, "_FaceDilate", .01f);
            SetColor(material, "_UnderlayColor", new Color(.002f, .006f, .018f, .88f));
            SetFloat(material, "_UnderlayOffsetX", .06f);
            SetFloat(material, "_UnderlayOffsetY", -.12f);
            SetFloat(material, "_UnderlayDilate", .10f);
            SetFloat(material, "_UnderlaySoftness", .06f);
            SetFloat(material, "_ZTestMode", (float)CompareFunction.LessEqual);
            material.EnableKeyword("OUTLINE_ON");
            material.EnableKeyword("UNDERLAY_ON");
            material.DisableKeyword("GLOW_ON");
        }
        if (owner.ownedMaterial != null) text.fontSharedMaterial = owner.ownedMaterial;
        text.UpdateMeshPadding();
    }
    private static void SetFloat(Material material, string property, float value)
    {
        if (material.HasProperty(property)) material.SetFloat(property, value);
    }
    private static void SetColor(Material material, string property, Color value)
    {
        if (material.HasProperty(property)) material.SetColor(property, value);
    }
    private void OnDestroy()
    {
        if (ownedMaterial == null) return;
        if (Application.isPlaying) Destroy(ownedMaterial); else DestroyImmediate(ownedMaterial);
        ownedMaterial = null;
    }
}
