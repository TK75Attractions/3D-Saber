using UnityEngine;

public partial class JudgmentSfx
{
    readonly AudioClip[] perfectVariations = new AudioClip[4];
    PerfectCutBag perfectBag;
    string perfectRun, perfectSong;

    void PreparePerfectBag()
    {
        FlourishSfx.PreparePerfectSource();
        for (int i = 0; i < perfectVariations.Length; i++)
            if (perfectVariations[i] == null &&
                FlourishSfx.TryGetCached(FlourishSfx.PerfectCutName(i), out var samples))
                perfectVariations[i] = ProceduralSfx.Clip(
                    FlourishSfx.PerfectCutName(i), samples, FlourishSfx.PerfectSampleRate);
    }

    AudioClip ChoosePerfectCut(JudgmentTier tier, AudioClip ordinary)
    {
        if (tier != JudgmentTier.Perfect || perfectClip != null || ordinary != defaultPerfectClip ||
            scoreManager != null && (scoreManager.LastCutWasGold || scoreManager.LastCutWasCross ||
                scoreManager.LastCutDirection != CutDirection.None || scoreManager.LastCutCount > 1))
            return ordinary;
        if (perfectBag == null || perfectRun != GameSession.AchievementRunId ||
            perfectSong != GameSession.SelectedSongId)
        {
            perfectRun = GameSession.AchievementRunId;
            perfectSong = GameSession.SelectedSongId;
            perfectBag = new PerfectCutBag(perfectSong, perfectRun);
        }
        int index = perfectBag.Next();
        return ProceduralSfx.Revive(ref perfectVariations[index]) ?? ordinary;
    }

    bool IsPerfectVariation(AudioClip clip)
    {
        foreach (var variation in perfectVariations)
            if (variation != null && variation == clip) return true;
        return false;
    }

    void ReleasePerfectBag()
    {
        for (int i = 0; i < perfectVariations.Length; i++)
        {
            UISkinKit.SafeDestroy(perfectVariations[i]);
            perfectVariations[i] = null;
        }
    }
}
