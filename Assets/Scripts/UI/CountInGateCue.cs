using System.Collections.Generic;
using UnityEngine;

// カウントイン(3・2・1)で判定ゲートの辺と床の判定線を拍ごとに点ける(2026-10-05 Web 試作の案B「ゲートの合図」)。
//   3: 左の辺と床の左半分が青(左手) / 2: 右の辺と床の右半分が赤(右手) / 1: 上下の辺と四隅が白 / START: 全部が難易度の色。
// 辺は JudgeGateFrame の GateLeft/GateRight/GateTop/GateBottom で、4本が材質を共有している。
// 共有材質には触らず、レンダラーごとの MaterialPropertyBlock で上書きし、終わったら必ず外す
// (Perfect で共有材質を光らせる GateBeatPulse を邪魔しない)。
public sealed class CountInGateCue
{
    public enum Side { Left, Right, Top, Bottom }

    // START から元の見た目へ戻し始める時刻と、戻しきるまでの秒数
    public const double RestoreFrom = .5;
    public const double RestoreSeconds = .4;

    static readonly string[] EdgeNames = { "GateLeft", "GateRight", "GateTop", "GateBottom" };
    static readonly string[] CornerNames =
    {
        "GateCornerTL_h", "GateCornerTL_v", "GateCornerTR_h", "GateCornerTR_v",
        "GateCornerBL_h", "GateCornerBL_v", "GateCornerBR_h", "GateCornerBR_v"
    };
    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

    readonly MeshRenderer[] edges = new MeshRenderer[4];
    readonly List<MeshRenderer> corners = new List<MeshRenderer>();
    readonly FloorTimingGuide floor;
    readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
    Color edgeBase = GameStageSkin.GateColor;
    Color edgeEmission = GameStageSkin.GateColor * GameStageSkin.GateEmission;
    Color cornerBase = GameStageSkin.GateCornerColor;
    Color cornerEmission = GameStageSkin.GateCornerColor * GameStageSkin.GateCornerEmission;

    public bool HasGate => edges[0] != null;
    public bool Applied { get; private set; }

    public CountInGateCue(Transform gate, FloorTimingGuide floor)
    {
        this.floor = floor;
        if (gate == null) return;
        for (int i = 0; i < EdgeNames.Length; i++)
        {
            var child = gate.Find(EdgeNames[i]);
            edges[i] = child != null ? child.GetComponent<MeshRenderer>() : null;
        }
        foreach (var name in CornerNames)
        {
            var child = gate.Find(name);
            var renderer = child != null ? child.GetComponent<MeshRenderer>() : null;
            if (renderer != null) corners.Add(renderer);
        }
        // 実際の材質の色を元の値として使う(戻すときはブロックを外すだけ)
        ReadColors(edges[0], ref edgeBase, ref edgeEmission);
        if (corners.Count > 0) ReadColors(corners[0], ref cornerBase, ref cornerEmission);
    }

    static void ReadColors(MeshRenderer renderer, ref Color baseColor, ref Color emission)
    {
        var material = renderer != null ? renderer.sharedMaterial : null;
        if (material == null) return;
        if (material.HasProperty(BaseColorId)) baseColor = material.GetColor(BaseColorId);
        if (material.HasProperty(EmissionId)) emission = material.GetColor(EmissionId);
    }

    // どの辺が何番目の数字で点くか(0=「3」、1=「2」、2=「1」、3=START で全部)。
    public static int LightStep(Side side) => side == Side.Left ? 0 : side == Side.Right ? 1 : 2;
    public static bool IsLit(Side side, int step) => step >= LightStep(side);

    // 点いた辺の色。左右はセーバーと同じ(青=左、赤=右)、上下は白、START は難易度の色。
    public static Color ColorFor(Side side, int step, Color accent)
    {
        if (step >= 3) return accent;
        if (side == Side.Left) return UISkinPalette.LogoBlue;
        if (side == Side.Right) return UISkinPalette.LogoRed;
        return Color.white;
    }

    static float Pulse(double age, double seconds) => age < 0 ? 0f : Mathf.Max(0f, 1f - (float)(age / seconds));

    // step: 0〜2 は数字、3 は START。sinceFirstBeat: 「3」の拍からの秒数。startAge: START からの秒数(START 前は負)。
    public void Apply(int step, double sinceFirstBeat, double countSeconds, double startAge, Color accent)
    {
        if (step < 0) { Restore(); return; }
        float back = startAge > RestoreFrom ? Mathf.Clamp01((float)((startAge - RestoreFrom) / RestoreSeconds)) : 0f;
        if (back >= 1f) { Restore(); return; }
        Applied = true;
        float emission = GameStageSkin.GateEmission;
        for (int i = 0; i < edges.Length; i++)
        {
            var renderer = edges[i];
            if (renderer == null) continue;
            var side = (Side)i;
            Color baseColor, glow;
            if (step >= 3)
            {
                float gain = 1.25f + 1.1f * Pulse(startAge, .3);
                baseColor = accent; glow = accent * (emission * gain);
            }
            else if (IsLit(side, step))
            {
                double age = sinceFirstBeat - LightStep(side) * countSeconds;
                float gain = 1.15f + .9f * Pulse(age, .25);
                Color color = ColorFor(side, step, accent);
                baseColor = color; glow = color * (emission * gain);
            }
            else
            {
                // まだ点いていない辺は暗くして、点いた辺を目立たせる
                baseColor = edgeBase * .55f; glow = edgeEmission * .3f;
            }
            Set(renderer, Color.Lerp(baseColor, edgeBase, back), Color.Lerp(glow, edgeEmission, back));
        }
        // 四隅: 「1」で白く光り、START で難易度の色
        Color cornerColor, cornerGlow;
        if (step >= 3) { cornerColor = accent; cornerGlow = accent * (GameStageSkin.GateCornerEmission * (1.2f + Pulse(startAge, .3))); }
        else if (step == 2)
        {
            float flash = Pulse(sinceFirstBeat - 2 * countSeconds, .25);
            cornerColor = Color.white; cornerGlow = Color.white * (GameStageSkin.GateCornerEmission * (1.1f + .9f * flash));
        }
        else { cornerColor = cornerBase * .6f; cornerGlow = cornerEmission * .4f; }
        foreach (var renderer in corners)
            if (renderer != null) Set(renderer, Color.Lerp(cornerColor, cornerBase, back), Color.Lerp(cornerGlow, cornerEmission, back));
        // 床の判定線: 左半分は「3」から青、右半分は「2」から赤。START の後 0.3 秒で白へ戻す。
        if (floor != null)
        {
            float fade = step >= 3 ? 1f - Mathf.Clamp01((float)(startAge / .3)) : 1f;
            floor.SetCountTint(UISkinPalette.LogoBlue, (step >= 0 ? 1f : 0f) * fade, UISkinPalette.LogoRed, (step >= 1 ? 1f : 0f) * fade);
        }
    }

    void Set(MeshRenderer renderer, Color baseColor, Color glow)
    {
        renderer.GetPropertyBlock(block);
        block.SetColor(BaseColorId, baseColor);
        block.SetColor(EmissionId, glow);
        renderer.SetPropertyBlock(block);
    }

    // 上書きを外して元の材質の見た目に戻す(何度呼んでもよい)。
    public void Restore()
    {
        foreach (var renderer in edges) if (renderer != null) renderer.SetPropertyBlock(null);
        foreach (var renderer in corners) if (renderer != null) renderer.SetPropertyBlock(null);
        if (floor != null) floor.ClearCountTint();
        Applied = false;
    }
}
