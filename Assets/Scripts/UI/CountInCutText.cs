using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// カウントインの斬られて割れる文字(数字と START。2026-10-05 Web 試作の案A「斬る数字」)。
// 割れた片は、同じ文字を UI の Mask(線の片側に広げて回した大きな四角)で切り抜いたもの。
// 線は文字の中心を通る前提。1本なら2片、2本(X)ならマスクを入れ子にして4片。
// 片は線の法線の向きへずれ、少し回って落ちながら消える。専用のシェーダーは使わない。
public sealed class CountInCutText
{
    public struct Cut
    {
        public Vector2 from, to;
        public Cut(Vector2 from, Vector2 to) { this.from = from; this.to = to; }
        public Vector2 Direction => (to - from).normalized;
        public float Degrees { get { var d = Direction; return Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg; } }
    }

    const float MaskLength = 6000f, MaskDepth = 3000f;

    readonly RectTransform root;
    readonly TextMeshProUGUI whole;
    readonly RectTransform[] pieces;
    readonly CanvasGroup[] pieceGroups;
    readonly Vector2[] pieceDirections;
    readonly float[] pieceSpin;
    readonly List<TextMeshProUGUI> texts = new List<TextMeshProUGUI>();
    readonly List<TextMeshProUGUI> pieceTexts = new List<TextMeshProUGUI>();

    public RectTransform Root => root;
    public TextMeshProUGUI Whole => whole;
    public TextMeshProUGUI PieceText(int index) => pieceTexts[Mathf.Clamp(index, 0, pieceTexts.Count - 1)];
    public int PieceCount => pieces.Length;
    public bool ShowingPieces { get; private set; }

    public CountInCutText(Transform parent, string name, string text, float size, TMP_FontAsset font,
        Material material, Cut[] cuts, FontStyles style)
    {
        root = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        root.SetParent(parent, false);
        root.sizeDelta = Vector2.zero;
        whole = MakeText(root, "Whole", text, size, font, material, style);
        int count = cuts.Length >= 2 ? 4 : 2;
        pieces = new RectTransform[count];
        pieceGroups = new CanvasGroup[count];
        pieceDirections = new Vector2[count];
        pieceSpin = new float[count];
        for (int p = 0; p < count; p++)
        {
            var holder = new GameObject("Piece" + p, typeof(RectTransform), typeof(CanvasGroup)).GetComponent<RectTransform>();
            holder.SetParent(root, false);
            holder.sizeDelta = Vector2.zero;
            pieceGroups[p] = holder.GetComponent<CanvasGroup>();
            pieceGroups[p].interactable = false;
            pieceGroups[p].blocksRaycasts = false;
            Transform inner = holder;
            float frameDegrees = 0f;
            // 子は親の pivot(線の上の点=文字の中心)に錨を置く。親の四角の中心に置くと、線から離れた所に描かれてしまう。
            Vector2 parentPivot = new Vector2(.5f, .5f);
            for (int c = 0; c < Mathf.Min(cuts.Length, 2); c++)
            {
                int side = ((p >> c) & 1) == 0 ? 1 : -1;
                var d = cuts[c].Direction;
                pieceDirections[p] += new Vector2(-d.y, d.x) * side;
                pieceSpin[p] += side * 4f;
                // 線に沿わせて回した四角。pivot の y を 0 にすると線の左側、1 にすると右側に広がる。
                var mask = new GameObject("Mask" + c, typeof(RectTransform), typeof(Image), typeof(Mask)).GetComponent<RectTransform>();
                mask.SetParent(inner, false);
                mask.anchorMin = mask.anchorMax = parentPivot;
                mask.sizeDelta = new Vector2(MaskLength, MaskDepth);
                mask.pivot = new Vector2(.5f, side > 0 ? 0f : 1f);
                mask.anchoredPosition = Vector2.zero;
                mask.localRotation = Quaternion.Euler(0, 0, cuts[c].Degrees - frameDegrees);
                frameDegrees = cuts[c].Degrees;
                var image = mask.GetComponent<Image>();
                image.color = Color.white;
                image.raycastTarget = false;
                mask.GetComponent<Mask>().showMaskGraphic = false;
                inner = mask;
                parentPivot = mask.pivot;
            }
            var piece = MakeText(inner, "Text", text, size, font, material, style);
            piece.rectTransform.anchorMin = piece.rectTransform.anchorMax = parentPivot;
            piece.rectTransform.anchoredPosition = Vector2.zero;
            piece.rectTransform.localRotation = Quaternion.Euler(0, 0, -frameDegrees);
            pieceTexts.Add(piece);
            pieces[p] = holder;
            holder.gameObject.SetActive(false);
        }
        Hide();
    }

    TextMeshProUGUI MakeText(Transform parent, string name, string text, float size, TMP_FontAsset font, Material material, FontStyles style)
    {
        var t = UISkinKit.MakeTMP(parent, name, text, size, Color.white, TextAlignmentOptions.Center,
            Vector2.zero, new Vector2(size * 4f, size * 1.5f), style, 0f, font);
        if (material != null) t.fontSharedMaterial = material;
        t.extraPadding = true;
        t.enableVertexGradient = true;
        texts.Add(t);
        return t;
    }

    // 上は白、下は色。芯を白に近くし、色は外側の光で付ける。
    public void SetGradient(Color top, Color bottom)
    {
        var gradient = new VertexGradient(top, top, bottom, bottom);
        foreach (var t in texts) if (t != null) t.colorGradient = gradient;
    }

    public void ShowWhole(float alpha, float scale)
    {
        root.gameObject.SetActive(true);
        ShowingPieces = false;
        whole.gameObject.SetActive(true);
        whole.alpha = Mathf.Clamp01(alpha);
        whole.rectTransform.localScale = Vector3.one * Mathf.Max(.01f, scale);
        foreach (var p in pieces) p.gameObject.SetActive(false);
    }

    // progress 0〜1 で、片が distance だけ法線の向きへずれ、drop だけ落ちながら消える。
    public void ShowPieces(float progress, float distance, float drop)
    {
        root.gameObject.SetActive(true);
        ShowingPieces = true;
        whole.gameObject.SetActive(false);
        float v = Mathf.Clamp01(progress), e = 1f - Mathf.Pow(1f - v, 3f);
        for (int p = 0; p < pieces.Length; p++)
        {
            pieces[p].gameObject.SetActive(true);
            pieces[p].anchoredPosition = pieceDirections[p] * (distance * e) + Vector2.down * (drop * e * e);
            pieces[p].localRotation = Quaternion.Euler(0, 0, pieceSpin[p] * e);
            pieceGroups[p].alpha = 1f - v;
        }
    }

    public void Hide()
    {
        ShowingPieces = false;
        root.gameObject.SetActive(false);
    }
}
