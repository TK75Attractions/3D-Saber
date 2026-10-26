using System.Collections.Generic;
using UnityEngine;

// 1人(1組)の曲数。設定だけ保存し、進行中の回数はタイトルで数え直す。
public static class VisitorPlays
{
    public const string PreferenceKey = "visitorSongsPerPlay_v1";
    static int? songsForTest;
    static readonly HashSet<string> completedRuns = new HashSet<string>();

    public static int SongsPerVisitor => songsForTest
        ?? ResolveSongsPerVisitor(PlayerPrefs.GetInt(PreferenceKey, 2));
    public static int CompletedSongs { get; private set; }
    public static bool HasNextSong => CompletedSongs < SongsPerVisitor;
    public static int CurrentSongNumber => Mathf.Min(CompletedSongs + 1, SongsPerVisitor);

    // 保存なし・壊れた値は、予定している2曲モードにそろえる。
    public static int ResolveSongsPerVisitor(int? saved) => saved == 1 ? 1 : 2;

    public static void ToggleSongsPerVisitor()
    {
        int next = SongsPerVisitor == 1 ? 2 : 1;
        if (songsForTest.HasValue) { songsForTest = next; return; }
        PlayerPrefs.SetInt(PreferenceKey, next);
        PlayerPrefs.Save();
    }

    // 差し替え中の切替も保存しない。null で実際の保存値へ戻す。
    public static void SetSongsPerVisitorForTest(int? songs)
    {
        songsForTest = songs.HasValue ? ResolveSongsPerVisitor(songs) : (int?)null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetSession() { BeginVisitor(); }

    public static void BeginVisitor()
    {
        CompletedSongs = 0;
        completedRuns.Clear();
    }

    // 結果の作り直しは同じ回。IDなしも、その来場者につき一度だけ数える。
    // 通常の曲開始は GameSession.ResetResult() が必ず新しいIDを作る。
    public static bool CompleteSong(string runId)
    {
        string id = string.IsNullOrWhiteSpace(runId) ? "" : runId;
        if (!completedRuns.Add(id)) return false;
        CompletedSongs++;
        return true;
    }
}
