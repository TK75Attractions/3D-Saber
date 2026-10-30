using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// 選曲案内と同じ人体模型。背中から見た青左・赤右の両手を、譜面時計で独立に動かす。
// 胴体・カメラを固定して撮影した画像の左右を使うため、追加カメラや判定用入力は不要。
[RequireComponent(typeof(CanvasRenderer))]
public sealed class TutorialGuideModel : MaskableGraphic
{
    public const string ResourcePath = "UI/TutorialGuide/BothHands";
    public const int Columns = 4, Rows = 4, FrameCount = 16;
    Texture2D atlas;
    bool attemptedLoad;
    public int LeftFrame { get; private set; } = 8;
    public int RightFrame { get; private set; } = 8;
    public bool IsReady => atlas != null;
    public override Texture mainTexture => atlas != null ? atlas : Texture2D.whiteTexture;

    public void Show(IReadOnlyList<NoteData> notes, double chartTime) =>
        ShowPose(TutorialGuideMotion.Sample(notes, chartTime, SaberHand.Left), TutorialGuideMotion.Sample(notes, chartTime, SaberHand.Right));

    // 両手の姿勢(0 = 下ろす 〜 1 = 頭の上)をそのまま見せる。範囲を合わせる段は、ノーツなしで大きく振るお手本に使う。
    public void ShowPose(float leftPose, float rightPose)
    {
        if (!attemptedLoad)
        {
            attemptedLoad = true;
            atlas = Resources.Load<Texture2D>(ResourcePath);
            if (atlas == null) Debug.LogError("チュートリアルのお手本画像が見つかりません。", this);
            SetMaterialDirty(); SetVerticesDirty();
        }
        int left = Frame(leftPose), right = Frame(rightPose);
        if (left == LeftFrame && right == RightFrame) return;
        LeftFrame = left; RightFrame = right; SetVerticesDirty();
    }

    static int Frame(float pose) => Mathf.Clamp(Mathf.RoundToInt(pose * (FrameCount - 1)), 0, FrameCount - 1);

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        if (atlas == null) return;
        var bounds = GetPixelAdjustedRect();
        float size = Mathf.Min(bounds.width, bounds.height);
        var rect = new Rect(bounds.center - Vector2.one * size * .5f, Vector2.one * size);
        Half(vh, rect, LeftFrame, false);
        Half(vh, rect, RightFrame, true);
    }

    void Half(VertexHelper vh, Rect rect, int frame, bool right)
    {
        float left = (frame % Columns + (right ? .5f : 0)) / Columns;
        float top = 1 - frame / Columns / (float)Rows;
        float x = right ? rect.center.x : rect.xMin;
        int start = vh.currentVertCount;
        vh.AddVert(new Vector3(x, rect.yMin), color, new Vector2(left, top - 1f / Rows));
        vh.AddVert(new Vector3(x, rect.yMax), color, new Vector2(left, top));
        vh.AddVert(new Vector3(x + rect.width * .5f, rect.yMax), color, new Vector2(left + .5f / Columns, top));
        vh.AddVert(new Vector3(x + rect.width * .5f, rect.yMin), color, new Vector2(left + .5f / Columns, top - 1f / Rows));
        vh.AddTriangle(start, start + 1, start + 2); vh.AddTriangle(start + 2, start + 3, start);
    }
}

// 判定調整を引いた譜面時刻でサンプリングする。毎フレームの積算をせず、音源の周回・停止でもずれない。
public static class TutorialGuideMotion
{
    public const double LeadSeconds = .24, RecoverSeconds = .24;

    public static float Sample(IReadOnlyList<NoteData> notes, double chartTime, SaberHand hand)
    {
        if (notes == null || hand == SaberHand.Any || double.IsNaN(chartTime) || double.IsInfinity(chartTime)) return .5f;
        double nearest = double.PositiveInfinity, relative = 0;
        bool up = false, found = false;
        foreach (var note in notes)
        {
            // 金色は自由な手の一例として右手で見せる。左右を取り違えない。
            if (note == null || (note.color == "blue" ? SaberHand.Left : SaberHand.Right) != hand) continue;
            int count = Math.Max(1, note.count);
            double spacing = count > 1 ? note.LingerSeconds((float)TutorialProgram.BeatSeconds) / (count - 1) : 0;
            if (chartTime < note.TimeSeconds - LeadSeconds || chartTime > note.TimeSeconds + spacing * (count - 1) + RecoverSeconds) continue;
            for (int cut = 0; cut < count; cut++)
            {
                double delta = chartTime - (note.TimeSeconds + cut * spacing);
                if (delta < -LeadSeconds || delta > RecoverSeconds || Math.Abs(delta) >= nearest) continue;
                nearest = Math.Abs(delta); relative = delta; found = true;
                // 矢印はその方向。ロングは上・下を交互に切り返す。
                up = count > 1 ? cut % 2 == 1 : note.direction == "up";
            }
        }
        return found ? Stroke(relative, up) : .5f;
    }

    // 範囲を合わせる段のお手本: 両腕を腰から肩まで、2拍で1往復(練習の音源と同じ拍)。
    // 2026-10-30 夜(改善案⑤2): 前は 0〜1(下ろした姿勢から頭の上)。頭の上はカメラに見えないので、0.2〜0.8 に狭めた。
    public const float RangeSweepLow = .2f, RangeSweepHigh = .8f;
    public const double RangeSweepSeconds = 2 * TutorialProgram.BeatSeconds;
    public static float RangeSweep(double chartTime) => double.IsNaN(chartTime) || double.IsInfinity(chartTime) ? .5f
        : (float)(.5 - .3 * Math.Cos(2 * Math.PI * chartTime / RangeSweepSeconds));

    public static float Stroke(double relative, bool up)
    {
        float start = up ? 0 : 1, end = 1 - start;
        if (relative < -LeadSeconds || relative > RecoverSeconds) return .5f;
        if (relative < -.08) return Mathf.SmoothStep(.5f, start, (float)((relative + LeadSeconds) / (LeadSeconds - .08)));
        if (relative <= .08) return Mathf.SmoothStep(start, end, (float)((relative + .08) / .16));
        return Mathf.SmoothStep(end, .5f, (float)((relative - .08) / (RecoverSeconds - .08)));
    }
}
