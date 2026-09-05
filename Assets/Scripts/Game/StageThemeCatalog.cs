using UnityEngine;

public enum StageTheme
{
    ObsidianRelay = 0,
    VioletVault = 1,
    AmberFoundry = 2,
    AzurePrism = 3
}

// 背景だけの乱数系列。譜面・演出などが使う UnityEngine.Random の状態を変えない。
public static class StageThemeCatalog
{
    public const int Count = 4;
    private static readonly System.Random random = new System.Random();
    private static int previous = -1;

    public static StageTheme NextForPlay()
    {
        var next = Choose(random.Next(previous < 0 ? Count : Count - 1), previous);
        previous = (int)next;
        return next;
    }

    // 初回は全4種、2回目からは直前以外の3種を等確率に選ぶ。
    public static StageTheme Choose(int roll, int previousIndex)
    {
        bool skip = previousIndex >= 0 && previousIndex < Count;
        int count = skip ? Count - 1 : Count;
        int index = ((roll % count) + count) % count;
        if (skip && index >= previousIndex) index++;
        return (StageTheme)index;
    }

    public static string DisplayName(StageTheme theme)
    {
        switch (theme)
        {
            case StageTheme.VioletVault: return "Violet Vault";
            case StageTheme.AmberFoundry: return "Amber Foundry";
            case StageTheme.AzurePrism: return "Azure Prism";
            default: return "Obsidian Relay";
        }
    }

    // 彩度は端の埋め込み灯に限定。赤/青ノーツと混同しない低輝度の環境色。
    public static Color Accent(StageTheme theme)
    {
        switch (theme)
        {
            case StageTheme.VioletVault: return new Color(.43f, .29f, .66f);
            case StageTheme.AmberFoundry: return new Color(.65f, .36f, .12f);
            case StageTheme.AzurePrism: return new Color(.17f, .46f, .72f);
            default: return new Color(.19f, .53f, .61f);
        }
    }

    public static Color MetalTint(StageTheme theme)
    {
        switch (theme)
        {
            case StageTheme.VioletVault: return new Color(.074f, .057f, .100f);
            case StageTheme.AmberFoundry: return new Color(.083f, .066f, .052f);
            case StageTheme.AzurePrism: return new Color(.046f, .074f, .108f);
            default: return new Color(.057f, .080f, .104f);
        }
    }
}
