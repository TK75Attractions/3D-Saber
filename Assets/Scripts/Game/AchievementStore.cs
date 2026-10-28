using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

// 実績の解除の記録(2026-10-29)。実績は一覧では見せず、取った人に「〜をした ○人目」を出すだけ。
// ・PlayerPrefs の1キー(Achievements.Log.v1)に、解除の1件 (ns, runId, achId, station, at) を並べて JSON で残す。
// ・「○人目」= その (ns, achId) の解除の数。この台で取った分(Unlock・UnlockMany)と、相手の台のプレイの ach(RankingSync が Merge)を
//   足した、2台の通し番号。同じ (ns, runId, achId) は二重に足さない。名前空間("" ふだん / "fes" 運営モード)ごとに別に数える。
//   ある時刻から後の回の数は CountSince(一番乗りなど)。
// ・保存した記録に読めない所が1か所でもある(壊れた1件・重なった1件・全体が読めない・版が違う)ときは、その値を上書きせず、
//   この起動の間だけメモリで覚える(番号がずれないよう、番号は出さない: Unlock・Count は 0。読めた分は UnlockedIn に使う)。
//   上限は MaxEntries 件。PlayerPrefs はメインスレッドだけ。
public static class AchievementStore
{
    public const string PreferenceKey = "Achievements.Log.v1";
    public const int Version = 1;
    public const int MaxEntries = 50000;
    // 実績の ID の長さの上限(一覧で一番長い ID は hardsplus の 9 字)。48 個でも1回の同期の記録に入る長さ。
    public const int MaxIdLength = 12;

    [Serializable]
    public sealed class Entry
    {
        public string ns;       // 名前空間("" はふだん)
        public string run;      // その回の ID(GameSession.AchievementRunId)
        public string ach;      // 実績の ID
        public string st;       // 取った台(A / B。空でもよい)
        public long at = -1;    // 取った時刻(UTC の Unix ミリ秒)
    }

    [Serializable]
    sealed class Saved
    {
        public int v;
    }

    static string keyForTest;
    static bool loaded, available, dirty, fullWarned, saveWarned;
    static readonly List<string> entryJson = new List<string>();
    // (ns, runId, achId) → その解除の番号(その (ns, achId) の何件目か)。
    static readonly Dictionary<string, int> ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
    // (ns, achId) → その解除の1件ずつ(足した順。件数が「○人目」)。(ns, runId) → その回の実績(取った順)。
    static readonly Dictionary<string, List<Entry>> byAchievement = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
    static readonly Dictionary<string, List<string>> byRun = new Dictionary<string, List<string>>(StringComparer.Ordinal);

    public static string Key => keyForTest ?? PreferenceKey;
    // 保存した記録を読めたか(無いときも true)。false の間は保存しない。
    public static bool Available { get { EnsureLoaded(); return available; } }
    public static int EntryCount { get { EnsureLoaded(); return entryJson.Count; } }
    // Merge で足して、まだ PlayerPrefs へ書いていない分があるか(RankingSync が WriteToPrefs でまとめて書く)。
    public static bool NeedsWrite => loaded && dirty && available;
    // PlayerPrefs へ書いた回数(テスト用。まとめて解除したときに1回だけ書くことを確かめる)。
    public static int WriteCountForTest { get; private set; }

    // 解除する。返すのは「○人目」(足したあとの、その (ns, achId) の解除の数)。同じ (ns, runId, achId) なら足さずに、そのときの番号。
    // 記録できないとき(ID の形が違う・保存した記録が読めない・上限)は 0。atUnixMs が 0 以下なら今の時刻。すぐディスクまで保存する。
    public static int Unlock(string ns, string runId, string achId, string station, long atUnixMs) =>
        UnlockMany(ns, runId, new[] { achId }, station, atUnixMs)[0];

    // 1回の実績をまとめて解除する(曲の終わり)。返す番号は Unlock と同じ意味で、achIds と同じ順。保存は最後に1回だけ(足した分があるとき)。
    public static int[] UnlockMany(string ns, string runId, IReadOnlyList<string> achIds, string station, long atUnixMs)
    {
        var numbers = new int[achIds?.Count ?? 0];
        if (numbers.Length == 0) return numbers;
        EnsureLoaded();
        ns = ns ?? "";
        if (atUnixMs <= 0) atUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string st = PhoneSaberStation.Normalize(station);
        bool added = false;
        for (int i = 0; i < numbers.Length; i++)
        {
            var entry = new Entry { ns = ns, run = runId, ach = achIds[i], st = st, at = atUnixMs };
            if (!IsValid(entry)) continue;
            string key = EntryKey(ns, runId, entry.ach);
            if (!ordinals.ContainsKey(key))
            {
                if (!Add(entry)) continue;
                added = true;
            }
            numbers[i] = available ? ordinals[key] : 0;
        }
        if (added && available)
        {
            dirty = true;
            if (WriteToPrefs())
            {
                try { PlayerPrefs.Save(); }
                catch (Exception exception) { WarnSave(exception); }
            }
        }
        return numbers;
    }

    // その (ns, achId) の解除の数。保存した記録が読めないときは 0。
    public static int Count(string ns, string achId)
    {
        EnsureLoaded();
        return available && byAchievement.TryGetValue(CountKey(ns ?? "", achId), out var list) ? list.Count : 0;
    }

    // その (ns, achId) を sinceUnixMs 以後(この台は取った時刻、相手の台の回は遊び終わった時刻)に取った回の数。回の ID が同じなら1回。
    // この台で取った分・相手の台から足した分(まだ保存していない分も)に、同期のメモリの記録(RankingSync。まだ数に足していない回)も合わせる。
    // 保存した記録に読めない所があるときは -1(分からない)。形の違う ID は 0。メインスレッドで。
    public static int CountSince(string ns, string achId, long sinceUnixMs) => CountSince(ns, achId, sinceUnixMs, RankingSync.Instance);

    // 同期の記録を、渡した RankingSync のものにする(テスト用。null なら実績の記録だけ)。
    internal static int CountSince(string ns, string achId, long sinceUnixMs, RankingSync sync)
    {
        EnsureLoaded();
        if (!available) return -1;
        ns = ns ?? "";
        if (!IsValidId(achId)) return 0;
        var runs = new HashSet<string>(StringComparer.Ordinal);
        if (byAchievement.TryGetValue(CountKey(ns, achId), out var list))
            foreach (var entry in list)
                if (entry.at >= sinceUnixMs) runs.Add(entry.run);
        if (sync != null) sync.AddRunsWithAchievement(ns, achId, sinceUnixMs, runs);
        return runs.Count;
    }

    // その回に取った実績(取った順の写し)。
    public static IReadOnlyList<string> UnlockedIn(string ns, string runId)
    {
        EnsureLoaded();
        return runId != null && byRun.TryGetValue(RunKey(ns ?? "", runId), out var list) ? list.ToArray() : Array.Empty<string>();
    }

    // 相手の台で遊んだ回の実績を足す(数が増えるだけ。ポップアップは出さない)。形の違う ID は捨てる。
    // PlayerPrefs へはまだ書かない(RankingSync が WriteToPrefs でまとめて書き、PlayerPrefs.Save もまとめてする)。
    public static void Merge(string ns, string runId, IEnumerable<string> achIds, string station, long atUnixMs)
    {
        if (achIds == null) return;
        EnsureLoaded();
        foreach (string achId in achIds)
        {
            var entry = new Entry { ns = ns ?? "", run = runId, ach = achId, st = station ?? "", at = atUnixMs };
            if (!IsValid(entry) || ordinals.ContainsKey(EntryKey(entry.ns, runId, achId))) continue;
            if (Add(entry) && available) dirty = true;
        }
    }

    // まだ書いていない分を PlayerPrefs へ書く(ディスクへの保存 PlayerPrefs.Save は呼び出し側)。書けた・書く分が無いなら true。
    public static bool WriteToPrefs()
    {
        if (!NeedsWrite) return true;
        try
        {
            PlayerPrefs.SetString(Key, ToJson());
            dirty = false;
            WriteCountForTest++;
            return true;
        }
        catch (Exception exception)
        {
            WarnSave(exception);
            return false;
        }
    }

    // テスト用: 保存の鍵を差し替える(null で本番の鍵)。呼ぶたびにメモリの記録を捨て、次に使うときに読み直す。
    public static void UseKeyForTest(string key)
    {
        keyForTest = string.IsNullOrEmpty(key) || key == PreferenceKey ? null : key;
        Unload();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Unload()
    {
        loaded = available = dirty = fullWarned = saveWarned = false;
        entryJson.Clear();
        ordinals.Clear();
        byAchievement.Clear();
        byRun.Clear();
    }

    // 1件の形: 名前空間・回の ID・実績の ID・台名・時刻。
    public static bool IsValid(Entry entry) =>
        entry != null && OperatorMode.IsValidNamespace(entry.ns) && RankingSyncLog.IsValidId(entry.run) && IsValidId(entry.ach) &&
        PhoneSaberStation.Normalize(entry.st) == (entry.st ?? "") && entry.at > 0 && entry.at <= RankingSyncLog.MaxUnixMilliseconds;

    // 実績の ID: 1〜12 文字の英数字と _ と -。
    public static bool IsValidId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxIdLength) return false;
        foreach (char c in id)
            if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') && c != '_' && c != '-') return false;
        return true;
    }

    // 保存の形: {"v":1,"entries":[{"ns":…,"run":…,"ach":…,"st":…,"at":…}, …]}(1件ずつ読めるように RankingSyncLog.SplitArray で分ける)。
    // メモリの記録(読めない所がある間は、この起動で覚えた分も)をこの形にする(AchievementTracker の「一番乗り」も読む)。
    public static string ToJson()
    {
        EnsureLoaded();
        return "{\"v\":" + Version.ToString(CultureInfo.InvariantCulture) + ",\"entries\":[" + string.Join(",", entryJson) + "]}";
    }

    static string EntryKey(string ns, string runId, string achId) => ns + "\n" + runId + "\n" + achId;
    static string CountKey(string ns, string achId) => ns + "\n" + achId;
    static string RunKey(string ns, string runId) => ns + "\n" + runId;

    // 検査済みで、まだ無い1件を足す。上限なら足さない。
    static bool Add(Entry entry)
    {
        if (entryJson.Count >= MaxEntries)
        {
            if (!fullWarned) { fullWarned = true; Debug.LogWarning("AchievementStore: 実績の記録が " + MaxEntries + " 件に達したので、ここから先は残しません。"); }
            return false;
        }
        var copy = new Entry { ns = entry.ns ?? "", run = entry.run, ach = entry.ach, st = entry.st ?? "", at = entry.at };
        string countKey = CountKey(copy.ns, copy.ach);
        if (!byAchievement.TryGetValue(countKey, out var same)) byAchievement.Add(countKey, same = new List<Entry>());
        same.Add(copy);
        ordinals.Add(EntryKey(copy.ns, copy.run, copy.ach), same.Count);
        string runKey = RunKey(copy.ns, copy.run);
        if (!byRun.TryGetValue(runKey, out var list)) byRun.Add(runKey, list = new List<string>());
        list.Add(copy.ach);
        entryJson.Add(JsonUtility.ToJson(copy));
        return true;
    }

    // 読めない所があったときの警告(全体でも1件だけでも同じ)。
    public static string UnreadableWarning(string key) =>
        "AchievementStore: 実績の記録(" + key + ")に読めない所があるので、上書きせずに、この起動の間だけ覚えます(○人目は出しません)。";

    // 最初に使うときに1回だけ読む。読めない所が1か所でもあれば、上書きしない(available = false)。読めた分はメモリに入れる。
    static void EnsureLoaded()
    {
        if (loaded) return;
        Unload();
        loaded = true;
        available = true;
        string json = PlayerPrefs.GetString(Key, "");
        if (string.IsNullOrEmpty(json)) return;
        Saved saved = null;
        bool readable = RankingSyncLog.SplitArray(json, "entries", out string envelope, out var items);
        if (readable)
        {
            try { saved = JsonUtility.FromJson<Saved>(envelope); }
            catch (Exception) { saved = null; }
        }
        bool broken = saved == null || saved.v != Version;
        if (!broken)
            foreach (string item in items)
            {
                Entry entry = null;
                if (item != null)
                {
                    try { entry = JsonUtility.FromJson<Entry>(item); }
                    catch (Exception) { entry = null; }
                }
                if (!IsValid(entry) || ordinals.ContainsKey(EntryKey(entry.ns ?? "", entry.run, entry.ach)) || !Add(entry)) broken = true;
            }
        if (!broken) return;
        available = false;
        Debug.LogWarning(UnreadableWarning(Key));
    }

    static void WarnSave(Exception exception)
    {
        if (saveWarned) return;
        saveWarned = true;
        Debug.LogWarning("AchievementStore: 実績の記録を保存できませんでした(" + exception.GetType().Name + ")。");
    }
}
