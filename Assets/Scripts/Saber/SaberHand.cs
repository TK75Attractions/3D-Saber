using UnityEngine;

// セーバーの「手」の識別。2本セーバープレイで、ノーツの色と切る手を対応付けるのに使う。
// Any はマウスフォールバックや色指定なしノーツ用(どちらの手でも可)。
public enum SaberHand
{
    Any = 0,
    Left = 1,
    Right = 2,
}

public static class SaberHandHelper
{
    // chart.json の color → 担当ハンド。青=左手 / 赤=右手(暫定マッピング、後で入れ替える可能性あり)。
    // gold と default(色指定なし)はどちらの手でも切れる。
    public static SaberHand FromColor(string color)
    {
        if (string.IsNullOrEmpty(color)) return SaberHand.Any;
        switch (color.ToLowerInvariant())
        {
            case "blue": return SaberHand.Left;
            case "red": return SaberHand.Right;
            default: return SaberHand.Any;
        }
    }

    // required(ノーツが要求する手)を cutter(切ろうとした手)が切れるか。
    // どちらかが Any なら常に可。
    public static bool CanCut(SaberHand required, SaberHand cutter)
    {
        return required == SaberHand.Any || cutter == SaberHand.Any || required == cutter;
    }

    public static SaberHand Other(SaberHand hand)
    {
        if (hand == SaberHand.Left) return SaberHand.Right;
        if (hand == SaberHand.Right) return SaberHand.Left;
        return SaberHand.Any;
    }

    // 手ごとのテーマ色。ノーツ本体・ブレード・トレイルで共通に使い、
    // 「この色はこの手」の対応を画面全体で一貫させる。
    public static Color HandColor(SaberHand hand)
    {
        if (hand == SaberHand.Left) return UISkinPalette.LogoBlue;
        if (hand == SaberHand.Right) return UISkinPalette.LogoRed;
        return UISkinPalette.Cyan;
    }
}
