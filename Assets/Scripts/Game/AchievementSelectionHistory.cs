using System;

// 選曲機会ごとの試聴。音声カーソルが連続して進んだ秒だけを数える。
public sealed class AchievementSelectionHistory
{
    public int Opportunity { get; private set; }
    public string FirstSong { get; private set; }
    public bool HeardAnother { get; private set; }
    public bool Pending { get; private set; }
    string selected;
    bool manual, sampled, restoring;
    int generation;
    double cursor, continuous;

    public void Clear()
    {
        FirstSong = selected = null;
        HeardAnother = Pending = manual = sampled = restoring = false;
        continuous = 0;
    }

    public void Begin(bool calibrationReturn)
    {
        if (!calibrationReturn) { Clear(); Opportunity++; }
        restoring = calibrationReturn;
        sampled = false;
        continuous = 0;
        Pending = false;
    }

    public void Select(string song, bool byHand)
    {
        bool restored = restoring && !byHand && selected == song && manual;
        restoring = false;
        byHand |= restored;
        if (selected == song && manual == byHand) return;
        selected = song;
        manual = byHand;
        sampled = false;
        continuous = 0;
    }

    public void Sample(string song, int sourceGeneration, double position, bool audible)
    {
        if (!manual || song != selected || string.IsNullOrEmpty(song) || !audible
            || double.IsNaN(position) || double.IsInfinity(position))
        { sampled = false; continuous = 0; return; }
        if (!sampled || sourceGeneration != generation || position < cursor)
        { continuous = 0; sampled = true; }
        else if (position == cursor)
        {
            // 同じカーソルのコマは加算しない(音声バッファの更新待ち)。
        }
        else continuous += position - cursor;
        cursor = position;
        generation = sourceGeneration;
        if (continuous + 1e-9 < AchievementCatalog.BackpickPreviewSeconds) return;
        if (FirstSong == null) FirstSong = song;
        else if (song != FirstSong) HeardAnother = true;
    }

    public void Finish(string song, bool byHand)
    {
        Pending = byHand && manual && song == selected && song == FirstSong && HeardAnother;
        sampled = false;
        continuous = 0;
    }

    public bool Consume()
    {
        bool result = Pending;
        Clear();
        return result;
    }
}
