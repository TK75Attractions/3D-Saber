using UnityEngine;

// 採点や絵を変えず、受理した一振りの根拠だけを渡す。
public struct SaberCutRecord
{
    public int Frame, Sample;
    public double ObservedAt;
    public ulong StrokeId;
    public bool Sweep, DirectionValid;
    public Vector2 Direction;

    public static SaberCutRecord Create(int sample, ulong stroke, bool sweep, Vector3 from, Vector3 to,
        float fromTurn, float toTurn, float verticalScale, Camera camera, bool valid)
    {
        var record = new SaberCutRecord { Frame = Time.frameCount, Sample = sample,
            ObservedAt = Time.realtimeSinceStartupAsDouble, StrokeId = stroke, Sweep = sweep };
        record.Direction = ScreenMotion(from, to, fromTurn, toTurn, verticalScale, camera);
        record.DirectionValid = valid && camera != null && CameraSaberHistory.Finite(record.Direction.x)
            && CameraSaberHistory.Finite(record.Direction.y) && record.Direction.sqrMagnitude > 1e-8f;
        return record;
    }

    // 以前の点も今の入力回転へ写してから、同じ投影で2点を見る。背景だけの回転は差にならない。
    public static Vector2 ScreenMotion(Vector3 from, Vector3 to, float fromTurn, float toTurn, float verticalScale, Camera camera)
    {
        if (camera == null) return Vector2.zero;
        from = WorldTurn.MapInput(WorldTurn.MapInput(from, -fromTurn), toTurn);
        if ((to - from).sqrMagnitude <= 1e-8f) return Vector2.zero;
        Vector3 a = camera.WorldToScreenPoint(from), b = camera.WorldToScreenPoint(to);
        return new Vector2(b.x - a.x, (b.y - a.y) * verticalScale);
    }
}
