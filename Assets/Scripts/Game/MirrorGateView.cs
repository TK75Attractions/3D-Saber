using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Manic Sonic の裏譜面 R の鏡門(U105)の絵。1枚の動的メッシュ(QuizMesh)に毎コマ描く(ノーツごとの資源を増やさない)。
//   ・門: 細い枠(内側 5.2 × 2.5、中心はノーツの帯の中心)。左半分が青・右半分が赤(手のいつもの側)、角は目印の鏡銀。光は素通しで 40%。
//     鏡にするときはトランプのように縦の軸で 180° 裏返り、そのまま「左赤・右青」になる(光 100%)。鏡の間は真ん中の軸の線と内側のガラスの縁。
//   ・奥から流れてきて、届く 0.5 秒前の深さ(judgeZ + 速さ × 0.5)で止まる。深さは見る(手前のノーツが門を隠す)。
//   ・割れるとき: 枠がガラスの破片になって、帯の外側と奥へ飛び、0.6 秒で消える(ゲートの内側を光らせないよう外へ飛ばす)。
//     F3 では破片を出さず、0.3 秒で薄くなって消える。
//   ・門を通ったノーツ: 通った所から出た所へノーツの色の細い筋(0.2 秒)と、両端の小さな波紋(F3 では出さない)。
//   ・床の判定線: 鏡の間は左赤・右青(カウントインの塗り分けの逆。FloorTimingGuide.SetCountTint を共有するので、鏡の外では必ず戻す)。
// 色は頂点色(線形の色空間なので、材質の色と同じ見え方になるよう color.linear にする)。
public sealed class MirrorGateView : IDisposable
{
    // 割れる破片の飛ぶ速さ(帯の外へ)と、奥へ下がる速さ(ワールド/秒)。
    public const float ShardSpeed = 2.8f, ShardRecede = 1.6f;
    const int PiecesPerBar = 4;

    struct Streak { public double start; public float from, to, y; public Color color; }

    readonly ReverseChartData data;
    readonly FloorTimingGuide floor;
    readonly List<Streak> streaks = new List<Streak>(16);
    QuizMesh mesh;
    bool tinted;

    public ReverseRules.GatePhase Phase { get; private set; }
    public float Flip { get; private set; }
    public float Glow { get; private set; }
    // 門の止まる深さと、このコマに描いた門の深さ。
    public float GateZ { get; private set; }
    public float DrawnZ { get; private set; }
    public int ShardsDrawn { get; private set; }
    public int StreaksDrawn { get; private set; }
    public int PendingStreaks => streaks.Count;
    public float Tint { get; private set; }
    public MeshRenderer Renderer => mesh != null ? mesh.Renderer : null;
    public Mesh Mesh => mesh != null ? mesh.Mesh : null;

    public MirrorGateView(Transform parent, ReverseChartData data, FloorTimingGuide floor)
    {
        this.data = data;
        this.floor = floor;
        mesh = new QuizMesh(parent, "MirrorGate", 0, world: true);
        var material = mesh.Renderer != null ? mesh.Renderer.sharedMaterial : null;
        if (material != null && material.HasProperty("_ZTest")) material.SetFloat("_ZTest", (float)CompareFunction.LessEqual);
    }

    static Color Vertex(Color color, float alpha)
    {
        Color linear = color.linear;
        linear.a = Mathf.Clamp01(alpha);
        return linear;
    }

    // seconds は譜面の時計の秒。approach は R の実効の接近時間、speed はノーツの速さ。
    public void Draw(double seconds, double approach, float speed, float judgeZ)
    {
        Phase = ReverseRules.GatePhase.None;
        Flip = Glow = 0f;
        ShardsDrawn = StreaksDrawn = 0;
        GateZ = judgeZ + speed * (float)ReverseRules.CrossSeconds;
        DrawnZ = float.NaN;
        if (mesh == null || !ReverseRules.Finite(seconds)) return;
        mesh.Clear();
        bool reduced = DisplaySettings.ReducedEffects;
        if (data?.gates != null)
            foreach (var gate in data.gates)
            {
                var phase = ReverseRules.GateState(gate, seconds, approach);
                if (phase == ReverseRules.GatePhase.None) continue;
                Phase = phase;
                double stop = gate.time / 1000.0, end = gate.endTime / 1000.0;
                float z = GateZ + speed * (float)Math.Max(0, stop - seconds);
                float alpha = phase == ReverseRules.GatePhase.Arriving
                    ? Mathf.Clamp01((float)((seconds - (stop - ReverseRules.ArriveSeconds(approach))) / ReverseRules.GateFadeInSeconds)) : 1f;
                bool broken = phase == ReverseRules.GatePhase.Broken;
                float flip = ReverseRules.GateFlip(gate, broken ? end - 1e-4 : seconds);
                float mirror = flip / 180f;
                Flip = flip;
                Glow = Mathf.Lerp(ReverseRules.PassGlow, ReverseRules.MirrorGlow, mirror);
                DrawnZ = z;
                if (broken)
                {
                    double age = seconds - end;
                    if (reduced)
                    {
                        alpha *= 1f - Mathf.Clamp01((float)(age / ReverseRules.ReducedFadeSeconds));
                        if (alpha > 0f) DrawFrame(z, flip, Glow * alpha, mirror * alpha);
                    }
                    else DrawShards(z, flip, Glow, age);
                    continue;
                }
                DrawFrame(z, flip, Glow * alpha, mirror * alpha);
            }
        DrawStreaks(seconds, reduced);
        mesh.Flush();
        UpdateFloorTint(seconds);
    }

    // 門の中の座標(x, y)を、縦の軸(x = 0)のまわりに flip 度裏返したワールドの点へ。
    static Vector3 Place(float x, float y, float z, float flip)
    {
        float r = flip * Mathf.Deg2Rad;
        return new Vector3(x * Mathf.Cos(r), y, z + x * Mathf.Sin(r));
    }

    void Band(Vector2 a, Vector2 b, float width, float z, float flip, Color color)
    {
        Vector2 along = b - a;
        if (along.sqrMagnitude < 1e-8f || width <= 0f || color.a <= 0f) return;
        Vector2 side = new Vector2(-along.y, along.x).normalized * (width * .5f);
        mesh.Quad(Place(a.x - side.x, a.y - side.y, z, flip), Place(b.x - side.x, b.y - side.y, z, flip),
            Place(b.x + side.x, b.y + side.y, z, flip), Place(a.x + side.x, a.y + side.y, z, flip), color);
    }

    void DrawFrame(float z, float flip, float glow, float mirror)
    {
        float w = ReverseRules.GateHalfWidth, h = ReverseRules.GateHalfHeight, t = ReverseRules.GateBar;
        float cy = ReverseRules.BandCenter.y;
        float top = cy + h + t * .5f, bottom = cy - h - t * .5f, left = -(w + t * .5f), right = w + t * .5f;
        Color blue = Vertex(UISkinPalette.LogoBlue, glow), red = Vertex(UISkinPalette.LogoRed, glow);
        // 左半分は青、右半分は赤(裏返すと左赤・右青)。
        Band(new Vector2(-(w + t), top), new Vector2(0f, top), t, z, flip, blue);
        Band(new Vector2(0f, top), new Vector2(w + t, top), t, z, flip, red);
        Band(new Vector2(-(w + t), bottom), new Vector2(0f, bottom), t, z, flip, blue);
        Band(new Vector2(0f, bottom), new Vector2(w + t, bottom), t, z, flip, red);
        Band(new Vector2(left, bottom - t * .5f), new Vector2(left, top + t * .5f), t, z, flip, blue);
        Band(new Vector2(right, bottom - t * .5f), new Vector2(right, top + t * .5f), t, z, flip, red);
        // 角は目印の鏡銀。
        Color corner = Vertex(RushESecretState.MirrorSilver, Mathf.Min(1f, glow * 1.5f));
        for (int i = 0; i < 4; i++)
        {
            float x = (i & 1) == 0 ? left : right, y = (i & 2) == 0 ? bottom : top;
            Band(new Vector2(x - t, y), new Vector2(x + t, y), t * 2f, z, flip, corner);
        }
        if (mirror <= 0f) return;
        // 鏡の間: 真ん中の縦の軸の線と、枠の内側に沿った細いガラスの縁。
        Color axis = Vertex(RushESecretState.MirrorSilver, .8f * mirror);
        Band(new Vector2(0f, cy - h), new Vector2(0f, cy + h), .025f, z, flip, axis);
        Color rim = Vertex(RushESecretState.MirrorSilver, .5f * mirror);
        float iw = w - .1f, ih = h - .1f;
        Band(new Vector2(-iw, cy + ih), new Vector2(iw, cy + ih), .02f, z, flip, rim);
        Band(new Vector2(-iw, cy - ih), new Vector2(iw, cy - ih), .02f, z, flip, rim);
        Band(new Vector2(-iw, cy - ih), new Vector2(-iw, cy + ih), .02f, z, flip, rim);
        Band(new Vector2(iw, cy - ih), new Vector2(iw, cy + ih), .02f, z, flip, rim);
    }

    // 割れた枠の破片。枠の6本の棒をそれぞれ4つに割り、帯の外側(門の中心から離れる向き)と奥へ飛ばす。
    void DrawShards(float z, float flip, float glow, double age)
    {
        float p = Mathf.Clamp01((float)(age / ReverseRules.ShatterSeconds));
        if (p >= 1f) return;
        float alpha = glow * (1f - p) * (1f - p);
        float w = ReverseRules.GateHalfWidth, h = ReverseRules.GateHalfHeight, t = ReverseRules.GateBar;
        float cy = ReverseRules.BandCenter.y;
        float top = cy + h + t * .5f, bottom = cy - h - t * .5f, left = -(w + t * .5f), right = w + t * .5f;
        var bars = new (Vector2 a, Vector2 b, Color color)[]
        {
            (new Vector2(-(w + t), top), new Vector2(0f, top), UISkinPalette.LogoBlue),
            (new Vector2(0f, top), new Vector2(w + t, top), UISkinPalette.LogoRed),
            (new Vector2(-(w + t), bottom), new Vector2(0f, bottom), UISkinPalette.LogoBlue),
            (new Vector2(0f, bottom), new Vector2(w + t, bottom), UISkinPalette.LogoRed),
            (new Vector2(left, bottom), new Vector2(left, top), UISkinPalette.LogoBlue),
            (new Vector2(right, bottom), new Vector2(right, top), UISkinPalette.LogoRed),
        };
        var center = new Vector3(0f, cy, z);
        float travel = (float)age;
        for (int bar = 0; bar < bars.Length; bar++)
        {
            Vector2 a = bars[bar].a, b = bars[bar].b;
            Color color = Vertex(bars[bar].color, alpha);
            for (int k = 0; k < PiecesPerBar; k++)
            {
                Vector2 from = Vector2.Lerp(a, b, k / (float)PiecesPerBar), to = Vector2.Lerp(a, b, (k + 1) / (float)PiecesPerBar);
                Vector2 mid = (from + to) * .5f;
                Vector3 start = Place(mid.x, mid.y, z, flip);
                Vector3 outward = start - center;
                outward.z = 0f;
                outward = outward.sqrMagnitude > 1e-6f ? outward.normalized : Vector3.up;
                Vector3 position = start + outward * (ShardSpeed * travel) + Vector3.forward * (ShardRecede * travel);
                float spin = (k * 1.7f + bar * .9f + 1f) * travel * 6f;
                float half = (to - from).magnitude * .5f * (1f - .4f * p);
                Vector2 dir = new Vector2(Mathf.Cos(spin), Mathf.Sin(spin)) * half;
                Vector2 side = new Vector2(-dir.y, dir.x).normalized * (t * .5f);
                mesh.Quad(position + new Vector3(-dir.x - side.x, -dir.y - side.y, 0f),
                    position + new Vector3(dir.x - side.x, dir.y - side.y, 0f),
                    position + new Vector3(dir.x + side.x, dir.y + side.y, 0f),
                    position + new Vector3(-dir.x + side.x, -dir.y + side.y, 0f), color);
                ShardsDrawn++;
            }
        }
    }

    // 門を通ったノーツの筋(ReverseMode が通った瞬間に呼ぶ)。start は通った時刻(譜面の時計の秒)、from は鏡像の x、to は本当の x。
    public void AddStreak(double start, float from, float to, float y, Color color)
    {
        if (DisplaySettings.ReducedEffects || !ReverseRules.Finite(start)) return;
        if (streaks.Count >= 16) streaks.RemoveAt(0);
        streaks.Add(new Streak { start = start, from = from, to = to, y = y, color = color });
    }

    void DrawStreaks(double seconds, bool reduced)
    {
        for (int i = streaks.Count - 1; i >= 0; i--)
        {
            var streak = streaks[i];
            double age = seconds - streak.start;
            if (age >= ReverseRules.StreakSeconds || age < -1) { streaks.RemoveAt(i); continue; }
            if (reduced || age < 0) continue;
            float fade = 1f - (float)(age / ReverseRules.StreakSeconds);
            Color tint = Color.Lerp(streak.color, Color.white, .4f);
            Color color = Vertex(tint, ReverseRules.StreakAlpha * fade);
            mesh.Line(new Vector3(streak.from, streak.y, GateZ), new Vector3(streak.to, streak.y, GateZ), ReverseRules.StreakWidth, color);
            float radius = Mathf.Lerp(.12f, ReverseRules.RippleRadius, (float)(age / ReverseRules.StreakSeconds));
            mesh.Ring(new Vector2(streak.from, streak.y), GateZ, radius, .03f, color, 24);
            mesh.Ring(new Vector2(streak.to, streak.y), GateZ, radius, .03f, color, 24);
            StreaksDrawn++;
        }
    }

    void UpdateFloorTint(double seconds)
    {
        Tint = ReverseRules.MirrorTint(data, seconds);
        if (floor == null) return;
        if (Tint > 0f)
        {
            floor.SetCountTint(UISkinPalette.LogoRed, Tint, UISkinPalette.LogoBlue, Tint);
            tinted = true;
        }
        else ClearTint();
    }

    void ClearTint()
    {
        if (!tinted) return;
        tinted = false;
        if (floor != null) floor.ClearCountTint();
    }

    // 止まっている間はそのまま。終わり・中断で隠し、床の塗り分けを戻す。
    public void Hide()
    {
        if (mesh != null) mesh.Hide();
        streaks.Clear();
        ClearTint();
    }

    public void Dispose()
    {
        Hide();
        mesh?.Dispose();
        mesh = null;
    }
}
