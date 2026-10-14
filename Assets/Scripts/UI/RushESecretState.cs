using System;
using UnityEngine;

// この選曲中だけの秘密。セーブデータや他の曲へ解除状態を持ち越さない。
public sealed class RushESecretState
{
    public const string SongId = "RushE";
    public const string Difficulty = "E";
    public const int RequiredHits = 10;
    public const float RevealSeconds = .82f;
    public const float TransitionSeconds = 1.85f;
    public static readonly Color Purple = new Color(.74f, .34f, 1f);
    public string SelectedSong { get; private set; }
    public int Hits { get; private set; }
    public bool Transitioning { get; private set; }
    public bool Unlocked { get; private set; }
    double lastHit = double.NegativeInfinity;
    public void Select(string song)
    {
        if (SelectedSong == song) return;
        SelectedSong = song; Reset();
    }
    public void Reset() { Hits = 0; Transitioning = Unlocked = false; lastHit = double.NegativeInfinity; }
    public bool TryHit(double now)
    {
        if (SelectedSong != SongId || Unlocked || Transitioning || double.IsNaN(now) || double.IsInfinity(now)
            || now - lastHit < .12) return false;
        lastHit = now; Hits++;
        if (Hits == RequiredHits) Transitioning = true;
        return true;
    }
    public bool Reveal()
    {
        if (!Transitioning || Unlocked) return false;
        Unlocked = true; return true;
    }
    public bool Finish()
    {
        if (!Transitioning || !Unlocked) return false;
        Transitioning = false; return true;
    }
}

// ジャケット半径を1とした座標。細かな手ぶれや滞在を数えず、横切る1振りだけを受け付ける。
public sealed class RushEJacketStroke
{
    Vector2 previous, entry;
    double previousTime;
    bool valid, crossing, spent;
    public void Reset() { valid = crossing = spent = false; }
    public bool Tick(Vector2 point, double now, bool available)
    {
        if (!available || float.IsNaN(point.x) || float.IsNaN(point.y) || float.IsInfinity(point.x) || float.IsInfinity(point.y)
            || double.IsNaN(now) || double.IsInfinity(now)) { Reset(); return false; }
        double dt = now - previousTime;
        if (!valid || dt <= 0 || dt > .18)
        { previous = entry = point; previousTime = now; valid = true; crossing = spent = false; return false; }
        Vector2 delta = point - previous;
        float along = delta.sqrMagnitude > .000001f ? Mathf.Clamp01(-Vector2.Dot(previous, delta) / delta.sqrMagnitude) : 0;
        bool intersects = (previous + delta * along).sqrMagnitude < .81f;
        if (!spent && intersects && !crossing) { crossing = true; entry = previous; }
        bool cut = !spent && crossing && intersects && (point - entry).magnitude >= 1.15f && delta.magnitude / dt >= 2.4;
        if (cut) spent = true;
        if (point.sqrMagnitude > 1.21f) { crossing = false; spent = false; }
        previous = point; previousTime = now;
        return cut;
    }
}
