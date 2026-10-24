using UnityEngine;

// 同じ難易度の再通知・曲送りでは線幅を弾ませない。
internal sealed class SongSelectRimPulse
{
    bool initialized, secret;
    int song, difficulty;
    double changedAt = double.NegativeInfinity;

    internal void Select(int songIndex, int selected, bool isSecret, double now)
    {
        if (!initialized || song != songIndex) changedAt = double.NegativeInfinity;
        else if (difficulty != selected || secret != isSecret) changedAt = now;
        song = songIndex;
        difficulty = selected;
        secret = isSecret;
        initialized = true;
    }

    internal float WidthAt(double now)
    {
        double age = now - changedAt;
        if (age < 0 || age >= .22) return 3;
        return age < .07 ? Mathf.Lerp(3, 7, (float)(age / .07))
            : Mathf.Lerp(7, 3, (float)((age - .07) / .15));
    }

    // 解除している隠し譜面があればその色(E は紫、G は金)。
    internal static Color ColorFor(int difficulty, SecretChart secret) =>
        secret != null ? secret.Accent : SongSelectSkin.DifficultyColor(difficulty);
}
