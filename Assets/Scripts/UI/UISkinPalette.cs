using UnityEngine;

// ゲーム全体で共有する配色。
public static class UISkinPalette
{
    public static readonly Color Cyan = new Color(0.27f, 1f, 0.97f);
    public static readonly Color Magenta = new Color(1f, 0.30f, 0.72f);
    public static readonly Color Yellow = new Color(1f, 0.92f, 0.35f);
    public static readonly Color Orange = new Color(1f, 0.60f, 0.25f);
    public static readonly Color OffWhite = new Color(0.91f, 0.93f, 1f);
    public static readonly Color SubtleGray = new Color(0.55f, 0.60f, 0.75f);

    // ノーツ種別カラー
    public static readonly Color NoteFlick = new Color(0.75f, 0.40f, 1f);     // バイオレット
    public static readonly Color NoteLong = new Color(0.30f, 1f, 0.65f);      // ティール / アクアグリーン
    public static readonly Color NoteGold = new Color(1f, 0.85f, 0.25f);      // 金

    public static readonly Color BgTop = new Color(0.03f, 0.04f, 0.10f, 1f);
    public static readonly Color BgBottom = new Color(0.10f, 0.05f, 0.24f, 1f);
}
