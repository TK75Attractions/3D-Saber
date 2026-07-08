using UnityEngine;

// 2本セーバー構成を実行時に組み立てるユーティリティ。
// シーンには従来どおりセーバー1式(Saber: Bridge+Tracker+Judge)だけを置き、2本目はここで生成する(シーン非破壊)。
//   ・棒1(port 5005) = シーンのセーバー。手は stick1Hand(既定 Right=赤)。マウスフォールバックあり。
//   ・棒2(port 5006) = 生成する2本目。手は逆(既定 Left=青)。フォールバック無し(無音時は非表示)。
// ブレード/トレイルは手の色(青=左 / 赤=右)で塗り、ノーツの色と対応させる。
public static class SaberRig
{
    public const float TrailTime = 0.22f;

    // 1本目に手を割り当て、2本目のセーバーを生成して返す(冪等)。
    // stick1Hand: 棒1(シーンのセーバー)の手。棒2は自動的に逆の手になる。
    public static SaberCutJudge EnsureSecondSaber(SaberCutJudge primary, SaberHand stick1Hand)
    {
        if (primary == null) return null;

        var primaryBridge = primary.bladeProvider != null
            ? primary.bladeProvider
            : primary.GetComponent<SaberInputBridge>();

        // 1本目の設定(手+色+トレイル)
        primary.hand = stick1Hand;
        Color primaryColor = SaberHandHelper.HandColor(stick1Hand);
        if (primaryBridge != null)
        {
            primaryBridge.stickIndex = 1;
            primaryBridge.SetBladeColor(primaryColor);
            EnsureTrail(primaryBridge.transform, primaryColor, primaryBridge.bladeWidth);
        }

        // 2本目(既にあれば再利用)
        var existing = FindSecondJudge(primary);
        if (existing != null) return existing;

        SaberHand otherHand = SaberHandHelper.Other(stick1Hand);
        Color otherColor = SaberHandHelper.HandColor(otherHand);
        var go = new GameObject($"Saber2_{otherHand}");
        var bridge = go.AddComponent<SaberInputBridge>();
        var tracker = go.AddComponent<SaberTracker>();
        var judge = go.AddComponent<SaberCutJudge>();

        // Bridge: 棒2(port 5006)を読む。マウスフォールバックは1本目の役目
        // (両方がマウスに追従すると同じ場所に2本重なってしまうため)。
        bridge.stickIndex = 2;
        bridge.useInputPoint = true;
        bridge.fallbackToMouse = false;
        if (primaryBridge != null)
        {
            bridge.useBladeMode = primaryBridge.useBladeMode;
            bridge.pixelsToWorld = primaryBridge.pixelsToWorld;
            bridge.fixedZ = primaryBridge.fixedZ;
            bridge.clampToBounds = primaryBridge.clampToBounds;
            bridge.minBounds = primaryBridge.minBounds;
            bridge.maxBounds = primaryBridge.maxBounds;
            bridge.inputPointStaleSeconds = primaryBridge.inputPointStaleSeconds;
            bridge.bladeWidth = primaryBridge.bladeWidth;
            bridge.enableSmoothing = primaryBridge.enableSmoothing;
            bridge.smoothingTau = primaryBridge.smoothingTau;
        }
        bridge.SetBladeColor(otherColor);

        // Judge: 1本目と同じ判定パラメータで、手だけ逆。
        judge.saber = tracker;
        judge.bladeProvider = bridge;
        judge.hand = otherHand;
        judge.autonomous = primary.autonomous;
        CopyTuning(primary, judge);

        EnsureTrail(go.transform, otherColor, bridge.bladeWidth);
        return judge;
    }

    // 判定チューニング(半径・速度閾値)を複製する。キャリブレーション時の緩和にも使う。
    public static void CopyTuning(SaberCutJudge from, SaberCutJudge to)
    {
        if (from == null || to == null) return;
        to.bladeRadius = from.bladeRadius;
        to.minCutSpeed = from.minCutSpeed;
        to.maxCutDistance = from.maxCutDistance;
        to.noteHitRadiusXY = from.noteHitRadiusXY;
        to.imuHintMaxAgeSeconds = from.imuHintMaxAgeSeconds;
    }

    private static SaberCutJudge FindSecondJudge(SaberCutJudge primary)
    {
        foreach (var j in Object.FindObjectsByType<SaberCutJudge>(FindObjectsSortMode.None))
        {
            if (j != primary) return j;
        }
        return null;
    }

    // スイングの軌跡(トレイル)。手の色で発光し、2本の見分けと剣戟の質感を担う(冪等)。
    public static TrailRenderer EnsureTrail(Transform saber, Color color, float bladeWidth)
    {
        if (saber == null) return null;
        var trail = saber.GetComponent<TrailRenderer>();
        if (trail == null) trail = saber.gameObject.AddComponent<TrailRenderer>();
        trail.time = TrailTime;
        trail.startWidth = Mathf.Max(0.06f, bladeWidth * 1.6f);
        trail.endWidth = 0f;
        trail.numCapVertices = 4;
        trail.minVertexDistance = 0.02f;
        if (trail.sharedMaterial == null)
        {
            var sh = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            var m = new Material(sh);
            // 加算ブレンドで「光の残像」に見せる
            if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);
            if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
            if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 0f);
            m.renderQueue = 3050;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Color.white);
            else if (m.HasProperty("_Color")) m.color = Color.white;
            trail.sharedMaterial = m;
        }
        Color c0 = color; c0.a = 0.65f;
        Color c1 = color; c1.a = 0f;
        trail.startColor = c0;
        trail.endColor = c1;
        return trail;
    }
}
