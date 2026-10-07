using System;
using UnityEngine;

// 左上・右上・右下・左下のカメラ座標を入力矩形へ写す。Unity API は保存時だけ使う。
public sealed class PhoneSaberPositionCalibration
{
    public const float Width = 1920f;
    public const float Height = 1080f;
    public const float OutsideMargin = 0.1f;
    readonly double[] matrix;
    readonly double[] inverse;
    readonly Vector2[] corners;

    PhoneSaberPositionCalibration(Vector2[] points, double[] forward, double[] backward)
    {
        corners = (Vector2[])points.Clone();
        matrix = forward;
        inverse = backward;
    }

    public Vector2[] CopyCorners() => (Vector2[])corners.Clone();
    public static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    public static Vector2 Apply(PhoneSaberPositionCalibration calibration, bool enabled, Vector2 point)
        => enabled && calibration != null ? calibration.Map(point) : point;

    // 無効時はクランプも行わず、生座標を完全に維持する。
    public Vector2 Map(Vector2 point)
    {
        double x = point.x / Width, y = point.y / Height;
        double denominator = matrix[6] * x + matrix[7] * y + matrix[8];
        // 領域外の射影の極で反対側へ飛ばないよう、分母を領域内と同じ正符号に保つ。
        denominator = Math.Max(1e-8, denominator);
        double u = (matrix[0] * x + matrix[1] * y + matrix[2]) / denominator;
        double v = (matrix[3] * x + matrix[4] * y + matrix[5]) / denominator;
        return new Vector2((float)(Math.Max(-OutsideMargin, Math.Min(1 + OutsideMargin, u)) * Width),
            (float)(Math.Max(-OutsideMargin, Math.Min(1 + OutsideMargin, v)) * Height));
    }

    public Vector2 Unmap(Vector2 point)
    {
        double x = point.x / Width, y = point.y / Height;
        double d = inverse[6] * x + inverse[7] * y + inverse[8];
        return new Vector2((float)((inverse[0] * x + inverse[1] * y + inverse[2]) / d * Width),
            (float)((inverse[3] * x + inverse[4] * y + inverse[5]) / d * Height));
    }

    public static bool TryCreate(Vector2[] points, out PhoneSaberPositionCalibration calibration, out string error)
    {
        calibration = null;
        error = "4隅を左上→右上→右下→左下の順で測定してください。";
        if (points == null || points.Length != 4) return false;
        double winding = 0, area = 0;
        for (int i = 0; i < 4; i++)
        {
            Vector2 a = points[i], b = points[(i + 1) % 4], c = points[(i + 2) % 4];
            if (!IsFinite(a.x) || !IsFinite(a.y) || Math.Abs(a.x) > 1000000 || Math.Abs(a.y) > 1000000) return false;
            double dx = b.x - a.x, dy = b.y - a.y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            double cross = dx * (c.y - b.y) - dy * (c.x - b.x);
            // 短い辺・ほぼ一直線・交差・凹形を拒否。反転済みのカメラ座標にも対応する。
            if (length < 20 || Math.Abs(cross) / length < 10 || (i > 0 && cross * winding <= 0))
                return false;
            winding = cross;
            area += (double)a.x * b.y - (double)a.y * b.x;
        }
        if (Math.Abs(area) < 3200) return false;

        // 単位を揃えた8元連立方程式を部分ピボット付きで解く (h33=1)。
        var system = new double[8, 9];
        for (int i = 0; i < 4; i++)
        {
            double x = points[i].x / Width, y = points[i].y / Height;
            double u = i == 1 || i == 2 ? 1 : 0, v = i >= 2 ? 1 : 0;
            int r = i * 2;
            system[r, 0] = x; system[r, 1] = y; system[r, 2] = 1;
            system[r, 6] = -u * x; system[r, 7] = -u * y; system[r, 8] = u;
            system[r + 1, 3] = x; system[r + 1, 4] = y; system[r + 1, 5] = 1;
            system[r + 1, 6] = -v * x; system[r + 1, 7] = -v * y; system[r + 1, 8] = v;
        }
        for (int col = 0; col < 8; col++)
        {
            int pivot = col;
            for (int row = col + 1; row < 8; row++)
                if (Math.Abs(system[row, col]) > Math.Abs(system[pivot, col])) pivot = row;
            if (Math.Abs(system[pivot, col]) < 1e-10) return false;
            for (int j = col; j <= 8; j++)
            {
                double tmp = system[col, j]; system[col, j] = system[pivot, j]; system[pivot, j] = tmp;
            }
            double divisor = system[col, col];
            for (int j = col; j <= 8; j++) system[col, j] /= divisor;
            for (int row = 0; row < 8; row++)
            {
                if (row == col) continue;
                double factor = system[row, col];
                for (int j = col; j <= 8; j++) system[row, j] -= factor * system[col, j];
            }
        }
        var h = new double[9];
        for (int i = 0; i < 8; i++) h[i] = system[i, 8];
        h[8] = 1;
        double sign = Math.Sign(h[6] * points[0].x / Width + h[7] * points[0].y / Height + h[8]);
        for (int i = 0; i < 9; i++) h[i] *= sign;
        foreach (Vector2 p in points)
            if (h[6] * p.x / Width + h[7] * p.y / Height + h[8] < 1e-8) return false;
        var inv = new[] {
            h[4]*h[8]-h[5]*h[7], h[2]*h[7]-h[1]*h[8], h[1]*h[5]-h[2]*h[4],
            h[5]*h[6]-h[3]*h[8], h[0]*h[8]-h[2]*h[6], h[2]*h[3]-h[0]*h[5],
            h[3]*h[7]-h[4]*h[6], h[1]*h[6]-h[0]*h[7], h[0]*h[4]-h[1]*h[3] };
        double determinant = h[0] * inv[0] + h[1] * inv[3] + h[2] * inv[6];
        if (Math.Abs(determinant) < 1e-10) return false;
        calibration = new PhoneSaberPositionCalibration(points, h, inv);
        error = "";
        return true;
    }
}

// 台なしは空ラベル専用のキーとし、台名「None」などと衝突させない。
public static class PhoneSaberPositionCalibrationStore
{
    public static string DataKey(string station) => "PhoneSaber.PositionCalibration." + PhoneSaberStation.Normalize(station) + ".Corners.v1";
    public static string EnabledKey(string station) => "PhoneSaber.PositionCalibration." + PhoneSaberStation.Normalize(station) + ".Enabled";

    [Serializable]
    sealed class Data
    {
        public int version = 1;
        public Vector2[] corners;
    }

    public static PhoneSaberPositionCalibration Load(string station, out bool enabled)
    {
        enabled = false;
        try
        {
            var data = JsonUtility.FromJson<Data>(PlayerPrefs.GetString(DataKey(station), ""));
            if (data == null || data.version != 1 ||
                !PhoneSaberPositionCalibration.TryCreate(data.corners, out var calibration, out _)) return null;
            enabled = PlayerPrefs.GetInt(EnabledKey(station), 0) == 1;
            return calibration;
        }
        catch (ArgumentException) { return null; }
    }

    public static void Save(string station, PhoneSaberPositionCalibration calibration)
    {
        PlayerPrefs.SetString(DataKey(station), JsonUtility.ToJson(new Data { corners = calibration.CopyCorners() }));
        SetEnabled(station, true);
    }

    public static void SetEnabled(string station, bool enabled)
    {
        PlayerPrefs.SetInt(EnabledKey(station), enabled ? 1 : 0);
        PlayerPrefs.Save();
    }

    public static void Reset(string station)
    {
        PlayerPrefs.DeleteKey(DataKey(station));
        PlayerPrefs.DeleteKey(EnabledKey(station));
        PlayerPrefs.Save();
    }
}
