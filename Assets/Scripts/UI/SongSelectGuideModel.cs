using UnityEngine;
using UnityEngine.UI;

// 保存済みの人体模型から書き出した透過コマ画像で、右手の案内を描く。
// 背面の構図にすることで、本人の右手を画面でも右側に見せる。
[RequireComponent(typeof(CanvasRenderer))]
public sealed class SongSelectGuideModel : MaskableGraphic
{
    public const float LoopSeconds = 5.2f;
    const int Columns = 6, Rows = 4, FrameCount = Columns * Rows;
    Texture2D atlas;
    SongSelectDiscGraphic reticle;
    bool attemptedLoad;
    int frameIndex;
    public bool IsReady => atlas != null;
    public int FrameIndex => frameIndex;
    public override Texture mainTexture => atlas != null ? atlas : Texture2D.whiteTexture;

    public void SetPose(float seconds)
    {
        if (!isActiveAndEnabled || !EnsureAnimation()) return;
        if (float.IsNaN(seconds) || float.IsInfinity(seconds)) seconds = 0;
        float cycle = Mathf.Repeat(Mathf.Max(0, seconds), LoopSeconds);
        // 加減速は模型から書き出した動作に含まれるため、コマは等間隔で進める。
        float raised = Mathf.Clamp01((cycle - .35f) / 1.1f)
            * (1 - Mathf.Clamp01((cycle - 4.1f) / .9f));
        int next = Mathf.Min(FrameCount - 1, Mathf.FloorToInt(raised * FrameCount));
        if (frameIndex != next) { frameIndex = next; SetVerticesDirty(); }
        var tint = SongSelectSkin.Cyan;
        tint.a = Mathf.SmoothStep(0, .9f, (raised - .75f) * 4);
        reticle.color = tint;
    }

    bool EnsureAnimation()
    {
        if (atlas != null) return true;
        if (attemptedLoad) return false;
        attemptedLoad = true;
        atlas = Resources.Load<Texture2D>("UI/SongSelectAnime/RightHandGuide");
        if (atlas == null)
        {
            Debug.LogError("右手の案内用2Dアニメーションが見つかりません。", this);
            return false;
        }
        reticle = SongSelectSkin.Graphic(transform, "DemonstrationAim", new Vector2(88, 177),
            new Vector2(46, 46), SongSelectDiscGraphic.Shape.Ring, Color.clear);
        reticle.Width = 2;
        SetMaterialDirty(); SetVerticesDirty();
        return true;
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        if (!IsReady) return;
        var bounds = GetPixelAdjustedRect();
        float aspect = (atlas.width / (float)Columns) / (atlas.height / (float)Rows);
        float width = Mathf.Min(bounds.width, bounds.height * aspect);
        float height = width / aspect;
        var rect = new Rect(bounds.center.x - width * .5f, bounds.center.y - height * .5f, width, height);

        // 素材は左上から横6コマずつ。同じ模型・カメラの固定枠なので足元はずれない。
        float left = (frameIndex % Columns) / (float)Columns;
        float right = left + 1f / Columns;
        float top = 1 - (frameIndex / Columns) / (float)Rows;
        float bottom = top - 1f / Rows;
        vh.AddVert(new Vector3(rect.xMin, rect.yMin), color, new Vector2(left, bottom));
        vh.AddVert(new Vector3(rect.xMin, rect.yMax), color, new Vector2(left, top));
        vh.AddVert(new Vector3(rect.xMax, rect.yMax), color, new Vector2(right, top));
        vh.AddVert(new Vector3(rect.xMax, rect.yMin), color, new Vector2(right, bottom));
        vh.AddTriangle(0, 1, 2); vh.AddTriangle(2, 3, 0);
    }
}
