using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// 29A: 描画するカメラの画素で保護範囲を作る。他の演出の材質へは渡さない。
public sealed partial class GameplayCutFeedback
{
    public const float ProtectionDelaySeconds = .07f;
    public const double ProtectionLeadSeconds = .24;
    public const float ProtectionPaddingPixels = 12f;
    static readonly int RectsId = Shader.PropertyToID("_NoteMaskRects");
    static readonly int RectCountId = Shader.PropertyToID("_NoteMaskCount");
    readonly Dictionary<CuttableNote, MeshRenderer> protectionTargets = new Dictionary<CuttableNote, MeshRenderer>();
    readonly List<Vector2> protectionUVs = new List<Vector2>(MaxBursts * VerticesPerBurst);
    readonly Vector3[] protectionCorners = new Vector3[8];
    Vector4[] protectionRects;
    ComputeBuffer protectionBuffer;
    float protectionWeight;
    double protectionSongTime = double.NaN;
    public bool ProtectDisabledPreviewNotes { get; set; }
    internal int ProtectionCount { get; private set; }
    internal Vector4 ProtectionRect(int index) => protectionRects[index];

    public void SetProtectionTime(double songTime) { protectionSongTime = songTime; }

    // 譜面読み込み時に全ノーツ数ぶん確保。プレビュー等の追加登録時だけ必要なら拡張する。
    public void ReserveProtection(int count)
    {
        count = Mathf.NextPowerOfTwo(Mathf.Max(16, count));
        if (protectionRects != null && protectionRects.Length >= count) return;
        protectionRects = new Vector4[count];
        protectionBuffer?.Release();
        protectionBuffer = new ComputeBuffer(count, 4 * sizeof(float));
        if (material != null) material.SetBuffer(RectsId, protectionBuffer);
    }

    void OnEnable() { RenderPipelineManager.beginCameraRendering += PrepareProtectionCamera; }

    void PrepareProtectionCamera(ScriptableRenderContext context, Camera camera)
    {
        if (ActiveCount > 0) PrepareProtection(camera, protectionSongTime);
    }

    public void ReleaseProtection()
    {
        RenderPipelineManager.beginCameraRendering -= PrepareProtectionCamera;
        protectionBuffer?.Release();
        protectionBuffer = null;
    }

    public void PrepareProtection(Camera camera, double songTime)
    {
        ProtectionCount = 0;
        if (material == null) return;
        if (camera != null && camera.pixelWidth > 0 && camera.pixelHeight > 0
            && !double.IsNaN(songTime) && !double.IsInfinity(songTime))
        {
            foreach (var pair in protectionTargets)
            {
                var note = pair.Key;
                var body = pair.Value;
                if (note == null || (!note.isActiveAndEnabled && !(ProtectDisabledPreviewNotes && note.gameObject.activeInHierarchy)) || note.IsCut || note.IsMissed || note.IsFinalized
                    || note.HidesCues || body == null || !body.enabled || !body.gameObject.activeInHierarchy
                    || (camera.cullingMask & (1 << body.gameObject.layer)) == 0) continue;
                double until = note.HitTime - songTime;
                if (double.IsNaN(until) || until < 0 || until > ProtectionLeadSeconds + 1e-9) continue;
                if (ProjectProtectionRect(camera, body, out Vector4 rect))
                    protectionRects[ProtectionCount++] = rect;
            }
        }
        if (ProtectionCount > 0) protectionBuffer.SetData(protectionRects, 0, 0, ProtectionCount);
        material.SetInt(RectCountId, ProtectionCount);
    }

    bool ProjectProtectionRect(Camera camera, MeshRenderer body, out Vector4 rect)
    {
        Bounds bounds = body.localBounds;
        Vector3 lo = bounds.min, hi = bounds.max;
        float near = camera.nearClipPlane;
        for (int i = 0; i < 8; i++)
        {
            Vector3 local = new Vector3((i & 1) == 0 ? lo.x : hi.x,
                (i & 2) == 0 ? lo.y : hi.y, (i & 4) == 0 ? lo.z : hi.z);
            protectionCorners[i] = camera.transform.InverseTransformPoint(body.transform.TransformPoint(local));
        }
        rect = new Vector4(float.PositiveInfinity, float.PositiveInfinity,
            float.NegativeInfinity, float.NegativeInfinity);
        bool found = false;
        for (int i = 0; i < 8; i++)
        {
            Vector3 a = protectionCorners[i];
            if (a.z >= near) IncludeProtectionPoint(camera, a, ref rect, ref found);
            // 近接面をまたぐ辺も切り詰め、カメラ後方の点を反転投影しない。
            for (int bit = 1; bit <= 4; bit <<= 1)
            {
                if ((i & bit) != 0) continue;
                Vector3 b = protectionCorners[i | bit];
                if ((a.z >= near) == (b.z >= near)) continue;
                IncludeProtectionPoint(camera, Vector3.LerpUnclamped(a, b, (near - a.z) / (b.z - a.z)),
                    ref rect, ref found);
            }
        }
        if (!found) return false;
        float dx = ProtectionPaddingPixels / camera.pixelWidth;
        float dy = ProtectionPaddingPixels / camera.pixelHeight;
        rect.x = Mathf.Max(0f, rect.x - dx); rect.y = Mathf.Max(0f, rect.y - dy);
        rect.z = Mathf.Min(1f, rect.z + dx); rect.w = Mathf.Min(1f, rect.w + dy);
        return rect.x <= rect.z && rect.y <= rect.w;
    }

    static void IncludeProtectionPoint(Camera camera, Vector3 local, ref Vector4 rect, ref bool found)
    {
        Vector3 p = camera.WorldToViewportPoint(camera.transform.TransformPoint(local));
        if (!Finite(p)) return;
        rect.x = Mathf.Min(rect.x, p.x); rect.y = Mathf.Min(rect.y, p.y);
        rect.z = Mathf.Max(rect.z, p.x); rect.w = Mathf.Max(rect.w, p.y);
        found = true;
    }
}
