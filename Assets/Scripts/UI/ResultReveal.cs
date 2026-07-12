using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// リザルト画面の段階出現演出(「シャッ、シャッ、デーン」)。
// 各要素を CanvasGroup 化して登録し、経過時間に応じて
//   Slide: 横/縦からスッと滑り込む(シャッ)
//   Slam : 大きく出現して着地バウンド(デーン。ランクバッジ用)
// を適用する。クリック/任意キーで全要素即表示(スキップ)。
// アニメ計算は Evaluate(...) に切り出してあり EditMode テストから直接駆動できる。
public class ResultReveal : MonoBehaviour
{
    public enum Kind { Slide, Slam }

    public struct Pose
    {
        public float alpha;
        public float scale;
        public Vector2 offset; // 基準位置からのずれ
    }

    private class Entry
    {
        public CanvasGroup cg;
        public RectTransform rt;
        public Vector2 basePos;
        public Kind kind;
        public float delay;
        public float duration;
        public Vector2 from;
    }

    // Slam の前半(拡大→着地)の割合。残りがバウンド。
    public const float SlamLandPortion = 0.6f;
    public const float SlamStartScale = 2.8f;
    public const float SlamBounceScale = 0.12f;

    private readonly List<Entry> entries = new List<Entry>();
    private float elapsed;

    // 経過時間(要素の delay 差し引き後)→ 表示状態。純関数。
    public static Pose Evaluate(Kind kind, float tSinceDelay, float duration, Vector2 slideFrom)
    {
        Pose pose;
        pose.scale = kind == Kind.Slam ? SlamStartScale : 1f;
        pose.alpha = 0f;
        pose.offset = kind == Kind.Slam ? Vector2.zero : slideFrom;
        if (tSinceDelay <= 0f || duration <= 0f) return pose;

        float p = Mathf.Clamp01(tSinceDelay / duration);
        if (kind == Kind.Slide)
        {
            float ease = 1f - (1f - p) * (1f - p) * (1f - p); // easeOutCubic
            pose.alpha = Mathf.Clamp01(p * 1.6f);
            pose.offset = slideFrom * (1f - ease);
            pose.scale = 1f;
            return pose;
        }

        // Slam(デーン)
        pose.offset = Vector2.zero;
        if (p < SlamLandPortion)
        {
            float q = p / SlamLandPortion;
            pose.alpha = Mathf.Clamp01(q * 2f);
            pose.scale = Mathf.Lerp(SlamStartScale, 1f, q * q); // 加速して着地
        }
        else
        {
            float q = (p - SlamLandPortion) / (1f - SlamLandPortion);
            pose.alpha = 1f;
            pose.scale = 1f + SlamBounceScale * Mathf.Sin(q * Mathf.PI) * (1f - 0.35f * q);
        }
        return pose;
    }

    public static ResultReveal Ensure(Canvas canvas)
    {
        var existing = canvas.GetComponentInChildren<ResultReveal>();
        if (existing != null) return existing;
        var go = new GameObject("ResultReveal", typeof(RectTransform));
        go.transform.SetParent(canvas.transform, false);
        return go.AddComponent<ResultReveal>();
    }

    // 要素を演出対象として登録する。登録した瞬間に非表示(alpha 0)へ初期化する。
    public void Add(GameObject go, float delay, float duration, Kind kind, Vector2 slideFrom)
    {
        if (go == null) return;
        var cg = go.GetComponent<CanvasGroup>();
        if (cg == null) cg = go.AddComponent<CanvasGroup>();
        var rt = go.GetComponent<RectTransform>();
        var e = new Entry
        {
            cg = cg,
            rt = rt,
            basePos = rt != null ? rt.anchoredPosition : Vector2.zero,
            kind = kind,
            delay = delay,
            duration = Mathf.Max(0.01f, duration),
            from = slideFrom,
        };
        entries.Add(e);
        Apply(e, Evaluate(kind, -1f, e.duration, slideFrom));
    }

    public int EntryCount => entries.Count;

    void Update()
    {
        elapsed += Time.unscaledDeltaTime;
        if (SkipRequested()) elapsed = 999f;
        Tick(elapsed);
    }

    // elapsed 秒時点の状態を全要素へ適用(テストから直接呼べる)
    public void Tick(float t)
    {
        foreach (var e in entries)
        {
            Apply(e, Evaluate(e.kind, t - e.delay, e.duration, e.from));
        }
    }

    private static void Apply(Entry e, Pose pose)
    {
        if (e.cg != null) e.cg.alpha = pose.alpha;
        if (e.rt != null)
        {
            e.rt.anchoredPosition = e.basePos + pose.offset;
            e.rt.localScale = new Vector3(pose.scale, pose.scale, 1f);
        }
    }

    private static bool SkipRequested()
    {
        if (Keyboard.current != null && Keyboard.current.anyKey.wasPressedThisFrame) return true;
        if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame) return true;
        return false;
    }
}
