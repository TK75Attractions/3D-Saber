using UnityEngine;

// 投影の見た目だけを先へ出す追加分。測定の合計・判定の実効補正には加えない。
public static class PhoneSaberVisualLeadSettings
{
    public const int MaximumExtraMilliseconds = 80;
    public const int MaximumTotalMilliseconds = 200;
    public const int StepMilliseconds = 10;

    public static string Key(string station) =>
        "PhoneSaber.VisualLead." + PhoneSaberStation.Normalize(station) + ".ExtraMs.v1";

    public static int Clamp(int ms) =>
        ((Mathf.Clamp(ms, 0, MaximumExtraMilliseconds) + StepMilliseconds / 2) / StepMilliseconds) * StepMilliseconds;

    public static int Load(string station) => Clamp(PlayerPrefs.GetInt(Key(station), 0));

    public static void Save(string station, int ms)
    {
        ms = Clamp(ms);
        if (Load(station) == ms) return;
        PlayerPrefs.SetInt(Key(station), ms);
        PlayerPrefs.Save();
        PhoneSaberEventLog.Record(station, "SYSTEM", "visual-lead-setting", "ms=" + ms);
    }

    // 手動 OFF・自動の欠測 fallback は先読み 0 のまま。追加分だけで予測を始めない。
    public static int TotalLead(int baseMs, int extraMs) => baseMs <= 0 ? 0 :
        (int)System.Math.Min(MaximumTotalMilliseconds, (long)baseMs + Clamp(extraMs));
}
