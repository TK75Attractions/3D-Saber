using UnityEngine;
using UnityEngine.UI;

// 開始ノーツを切った後だけ、奥の消失点から周縁へ光を流す。
// カメラや判定座標は動かさず、4種類の背景に共通する前進感を重ねる。
[RequireComponent(typeof(CanvasRenderer))]
public sealed class TitleDepartureGraphic : MaskableGraphic
{
    static readonly Vector2[] Paths = {
        new Vector2(.72f, .56f), new Vector2(1.12f, .24f),
        new Vector2(.88f, -.06f), new Vector2(1.04f, -.40f),
        new Vector2(.75f, -.69f), new Vector2(.30f, -.91f),
        new Vector2(.43f, .82f), new Vector2(.97f, .82f)
    };
    float departure;

    public void SetDeparture(float progress)
    {
        raycastTarget = false;
        float next = Mathf.Clamp01(progress);
        if (Mathf.Approximately(departure, next)) return;
        departure = next;
        SetVerticesDirty();
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        float strength = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.11f, .34f, departure))
            * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.82f, 1f, departure)));
        if (strength < .0001f) return;

        float flight = Mathf.InverseLerp(.11f, 1f, departure);
        float travel = flight * flight;
        Vector2 center = new Vector2(0f, -120f);
        Vector2 size = rectTransform.rect.size * .5f;
        for (int side = -1; side <= 1; side += 2)
        for (int i = 0; i < Paths.Length; i++)
        {
            Vector2 path = Vector2.Scale(Paths[i], size);
            path.x *= side;
            path -= center;
            Color tint = side < 0 ? new Color(1f, .13f, .26f) : new Color(.12f, .72f, 1f);
            for (int echo = 0; echo < 2; echo++)
            {
                float phase = Mathf.Repeat(i * .137f + echo * .5f + travel * 1.4f, 1f);
                float radial = .20f + phase * phase * 1.45f;
                float length = Mathf.Lerp(.035f, .23f, phase) * (1f + travel * .8f);
                Vector2 head = center + path * radial;
                Vector2 tail = center + path * Mathf.Max(.15f, radial - length);
                float alpha = Mathf.Sin(phase * Mathf.PI) * strength;
                float width = Mathf.Lerp(.8f, 3.5f, phase);
                Ribbon(vh, tail, head, width * 5f, tint, alpha * .055f);
                Ribbon(vh, tail, head, width, tint, alpha * .62f);
            }
        }
    }

    static void Ribbon(VertexHelper vh, Vector2 tail, Vector2 head, float width, Color tint, float alpha)
    {
        Vector2 direction = (head - tail).normalized;
        Vector2 edge = new Vector2(-direction.y, direction.x) * width * .5f;
        int first = vh.currentVertCount;
        Color transparent = new Color(tint.r, tint.g, tint.b, 0f);
        tint.a = alpha;
        vh.AddVert(tail - edge * .2f, transparent, Vector2.zero);
        vh.AddVert(tail + edge * .2f, transparent, Vector2.zero);
        vh.AddVert(head + edge, tint, Vector2.zero);
        vh.AddVert(head - edge, tint, Vector2.zero);
        vh.AddTriangle(first, first + 1, first + 2);
        vh.AddTriangle(first, first + 2, first + 3);
    }
}
