using System;
using System.Collections.Generic;
using UnityEngine;

public enum StageTheme
{
    ObsidianRelay = 0,
    VioletVault = 1,
    AmberFoundry = 2,
    AzurePrism = 3,
    AbyssalRuins = 4,
    SkySanctuary = 5,
    MoonlitGarden = 6,
    CrystalGrotto = 7,
    AstralOrbit = 8,
    DesertSanctum = 9,
    PulseArray = 10,
    // 削除済みの写真背景ID 11〜13は再利用しない。既存背景の保存値を維持する。
    MidnightHarbor = 14,
    VerdantAtrium = 15,
    ClockworkLibrary = 16,
    PracticeCourtyard = 17
}

// 曲フォルダの stage.json で固定する。難易度・再プレイ・選曲順で背景を変えない。
public static class StageThemeCatalog
{
    public static readonly IReadOnlyList<StageTheme> All = Array.AsReadOnly((StageTheme[])Enum.GetValues(typeof(StageTheme)));
    public static int Count => All.Count;
    public static bool IsDefined(StageTheme theme) => Enum.IsDefined(typeof(StageTheme), theme);

    public static StageTheme ForSong(string songId)
    {
        // Game シーン直接起動の既定曲と揃える。未設定の追加曲も抽選せず安全な背景で開く。
        string id = string.IsNullOrEmpty(songId) ? "ElDorado" : songId;
        string name = StagePerformanceTimeline.Load(id).stageTheme;
        return Enum.TryParse(name, true, out StageTheme theme) && IsDefined(theme)
            ? theme : StageTheme.ObsidianRelay;
    }

    public static string DisplayName(StageTheme theme)
    {
        switch (theme)
        {
            case StageTheme.VioletVault: return "Violet Vault";
            case StageTheme.AmberFoundry: return "Amber Foundry";
            case StageTheme.AzurePrism: return "Azure Prism";
            case StageTheme.AbyssalRuins: return "Abyssal Ruins";
            case StageTheme.SkySanctuary: return "Sky Sanctuary";
            case StageTheme.MoonlitGarden: return "Moonlit Garden";
            case StageTheme.CrystalGrotto: return "Crystal Grotto";
            case StageTheme.AstralOrbit: return "Astral Orbit";
            case StageTheme.DesertSanctum: return "Desert Sanctum";
            case StageTheme.PulseArray: return "Pulse Array";
            case StageTheme.MidnightHarbor: return "Midnight Harbor";
            case StageTheme.VerdantAtrium: return "Verdant Atrium";
            case StageTheme.ClockworkLibrary: return "Clockwork Library";
            case StageTheme.PracticeCourtyard: return "Practice Courtyard";
            default: return "Obsidian Relay";
        }
    }

    public static bool IsScenic(StageTheme theme) =>
        (theme >= StageTheme.AbyssalRuins && theme <= StageTheme.DesertSanctum) ||
        theme == StageTheme.MidnightHarbor || theme == StageTheme.VerdantAtrium || theme == StageTheme.ClockworkLibrary ||
        theme == StageTheme.PracticeCourtyard;

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
