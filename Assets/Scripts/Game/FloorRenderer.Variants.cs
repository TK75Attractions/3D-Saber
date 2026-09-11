using UnityEngine;

// 同じ金属デッキを共有しつつ、壁の構造・床の継ぎ目・負の空間を変えた3種。
public partial class FloorRenderer
{
    private void BuildVariantFloorBay(StageGeometry panels, StageGeometry inlays, float z, float length)
    {
        Quaternion flat = Quaternion.Euler(90f, 0f, 0f);
        float y = floorY - .018f;
        if (ActiveTheme == StageTheme.VioletVault)
        {
            // 幅広の六角風スラブ。肩の斜線が壁のV字フレームへ連続する。
            foreach (int side in new[] { -1, 1 })
            {
                panels.Panel(new Vector3(side * 2.98f, y, z + length * .5f),
                    new Vector3(5.87f, length - .13f, .10f), flat, .48f);
                int first = inlays.VertexCount;
                inlays.Beam(new Vector3(side * 4.15f, floorY + .04f, z + .2f),
                    new Vector3(side * 5.75f, floorY + .04f, z + length * .54f), .019f, .008f);
                inlays.TagMotionSince(first, new Vector3(side * 2.98f, y, z + length * .5f));
            }
        }
        else if (ActiveTheme == StageTheme.AmberFoundry)
        {
            // コンベアのような短い横スラブと、左右の埋め込み留め具。
            for (int section = 0; section < 2; section++)
            {
                float mid = z + length * (.25f + section * .5f);
                panels.Panel(new Vector3(0, y, mid), new Vector3(6.02f, length * .5f - .10f, .10f), flat, .08f);
                foreach (int side in new[] { -1, 1 })
                {
                    panels.Panel(new Vector3(side * 4.52f, y, mid), new Vector3(2.87f, length * .5f - .10f, .10f), flat, .15f);
                    inlays.Box(new Vector3(side * 5.60f, floorY + .04f, mid), new Vector3(.38f, .008f, .05f));
                }
            }
        }
        else
        {
            // 中央に広い静かな通路、端にずれた小型スラブ。壁の浮遊感を床にも反復。
            panels.Panel(new Vector3(0, y, z + length * .5f), new Vector3(5.86f, length - .12f, .10f), flat, .32f);
            foreach (int side in new[] { -1, 1 })
                for (int section = 0; section < 2; section++)
                {
                    float mid = z + length * (.25f + section * .5f);
                    panels.Panel(new Vector3(side * 4.51f, y, mid), new Vector3(2.90f, length * .5f - .14f, .10f), flat, .29f);
                    inlays.Box(new Vector3(side * 5.53f, floorY + .04f, mid), new Vector3(.020f, .008f, length * .20f));
                }
        }
    }

    private void BuildVariantWalls(float farZ, Material panelMat, Material ribMat, Material darkMat, Material lightMat, Material amberMat)
    {
        var panels = new StageGeometry(); var insets = new StageGeometry(); var ribs = new StageGeometry();
        var trims = new StageGeometry(); var tabs = new StageGeometry();
        float spacing = ActiveTheme == StageTheme.AmberFoundry ? 4.6f : 6.0f;
        for (float z = minZ; z < farZ - 1.2f; z += spacing)
        {
            float length = Mathf.Min(spacing, farZ - z);
            if (length < 1.2f) continue;
            float mid = z + length * .5f;
            foreach (int side in new[] { -1, 1 })
            {
                var inward = Quaternion.Euler(0, side * 90f, 0);
                if (ActiveTheme == StageTheme.VioletVault)
                {
                    // 壁面に対して斜めに浮いた菱形装甲と、連続するV字の支柱。
                    panels.Panel(new Vector3(side * 8.22f, floorY + 3.1f, mid),
                        new Vector3(length - .20f, 5.45f, .28f), inward, .50f);
                    insets.Panel(new Vector3(side * 7.95f, floorY + 3.0f, mid),
                        new Vector3(length * .74f, 3.70f, .18f), inward, .60f);
                    panels.Panel(new Vector3(side * 7.77f, floorY + 2.92f, mid),
                        new Vector3(length * .58f, 2.55f, .22f), inward * Quaternion.Euler(0, 0, 18f), .54f);
                    Vector3 foot = new Vector3(side * 6.85f, floorY + .15f, z + .16f);
                    Vector3 apex = new Vector3(side * 6.35f, floorY + 5.68f, mid);
                    Vector3 end = new Vector3(side * 6.85f, floorY + .15f, z + length - .16f);
                    ribs.Beam(foot, apex, .27f, .31f); ribs.Beam(apex, end, .27f, .31f);
                    trims.Beam(Vector3.Lerp(foot, apex, .19f) + Vector3.left * side * .16f,
                        Vector3.Lerp(foot, apex, .59f) + Vector3.left * side * .16f, .026f, .021f);
                    trims.Beam(Vector3.Lerp(apex, end, .16f) + Vector3.left * side * .16f,
                        Vector3.Lerp(apex, end, .33f) + Vector3.left * side * .16f, .026f, .021f);
                    ribs.Panel(new Vector3(side * 6.85f, floorY + .20f, z + .16f), new Vector3(.8f, .60f, .95f), Quaternion.identity, .16f);
                    tabs.Box(new Vector3(side * 7.62f, floorY + 2.8f, mid), new Vector3(.023f, .12f, .23f));
                }
                else if (ActiveTheme == StageTheme.AmberFoundry)
                {
                    // 暗い放熱ユニット。水平フィンと短い琥珀色の灯が、工業設備の縮尺を示す。
                    panels.Panel(new Vector3(side * 8.08f, floorY + 2.75f, mid),
                        new Vector3(length - .18f, 5.05f, .40f), inward, .22f);
                    insets.Panel(new Vector3(side * 7.70f, floorY + 2.68f, mid),
                        new Vector3(length - .78f, 3.3f, .25f), inward, .35f);
                    for (int fin = 0; fin < 7; fin++)
                    {
                        float h = floorY + 1.31f + fin * .43f;
                        ribs.Box(new Vector3(side * 7.39f, h, mid), new Vector3(.56f, .18f, length - 1.05f));
                        if (fin == 1 || fin == 5)
                            trims.Box(new Vector3(side * 7.095f, h - .068f, mid), new Vector3(.018f, .025f, length * .38f));
                    }
                    ribs.Panel(new Vector3(side * 6.94f, floorY + 2.65f, z + .15f),
                        new Vector3(.64f, 5.3f, .72f), Quaternion.identity, .16f);
                    ribs.Beam(new Vector3(side * 6.6f, floorY + .20f, z + .15f),
                        new Vector3(side * 6.94f, floorY + 2.0f, z + .15f), .36f, .75f);
                    trims.Box(new Vector3(side * 6.59f, floorY + 3.85f, z + .15f), new Vector3(.016f, .45f, .18f));
                    for (int tick = 0; tick < 3; tick++)
                        tabs.Box(new Vector3(side * 6.59f, floorY + .90f + tick * .18f, z + .15f), new Vector3(.018f, .06f, .23f));
                }
                else
                {
                    // 上下にずれた独立パネルと中空の支持フレーム。背後の暗い空間を広く残す。
                    for (int tier = 0; tier < 3; tier++)
                    {
                        float h = floorY + 1.0f + tier * 1.63f;
                        float depthShift = (tier % 2 == 0 ? -.38f : .38f);
                        var tilt = inward * Quaternion.Euler(0, 0, tier % 2 == 0 ? -12f : 12f);
                        insets.Panel(new Vector3(side * 8.14f, h, mid + depthShift),
                            new Vector3(length * .85f, 1.32f, .17f), tilt, .29f);
                        panels.Panel(new Vector3(side * (7.35f + tier * .14f), h, mid + depthShift),
                            new Vector3(length * .72f, 1.18f, .32f), tilt, .29f);
                        int first = trims.VertexCount;
                        trims.Beam(new Vector3(side * (7.15f + tier * .14f), h - .48f, mid - length * .2f + depthShift),
                            new Vector3(side * (7.15f + tier * .14f), h - .31f, mid + length * .08f + depthShift), .024f, .02f);
                        trims.TagMotionSince(first, new Vector3(side * (7.35f + tier * .14f), h, mid + depthShift));
                    }
                    ribs.Beam(new Vector3(side * 7.3f, floorY + .1f, z + .2f),
                        new Vector3(side * 6.40f, floorY + 5.9f, z + 1.00f), .18f, .24f);
                    ribs.Beam(new Vector3(side * 6.40f, floorY + 5.9f, z + 1.00f),
                        new Vector3(side * 7.6f, floorY + 5.55f, mid + 1.0f), .18f, .24f);
                    ribs.Panel(new Vector3(side * 6.77f, floorY + .25f, mid),
                        new Vector3(.64f, .50f, length * .55f), Quaternion.identity, .12f);
                    tabs.Box(new Vector3(side * 6.75f, floorY + .525f, mid), new Vector3(.13f, .016f, .30f));
                }
            }
        }
        Emit("WallPanels", panels, panelMat);
        Emit("WallRecesses", insets, darkMat);
        Emit("WallStructuralRibs", ribs, ribMat);
        Emit("WallLightInlays", trims, lightMat);
        Emit("WallServiceTabs", tabs, amberMat);
    }
}
