using System;
using UnityEngine;

public readonly struct CameraSaberSample
{
    public readonly double ReceiveTime;
    public readonly Vector3 EndA;
    public readonly Vector3 EndB;
    public readonly CameraSaberColor Color;
    public Vector3 Center => (EndA + EndB) * .5f;

    public CameraSaberSample(double receiveTime, Vector3 a, Vector3 b, CameraSaberColor color)
    {
        ReceiveTime = receiveTime;
        EndA = a;
        EndB = b;
        Color = color;
    }
}

// 固定長リング。表示用の補間点ではなく、新しいCamera入力だけを蓄積する。
public sealed class CameraSaberHistory
{
    readonly CameraSaberSample[] samples = new CameraSaberSample[128];
    int first;
    public int Count { get; private set; }
    public CameraSaberSample this[int index] => samples[(first + index) % samples.Length];

    public void Clear() { first = 0; Count = 0; }

    public bool Add(CameraSaberSample sample, double historySeconds)
    {
        if (!Finite(sample.ReceiveTime) || !Finite(sample.EndA) || !Finite(sample.EndB)) return false;
        if (Count > 0 && sample.ReceiveTime <= this[Count - 1].ReceiveTime) return false;
        Trim(sample.ReceiveTime - historySeconds);
        if (Count == samples.Length) { first = (first + 1) % samples.Length; Count--; }
        samples[(first + Count++) % samples.Length] = sample;
        return true;
    }

    public void Trim(double oldestReceiveTime)
    {
        while (Count > 0 && this[0].ReceiveTime < oldestReceiveTime)
        { first = (first + 1) % samples.Length; Count--; }
    }

    public static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
    static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);

    // Cameraの端点順序の反転は、棒の回転・移動として解釈しない。
    public static void AlignEndpoints(CameraSaberSample previous, ref Vector3 a, ref Vector3 b)
    {
        float direct = (previous.EndA - a).sqrMagnitude + (previous.EndB - b).sqrMagnitude;
        float swapped = (previous.EndA - b).sqrMagnitude + (previous.EndB - a).sqrMagnitude;
        if (swapped < direct) { Vector3 temp = a; a = b; b = temp; }
    }

    // 前後の線分が掃いた四辺形を2三角形で近似する。点入力(端点が一致)にも対応。
    public static float SweptDistance(Vector2 p, Vector2 a0, Vector2 b0, Vector2 a1, Vector2 b1,
        out Vector2 closest)
    {
        if (InsideTriangle(p, a0, b0, b1) || InsideTriangle(p, a0, b1, a1))
        { closest = p; return 0f; }
        float best = SaberCutJudge.DistPointToSegment(p, a0, b0, out closest);
        CheckEdge(p, b0, b1, ref best, ref closest);
        CheckEdge(p, b1, a1, ref best, ref closest);
        CheckEdge(p, a1, a0, ref best, ref closest);
        return best;
    }

    static void CheckEdge(Vector2 p, Vector2 a, Vector2 b, ref float best, ref Vector2 closest)
    {
        float d = SaberCutJudge.DistPointToSegment(p, a, b, out Vector2 q);
        if (d < best) { best = d; closest = q; }
    }

    static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
    static bool InsideTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        if (Mathf.Abs(Cross(b - a, c - a)) < .000001f) return false;
        float x = Cross(b - a, p - a), y = Cross(c - b, p - b), z = Cross(a - c, p - c);
        return (x >= 0 && y >= 0 && z >= 0) || (x <= 0 && y <= 0 && z <= 0);
    }
}
