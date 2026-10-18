using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

// 「この赤を無視する」（2026-10-18: 実験場所の赤いコーンを iPhone がときどき剣と取り違え、
// 照準が左下へ飛んで曲を選べず、本編でも剣が跳んだ）。認識は変えず、PC 側で受信を捨てる。
// 剣をカメラの外に出して F8 のボタンを押すと、そのとき届いている両端を囲む範囲（カメラ画像の 0..1 座標）を
// 台ごとに覚える。以後、両端とも同じ範囲に入る受信は使わない（前の位置を保つ）。剣が範囲を横切っても、
// 両端が同時に範囲内に収まらない限り捨てない。
public static class PhoneSaberIgnoreZones
{
    public const int MaximumZones = 4;
    public const float Margin = .04f, MinimumSize = .06f;
    public static int Revision { get; private set; }
    // 実行中のゲームだけで有効（テスト・検査では保存設定を読まない）。
    public static bool Enabled { get; set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void EnableAtStartup() { if (PhoneSaberP2PBridgeProcess.AutoStartEnabled) Enabled = true; }

    public static string Key(string station) => "PhoneSaber.IgnoreZones." + PhoneSaberStation.Normalize(station) + ".v1";

    public static List<Rect> Load(string station) => Parse(PlayerPrefs.GetString(Key(station), ""));

    public static List<Rect> Parse(string text)
    {
        var zones = new List<Rect>();
        if (string.IsNullOrEmpty(text)) return zones;
        foreach (string part in text.Split(';'))
        {
            var v = part.Split(',');
            if (v.Length != 4) continue;
            if (!float.TryParse(v[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
                !float.TryParse(v[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) ||
                !float.TryParse(v[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float w) ||
                !float.TryParse(v[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float h)) continue;
            if (!(w > 0 && h > 0) || float.IsInfinity(x + y + w + h)) continue;
            zones.Add(new Rect(x, y, w, h));
            if (zones.Count >= MaximumZones) break;
        }
        return zones;
    }

    public static string Format(List<Rect> zones)
    {
        var text = new StringBuilder();
        foreach (var z in zones)
        {
            if (text.Length > 0) text.Append(';');
            text.Append(string.Format(CultureInfo.InvariantCulture, "{0:0.####},{1:0.####},{2:0.####},{3:0.####}", z.x, z.y, z.width, z.height));
        }
        return text.ToString();
    }

    // 両端（0..1）を囲み、少し広げた範囲。古い順に最大 MaximumZones か所。
    public static Rect ZoneFor(Vector2 a, Vector2 b)
    {
        Vector2 min = Vector2.Min(a, b) - Vector2.one * Margin, max = Vector2.Max(a, b) + Vector2.one * Margin;
        Vector2 size = Vector2.Max(max - min, Vector2.one * MinimumSize);
        Vector2 centre = (min + max) * .5f;
        return new Rect(centre - size * .5f, size);
    }

    public static bool Add(string station, Vector2 a, Vector2 b)
    {
        if (!Finite(a) || !Finite(b)) return false;
        var zones = Load(station);
        if (zones.Count >= MaximumZones) zones.RemoveAt(0);
        Rect zone = ZoneFor(a, b);
        zones.Add(zone);
        PlayerPrefs.SetString(Key(station), Format(zones));
        PlayerPrefs.Save();
        Revision++;
        PhoneSaberEventLog.Record(station, "SYSTEM", "ignore-zone-add", "zone=" + Format(new List<Rect> { zone }) + " count=" + zones.Count);
        return true;
    }

    public static void Clear(string station)
    {
        PlayerPrefs.DeleteKey(Key(station));
        PlayerPrefs.Save();
        Revision++;
        PhoneSaberEventLog.Record(station, "SYSTEM", "ignore-zone-clear", "");
    }

    public static bool Covers(List<Rect> zones, Vector2 a, Vector2 b)
    {
        if (zones == null) return false;
        foreach (var z in zones)
            if (z.Contains(a) && z.Contains(b)) return true;
        return false;
    }

    static bool Finite(Vector2 v) => !float.IsNaN(v.x) && !float.IsInfinity(v.x) && !float.IsNaN(v.y) && !float.IsInfinity(v.y);
}

// 照準（メニューの狙いの点）の跳び止め。刃の揺れ止めと同じ考え方で、前の点から screenFraction 以上
// 離れた受信を最大2回だけ捨てる。同じ受信時刻は一度だけ処理する。
public sealed class PhoneSaberPointJumpGate
{
    public const int MaximumRejectedSamples = 2;
    Vector2 last;
    double lastTime = double.NaN;
    bool has;
    int rejected;

    public void Reset() { has = false; rejected = 0; lastTime = double.NaN; }

    public Vector2 Apply(double receiveTime, Vector2 point, float jumpDistance)
    {
        if (float.IsNaN(point.x) || float.IsNaN(point.y) || double.IsNaN(receiveTime)) return point;
        if (has && receiveTime == lastTime) return last;
        lastTime = receiveTime;
        if (has && (point - last).sqrMagnitude > jumpDistance * jumpDistance && rejected < MaximumRejectedSamples)
        {
            rejected++;
            return last;
        }
        rejected = 0; has = true; last = point;
        return point;
    }
}
