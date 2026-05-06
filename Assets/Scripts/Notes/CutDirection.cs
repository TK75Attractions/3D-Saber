using UnityEngine;

public enum CutDirection
{
    None,
    Up, Down, Left, Right,
    UpLeft, UpRight, DownLeft, DownRight
}

public static class CutDirectionHelper
{
    // chart.json の direction 文字列を enum に。
    public static CutDirection Parse(string s)
    {
        if (string.IsNullOrEmpty(s)) return CutDirection.None;
        switch (s.ToLowerInvariant().Replace("-", "").Replace("_", "").Replace(" ", ""))
        {
            case "none": return CutDirection.None;
            case "up": return CutDirection.Up;
            case "down": return CutDirection.Down;
            case "left": return CutDirection.Left;
            case "right": return CutDirection.Right;
            case "upleft": return CutDirection.UpLeft;
            case "upright": return CutDirection.UpRight;
            case "downleft": return CutDirection.DownLeft;
            case "downright": return CutDirection.DownRight;
            default: return CutDirection.None;
        }
    }

    public static Vector2 ToVector(CutDirection d)
    {
        const float s = 0.7071068f; // 1/sqrt(2)
        switch (d)
        {
            case CutDirection.Up: return new Vector2(0f, 1f);
            case CutDirection.Down: return new Vector2(0f, -1f);
            case CutDirection.Left: return new Vector2(-1f, 0f);
            case CutDirection.Right: return new Vector2(1f, 0f);
            case CutDirection.UpLeft: return new Vector2(-s, s);
            case CutDirection.UpRight: return new Vector2(s, s);
            case CutDirection.DownLeft: return new Vector2(-s, -s);
            case CutDirection.DownRight: return new Vector2(s, -s);
            default: return Vector2.zero;
        }
    }

    // Z 軸まわり回転角（矢印の見た目を required 方向に向ける用）。Up が 0°、CCW プラス。
    public static float ToZRotationDegrees(CutDirection d)
    {
        switch (d)
        {
            case CutDirection.Up: return 0f;
            case CutDirection.UpLeft: return 45f;
            case CutDirection.Left: return 90f;
            case CutDirection.DownLeft: return 135f;
            case CutDirection.Down: return 180f;
            case CutDirection.DownRight: return 225f;
            case CutDirection.Right: return 270f;
            case CutDirection.UpRight: return 315f;
            default: return 0f;
        }
    }

    // 切断ベクトル（XY）が要求方向と一致するか。tolerance は dot 閾値（cos）。
    // 既定は 0.866（=cos30°）：要求方向に対して ±30° の範囲のみ一致と判定する厳しめの設定。
    public static bool Matches(CutDirection required, Vector2 cutVelXY, float tolerance = 0.866f)
    {
        if (required == CutDirection.None) return true;
        if (cutVelXY.sqrMagnitude < 0.0001f) return false;
        Vector2 v = cutVelXY.normalized;
        Vector2 r = ToVector(required);
        return Vector2.Dot(v, r) >= tolerance;
    }

    // 速度方向か IMU 検知方向のどちらかが一致すれば OK。
    public static bool MatchesWithHint(CutDirection required, Vector2 cutVelXY, CutDirection imuHint)
    {
        if (required == CutDirection.None) return true;
        if (Matches(required, cutVelXY)) return true;
        if (imuHint != CutDirection.None && imuHint == required) return true;
        return false;
    }

    // Swing8DirectionLogger の 0..7 インデックスを enum に。
    public static CutDirection FromSwing8Index(int idx)
    {
        switch (idx)
        {
            case 0: return CutDirection.Right;
            case 1: return CutDirection.UpRight;
            case 2: return CutDirection.Up;
            case 3: return CutDirection.UpLeft;
            case 4: return CutDirection.Left;
            case 5: return CutDirection.DownLeft;
            case 6: return CutDirection.Down;
            case 7: return CutDirection.DownRight;
            default: return CutDirection.None;
        }
    }
}
