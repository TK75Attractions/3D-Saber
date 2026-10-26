using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

// tips の読取・条件・抽選。シーンや UI を作らず、文字列と乱数だけでも検証できる。
public sealed class TransitionTips
{
    public const float MinimumHoldSeconds = .8f;
    public const float MaximumHoldSeconds = 1.6f;
    public const float BaseHoldSeconds = .35f;
    public const float SecondsPerDisplayCharacter = .05f;
    public const double RareWeight = .25;
    public const double SelectionHintMultiplier = 2;
    public const double SongPoolProbability = .5;
    // 同じ行は最近の8回(候補が少ないときは候補の数 − 1 回)のうちには出さない。1人の4回と次の人の4回で重ならない。
    // 「全部見るまで出さない」にすると [裏3]・[まれ] も一巡で必ず1回出て、長い目で見るとまれにならないため。
    public const int RecentWindow = 8;

    public enum Destination { Unknown, Title, SongSelect, Play, Practice, Calibration, Result }

    public readonly struct Context
    {
        public readonly Destination Target;
        public readonly string SongId;
        public readonly bool IsSecret;

        public Context(Destination target, string songId = null, bool isSecret = false)
        {
            Target = target;
            SongId = songId;
            IsSecret = isSecret;
        }

        public bool IsSongTransition => Target == Destination.Play || Target == Destination.Result;
    }

    public sealed class Entry
    {
        public string Text { get; }
        public IReadOnlyList<string> SongIds { get; }
        public IReadOnlyList<Destination> Destinations { get; }
        public int HintLevel { get; }
        public bool OnlySecret { get; }
        public bool Rare { get; }

        internal Entry(string text, List<string> songs, List<Destination> destinations,
            int hintLevel, bool onlySecret, bool rare)
        {
            Text = text;
            SongIds = songs.AsReadOnly();
            Destinations = destinations.AsReadOnly();
            HintLevel = hintLevel;
            OnlySecret = onlySecret;
            Rare = rare;
        }

        public bool Matches(Context context)
        {
            foreach (var destination in Destinations)
                if (destination != context.Target) return false;

            if (OnlySecret && (!context.IsSongTransition || !context.IsSecret)) return false;
            if (SongIds.Count > 0)
            {
                if (!context.IsSongTransition) return false;
                foreach (string song in SongIds)
                    if (!string.Equals(song, context.SongId, StringComparison.Ordinal)) return false;
                // 今まさに遊ぶ裏譜面の発見ヒントは、結果へ行くときも出さない。
                if (context.IsSecret && HintLevel > 0) return false;
            }
            return true;
        }

        public double Weight(Context context)
        {
            double weight = HintLevel == 3 ? RareWeight : 1;
            if (Rare) weight *= RareWeight;
            if (context.Target == Destination.SongSelect && SongIds.Count == 0 && HintLevel > 0)
                weight *= SelectionHintMultiplier;
            return weight;
        }
    }

    static TransitionTips shared;
    public static bool Enabled { get; set; } = true;
    public static TransitionTips Shared => shared ?? (shared = FromFile(
        Path.Combine(Application.streamingAssetsPath, "Tips", "tips_ja.txt")));

    public IReadOnlyList<Entry> Entries { get; }

    readonly System.Random random;
    // 出した行(新しい順)。
    readonly List<Entry> recent = new List<Entry>();

    TransitionTips(List<Entry> entries, System.Random random)
    {
        Entries = entries.AsReadOnly();
        this.random = random ?? new System.Random();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetShared()
    {
        shared = null;
        // Enabled はテスト全体の SetUpFixture でも設定するため、ここでは上書きしない。
    }

    public static TransitionTips FromFile(string path, System.Random random = null, Action<string> warning = null)
    {
        string source;
        try
        {
            source = File.ReadAllText(path, new UTF8Encoding(false, true));
        }
        catch (FileNotFoundException) { source = null; }
        catch (DirectoryNotFoundException) { source = null; }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException ||
            error is DecoderFallbackException || error is NotSupportedException ||
            error is System.Security.SecurityException)
        {
            Report(warning, "TransitionTips: tips を読み込めないため表示しません: " + error.Message);
            source = null;
        }
        return FromText(source, random, warning);
    }

    public static TransitionTips FromText(string source, System.Random random = null, Action<string> warning = null)
    {
        var entries = new List<Entry>();
        var warned = new HashSet<string>(StringComparer.Ordinal);
        using (var reader = new StringReader((source ?? "").TrimStart('\uFEFF')))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

                var songs = new List<string>();
                var destinations = new List<Destination>();
                int hintLevel = 0;
                bool onlySecret = false, rare = false;
                while (line.StartsWith("[", StringComparison.Ordinal))
                {
                    int end = line.IndexOf(']');
                    if (end < 0)
                    {
                        // 条件と本文の境界を決められない場合も、行そのものは失わない。
                        if (warned.Add("missing:" + line))
                            Report(warning, "TransitionTips: 閉じていない条件を本文として残します: " + line);
                        break;
                    }

                    string condition = line.Substring(1, end - 1).Trim();
                    if (!ReadCondition(condition, songs, destinations, ref hintLevel, ref onlySecret, ref rare)
                        && warned.Add(condition))
                        Report(warning, "TransitionTips: 読めない条件 [" + condition + "] を無視します。");
                    line = line.Substring(end + 1).TrimStart();
                }

                string body = line.Trim();
                if (body.Length > 0)
                    entries.Add(new Entry(body, songs, destinations, hintLevel, onlySecret, rare));
            }
        }
        return new TransitionTips(entries, random);
    }

    static void Report(Action<string> warning, string message)
    {
        if (warning != null) warning(message);
        else Debug.LogWarning(message);
    }

    static bool ReadCondition(string condition, List<string> songs, List<Destination> destinations,
        ref int hintLevel, ref bool onlySecret, ref bool rare)
    {
        switch (condition)
        {
            case "裏1": hintLevel = Math.Max(hintLevel, 1); return true;
            case "裏2": hintLevel = Math.Max(hintLevel, 2); return true;
            case "裏3": hintLevel = Math.Max(hintLevel, 3); return true;
            case "裏で": onlySecret = true; return true;
            case "まれ": rare = true; return true;
        }
        if (condition.StartsWith("曲:", StringComparison.Ordinal))
        {
            string song = condition.Substring(2).Trim();
            if (song.Length == 0) return false;
            songs.Add(song);
            return true;
        }
        if (condition.StartsWith("行き先:", StringComparison.Ordinal))
        {
            Destination destination;
            switch (condition.Substring(4).Trim())
            {
                case "タイトル": destination = Destination.Title; break;
                case "選曲": destination = Destination.SongSelect; break;
                case "プレイ": destination = Destination.Play; break;
                case "練習": destination = Destination.Practice; break;
                case "調整": destination = Destination.Calibration; break;
                case "結果": destination = Destination.Result; break;
                default: return false;
            }
            destinations.Add(destination);
            return true;
        }
        return false;
    }

    public static string SceneKey(string sceneName)
        => Path.GetFileNameWithoutExtension((sceneName ?? "").Replace('\\', '/'));

    public static Destination DestinationFor(string sceneName, bool tutorial, bool calibration)
    {
        switch (SceneKey(sceneName).ToLowerInvariant())
        {
            case "title": return Destination.Title;
            case "songselect": return Destination.SongSelect;
            case "result": return Destination.Result;
            case "game":
                if (calibration) return Destination.Calibration;
                return tutorial ? Destination.Practice : Destination.Play;
            default: return Destination.Unknown;
        }
    }

    public static float DisplayLength(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        float length = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            length += c <= '\u007F' || (c >= '\uFF61' && c <= '\uFF9F') ? .5f : 1f;
            // UTF-16 のサロゲートペアも、全角1文字として数える。
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
        }
        return length;
    }

    public static float HoldSeconds(string text)
        => Math.Max(MinimumHoldSeconds, Math.Min(MaximumHoldSeconds,
            BaseHoldSeconds + SecondsPerDisplayCharacter * DisplayLength(text)));

    public void ClearSeen() => recent.Clear();

    public Entry Select(Context context)
    {
        if (!Enabled) return null;
        var eligible = new List<Entry>();
        foreach (var entry in Entries)
            if (entry.Matches(context)) eligible.Add(entry);
        if (eligible.Count == 0) return null;

        // 候補のうち最近出した行を外す(別の画面や曲で出した行も数える)。候補は必ず1つ以上残る。
        int window = Math.Min(RecentWindow, eligible.Count - 1);
        var blocked = new HashSet<Entry>();
        for (int i = 0; i < recent.Count && blocked.Count < window; i++)
            if (eligible.Contains(recent[i])) blocked.Add(recent[i]);
        var available = new List<Entry>();
        foreach (var entry in eligible)
            if (!blocked.Contains(entry)) available.Add(entry);
        // 候補が直前の1件しかないときは、連続表示せず今回の tips を休む。
        if (available.Count == 1 && recent.Count > 0 && available[0] == recent[0]) return null;

        // 曲の前後は、その曲の行(裏譜面に入るときは [裏で] も)とその他を半々で選ぶ。
        var songs = new List<Entry>();
        var others = new List<Entry>();
        foreach (var entry in available)
        {
            if (context.IsSongTransition && (entry.SongIds.Count > 0 || entry.OnlySecret)) songs.Add(entry);
            else others.Add(entry);
        }

        List<Entry> pool = available;
        if (songs.Count > 0 && others.Count > 0)
            pool = random.NextDouble() < SongPoolProbability ? songs : others;

        double total = 0;
        foreach (var entry in pool) total += entry.Weight(context);
        double ticket = random.NextDouble() * total;
        Entry selected = pool[pool.Count - 1];
        foreach (var entry in pool)
        {
            ticket -= entry.Weight(context);
            if (ticket < 0) { selected = entry; break; }
        }

        recent.Insert(0, selected);
        if (recent.Count > RecentWindow * 4) recent.RemoveAt(recent.Count - 1);
        return selected;
    }
}
