using UnityEngine;
using UnityEngine.UI;

// コンボ数の後ろで燃える炎の状態。AP(全 Perfect)中は虹色、FC(Bad/Miss 無し)中は金色、どちらも崩れたら消える。
public enum ComboFlameMode { None, Gold, Rainbow }

// 炎の立ち上がり/消滅。目標が変わったら今の炎を一度消してから新しい炎を立ち上げる(AP→FC は虹が消えて金が上がる)。
public sealed class ComboFlameEnvelope
{
    public const float ExtinguishSeconds = .30f;
    public const float RiseSeconds = .45f;
    public ComboFlameMode Shown { get; private set; }
    public float Level { get; private set; }

    public void Tick(ComboFlameMode desired, float dt)
    {
        dt = Mathf.Max(0f, dt);
        if (desired != Shown)
        {
            Level = Mathf.Max(0f, Level - dt / ExtinguishSeconds);
            if (Level > 0f) return;      // まだ消えている途中
            Shown = desired;             // 消え切ったら切り替え、同じフレームから立ち上がり始める
        }
        if (Shown != ComboFlameMode.None) Level = Mathf.Min(1f, Level + dt / RiseSeconds);
        else Level = 0f;
    }

    public void Reset() { Shown = ComboFlameMode.None; Level = 0f; }
}

// 炎の形と色を決める純関数群。画像・パーティクル・シェーダーを使わず、UI の頂点色だけで描く(投影でも沈まない彩度と明度)。
public static class ComboFlameLogic
{
    public const int Segments = 6;           // 1本の炎の縦分割
    public const int Layers = 2;             // 外側の色帯 + 内側の明るい芯
    public const float MinHeight = 48f, MaxHeight = 200f;

    // 判定の集計から目標の炎を決める。1つでも判定があり、Bad/Miss が無ければ FC、さらに全部 Perfect なら AP。
    public static ComboFlameMode DesiredMode(int hits, int perfect, int bad, int miss)
    {
        if (hits <= 0 || bad > 0 || miss > 0) return ComboFlameMode.None;
        return perfect >= hits ? ComboFlameMode.Rainbow : ComboFlameMode.Gold;
    }

    // 強さは曲の進行割合(ユーザー指定)。条件を保っている限り曲の終わりに向けて最大になる。
    public static float Intensity(double songTime, double duration)
    {
        if (duration <= 0 || double.IsNaN(songTime) || double.IsInfinity(songTime)) return 0f;
        return Mathf.Clamp01((float)(songTime / duration));
    }

    public static int TongueCount(float intensity, bool reduced)
    {
        return reduced ? 4 : Mathf.RoundToInt(Mathf.Lerp(5f, 12f, Mathf.Clamp01(intensity)));
    }

    public static float Height(float intensity) { return Mathf.Lerp(MinHeight, MaxHeight, Mathf.Clamp01(intensity)); }

    // コンボの桁数に合わせて炎の横幅を決める(数字の右端に揃える)。
    public static float FlameWidth(int digits) { return Mathf.Clamp(70f + 62f * Mathf.Max(1, digits), 120f, 340f); }

    // 原点は矩形の右下。x は [-width, 0]、y は上向き。
    public static void Fill(VertexHelper vh, ComboFlameMode mode, float intensity, float level, float time, float width, bool reduced)
    {
        if (mode == ComboFlameMode.None || level <= .001f || width <= 0f) return;
        int count = TongueCount(intensity, reduced);
        float height = Height(intensity) * Mathf.Lerp(.35f, 1f, level);
        float alpha = level * Mathf.Lerp(.65f, 1f, intensity);
        float t = reduced ? 0f : time;   // LOW は揺らめかず静止
        for (int layer = 0; layer < Layers; layer++)
        {
            bool core = layer == 1;
            for (int i = 0; i < count; i++)
            {
                float u = count == 1 ? .5f : i / (float)(count - 1);
                float phase = i * 1.7f;
                float x = -width + u * width;
                float wobble = .72f + .14f * Mathf.Sin(t * 2.3f + phase) + .14f * Hash(i);
                float tongueHeight = height * (core ? .62f : 1f) * wobble;
                float baseWidth = width / count * (core ? .8f : 1.5f);
                Palette(mode, u, t, core, out Color root, out Color middle, out Color tip);
                Tongue(vh, x, tongueHeight, baseWidth, t, phase, root, middle, tip, alpha * (core ? .9f : .8f));
            }
        }
    }

    static float Hash(int i) { return Mathf.Repeat(Mathf.Sin(i * 12.9898f) * 43758.5453f, 1f); }

    static void Palette(ComboFlameMode mode, float u, float t, bool core, out Color root, out Color middle, out Color tip)
    {
        if (mode == ComboFlameMode.Rainbow)
        {
            float hue = Mathf.Repeat(u * .9f + t * .12f, 1f);
            Color c = Color.HSVToRGB(hue, .85f, 1f);
            root = Color.Lerp(c, Color.white, core ? .75f : .25f);
            middle = c;
            tip = Color.HSVToRGB(Mathf.Repeat(hue + .12f, 1f), .9f, .95f);
        }
        else
        {
            root = core ? new Color(1f, .98f, .82f) : new Color(1f, .93f, .55f);
            middle = core ? new Color(1f, .85f, .35f) : new Color(1f, .68f, .12f);
            tip = new Color(1f, .35f, .05f);
        }
    }

    // 1本の炎: 根元から先端へ細くなる帯。先端ほど左右へ揺れ、透明になる。根元も少しだけ薄くして数字の下端に馴染ませる。
    static void Tongue(VertexHelper vh, float x, float h, float w, float t, float phase, Color root, Color middle, Color tip, float alpha)
    {
        int first = vh.currentVertCount;
        for (int j = 0; j <= Segments; j++)
        {
            float v = j / (float)Segments;
            float half = w * .5f * Mathf.Pow(1f - v, .85f) * (1f + .12f * Mathf.Sin(t * 5f + phase + v * 6f));
            float sway = w * .35f * v * v * Mathf.Sin(t * 3.1f + phase + v * 4f);
            Color c = v < .45f ? Color.Lerp(root, middle, v / .45f) : Color.Lerp(middle, tip, (v - .45f) / .55f);
            c.a = alpha * Mathf.Pow(1f - v, 1.15f) * Mathf.Lerp(.55f, 1f, Mathf.Clamp01(v / .15f));
            float cx = x + sway, cy = v * h;
            AddVert(vh, cx - half, cy, c);
            AddVert(vh, cx + half, cy, c);
            if (j > 0)
            {
                int k = first + j * 2;
                vh.AddTriangle(k - 2, k, k + 1);
                vh.AddTriangle(k - 2, k + 1, k - 1);
            }
        }
    }

    static void AddVert(VertexHelper vh, float x, float y, Color c)
    {
        var vert = UIVertex.simpleVert;
        vert.position = new Vector3(x, y, 0f);
        vert.color = c;
        vh.AddVert(vert);
    }
}
