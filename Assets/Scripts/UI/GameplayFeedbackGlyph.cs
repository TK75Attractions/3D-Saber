using UnityEngine;
using UnityEngine.UI;

// 色だけに頼らず、Missの×と正しい方向の矢印を幾何形状で示す。
[RequireComponent(typeof(CanvasRenderer))]
public sealed class GameplayFeedbackGlyph : MaskableGraphic
{
    public enum Kind { Slash, Cross, Arrow }
    Kind kind;
    CutDirection direction;
    public void Set(Kind value, CutDirection required, Color tint) { kind = value; direction = required; color = tint; SetVerticesDirty(); }
    protected override void OnPopulateMesh(VertexHelper mesh)
    {
        mesh.Clear(); float r = Mathf.Min(rectTransform.rect.width, rectTransform.rect.height) * .36f;
        if (kind == Kind.Arrow)
        {
            Vector2 tip = CutDirectionHelper.ToVector(direction) * r;
            Vector2 side = new Vector2(-tip.y, tip.x) * .65f;
            Line(mesh, -tip, tip, 3); Line(mesh, tip, side, 3); Line(mesh, tip, -side, 3);
        }
        else
        {
            Line(mesh, new Vector2(-r, -r), new Vector2(r, r), 3);
            if (kind == Kind.Cross) Line(mesh, new Vector2(-r, r), new Vector2(r, -r), 3);
        }
    }
    void Line(VertexHelper mesh, Vector2 a, Vector2 b, float width)
    {
        Vector2 n = new Vector2(-(b-a).y, (b-a).x).normalized * width * .5f;
        int start = mesh.currentVertCount;
        mesh.AddVert(a-n, color, Vector2.zero); mesh.AddVert(a+n, color, Vector2.zero);
        mesh.AddVert(b+n, color, Vector2.zero); mesh.AddVert(b-n, color, Vector2.zero);
        mesh.AddTriangle(start, start+1, start+2); mesh.AddTriangle(start, start+2, start+3);
    }
}
