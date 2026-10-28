using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

// 台同士のランキング同期(RankingSync、2026-10-28)の、ネットワークを使わない部分。テストの中心。
// ・遊び終わった1回(RankingPlay)を、PlayerPrefs の1キー(RankingSync.Log.v1)に JSON で残す。壊れた値は捨てる(偽の記録を作らない)。
// ・併合は id(= GameSession.AchievementRunId)が無いものだけ足す。同じ回は何度届いても1回。同じ id で中身が違えば先のものを残す。
// ・要約(digest)は件数と、id の 64bit ハッシュ(FNV-1a)の XOR。足した順に依らないので、同じ記録を持つ2台は同じ要約になる。
// ・送る形は "BTSRANK2 " + JSON。種類は plays(プレイの束)と digest(件数と要約、最近聞こえた相手)。1データグラムは 1200 バイト以下に分ける。
// ・版 2(2026-10-29 運営モードと実績): 1回の記録に ns(名前空間。"" はふだん、"fes" は運営モードの表)と ach(その回の実績の ID)。
//   ほかの版(BTSRANK1 など)のメッセージは合流しない(RankingSync が運営に1回知らせる)。保存の形は版 1(ns・ach なし = ふだん)も読む。

// 表(通算の順位・上位5件)へ足した結果。失敗は、ログに入れずに適用待ちに残して、あとで足し直す。
public enum RankingMergeResult { Added, AlreadyThere, OutsideTop, Failed }

[Serializable]
public sealed class RankingPlay
{
    // 数の欄は初期値を -1 にする。JSON に欄が無ければ -1 のままで検査に落ちる(0点・精度0の偽の記録を作らない)。
    public string id;        // その回の ID(GameSession.AchievementRunId)
    public string song;      // 曲 ID
    public string diff;      // 難易度(表は小文字にそろえて引く)
    public int score = -1;   // 点(0〜100000)
    public string rank;      // ランクの表示ラベル("S+" など)
    public float acc = -1f;  // 精度(0〜1)
    public string date;      // 遊んだ日 "yyyy/MM/dd"(HI-SCORE の表示)
    public string st;        // 遊んだ台の名前(A / B。空でもよい)
    public long at = -1;     // 遊び終わった時刻(UTC の Unix ミリ秒。上位5件の同点の並びにも使う)
    // 版 2 から。欄が無ければ(版 1 の保存)ふだんの表・実績なし。
    public string ns;        // 名前空間("" はふだん、"fes" は運営モードの表)。合流はこの名前空間の表へ
    public List<string> ach = new List<string>();   // その回に取った実績の ID(相手の台は AchievementStore.Merge で数に足す)
}

// 受け取った1通。v は初期値を持たせず、count は -1(欄の無い JSON を正しい1通と取り違えない)。
[Serializable]
public sealed class RankingSyncMessage
{
    public int v;
    public string kind;       // "plays" / "digest"
    public string from;       // 差出人の機械 ID
    public string st;         // 差出人の台名
    public int count = -1;    // 差出人の記録の件数
    public string hash;       // 差出人の要約(16 桁の16進)
    public List<string> heard = new List<string>();   // 要約だけ: 差出人が最近(60秒以内に)聞こえた相手の機械 ID
    public List<RankingPlay> plays = new List<RankingPlay>();
}

// 送る1通と、その中のプレイの数(送った件数を数えるため)。
public readonly struct RankingSyncDatagram
{
    public readonly string Text;
    public readonly int Plays;
    public RankingSyncDatagram(string text, int plays) { Text = text; Plays = plays; }
}

public sealed class RankingSyncLog
{
    public const string PreferenceKey = "RankingSync.Log.v1";
    // 通信の版(2026-10-29 に 2: ns・ach を足した)。目印の数字と JSON の v。ほかの版のメッセージは合流しない。
    public const string PrefixStem = "BTSRANK";
    public const string Prefix = "BTSRANK2 ";
    public const int Version = 2;
    // 保存(ログ・足し直し待ち)の形の版。書くのは 2、読むのは 1(ns・ach なし = ふだんの表)と 2。
    public const int SavedVersion = 2;
    public const int OldestSavedVersion = 1;
    public const string PlaysKind = "plays";
    public const string DigestKind = "digest";
    public const int MaxDatagramBytes = 1200;
    public const int MaxReceiveBytes = 1400;
    // ログの上限(全曲・起動をまたいだ合計)。文化祭の2日で数百回の見込み。これを超えた回はこの台の表にだけ残る(同期しない)。
    public const int MaxPlays = 20000;
    // これを超えたら運営に1回知らせる。
    public const int WarnPlays = 18000;
    // 要約に入れる「最近聞こえた相手」の数の上限(1200 バイトに収める)。
    public const int MaxHeard = 8;
    public const int MinIdLength = 8, MaxIdLength = 64, MaxSongLength = 64, MaxDifficultyLength = 16, MaxRankLength = 8;
    public const int MaxStationLength = 16;
    // 1回の記録に入れる実績の数の上限(実績は全部で44個)。12 字の ID が48個でも、ふつうの回(32 字の回の ID・曲 ID 15 字ほど・
    // 台名 16 字まで)は1通に入る(1回分が束に入る大きさ MaxPlayBytes は下で確かめる)。
    public const int MaxAchievementsPerPlay = 48;
    public const long MaxUnixMilliseconds = 32503680000000; // 3000-01-01(この台の時刻へ直しても桁があふれない範囲)

    static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
    // 受け取ったバイト列は厳しく読む(壊れた UTF-8 を置き換え文字で通さない)。
    static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
    // 1回分の JSON の大きさの上限: 1200 バイトから、一番大きな束の頭(64 文字の差出人・16 文字の台名・件数の上限)と閉じの "]}" を除いた残り。
    // これに収まる回だけを正しい形とする(どの束にも入り、送れない回を作らない)。
    public static readonly int MaxPlayBytes = MaxDatagramBytes -
        (Utf8.GetByteCount(Head(PlaysKind, new string('f', MaxIdLength), new string('S', MaxStationLength), MaxPlays, HashToText(ulong.MaxValue), null)) + 2);
    // 1回分の JSON の、文字列の中身を除いた部分の大きさ(欄の名前・引用符・区切り・数の欄の一番長い形。精度は 24 文字まで見込む)。
    const int FixedPlayBytes = 140;

    // 保存した JSON の外側(プレイは SplitPlays で1回ずつ読む)。
    [Serializable]
    sealed class Saved
    {
        public int v;
    }

    // 足した順(減らさない)。送る束と保存の JSON は、足したときに1回だけ作った各プレイの JSON とバイト数をつなげる。
    readonly List<RankingPlay> plays = new List<RankingPlay>();
    readonly List<string> playJson = new List<string>();
    readonly List<int> playBytes = new List<int>();
    readonly Dictionary<string, int> positions = new Dictionary<string, int>(StringComparer.Ordinal);
    ulong hash;

    public int Count => plays.Count;
    public ulong Hash => hash;
    public string HashText => HashToText(hash);
    public bool IsFull => plays.Count >= MaxPlays;
    // 読むだけ(中身を書き換えない)。
    public IReadOnlyList<RankingPlay> Plays => plays;
    // 外へ渡す、足した順の写し(書き換えてもこのログは変わらない。一覧も書き換えられない)。
    public IReadOnlyList<RankingPlay> CopyPlays()
    {
        var copies = new RankingPlay[plays.Count];
        for (int i = 0; i < copies.Length; i++) copies[i] = Copy(plays[i]);
        return Array.AsReadOnly(copies);
    }
    public bool Contains(string id) => id != null && positions.ContainsKey(id);
    public RankingPlay Find(string id) => id != null && positions.TryGetValue(id, out int at) ? plays[at] : null;
    public bool SameDigest(int count, string hashText) => count == Count && string.Equals(hashText, HashText, StringComparison.Ordinal);

    // 正しい形で、まだ無い id なら写しを足す。足したら true。
    public bool Add(RankingPlay play)
    {
        if (!IsValid(play) || positions.ContainsKey(play.id) || IsFull) return false;
        var copy = Copy(play);
        string json = JsonUtility.ToJson(copy);
        positions.Add(copy.id, plays.Count);
        plays.Add(copy);
        playJson.Add(json);
        playBytes.Add(Utf8.GetByteCount(json));
        hash ^= IdHash(copy.id);
        return true;
    }

    // 届いた束を足す。新しく足した回(写し)を返す。壊れた回・同じ id の回は捨てる。
    public List<RankingPlay> Merge(IEnumerable<RankingPlay> incoming)
    {
        var added = new List<RankingPlay>();
        if (incoming == null) return added;
        foreach (var play in incoming)
            if (Add(play)) added.Add(plays[plays.Count - 1]);
        return added;
    }

    public string ToJson() => "{\"v\":" + SavedVersion.ToString(CultureInfo.InvariantCulture) + ",\"plays\":[" + string.Join(",", playJson) + "]}";

    // 保存した JSON を読む。全体が読めない・版が違うときは空のログ、一部の回だけ壊れていればその回だけ捨てる(broken = true)。
    // 版 1 の保存(ns・ach の欄なし)は、ふだんの表・実績なしの回として読む。
    public static RankingSyncLog FromJson(string json, out bool broken)
    {
        broken = false;
        var log = new RankingSyncLog();
        if (string.IsNullOrEmpty(json)) return log;
        Saved saved;
        if (!SplitPlays(json, out string envelope, out var items)) { broken = true; return log; }
        try { saved = JsonUtility.FromJson<Saved>(envelope); }
        catch (Exception) { broken = true; return log; }
        if (saved == null || saved.v < OldestSavedVersion || saved.v > SavedVersion) { broken = true; return log; }
        foreach (string item in items)
            if (!log.Add(ParsePlay(item))) broken = true;
        return log;
    }

    // JSON の外側の1段を、空白や欄の順に依らずに読み、"plays" の配列をプレイごとの JSON に分ける。envelope は plays を [] にした全体。
    // プレイは1回ずつ読む(JsonUtility.FromJson<RankingPlay> は欄の初期値 -1 から読むので、欠けた数の欄を確かに見分けられ、
    // 形の壊れた1回だけを捨てられる)。配列の要素がオブジェクトでなければ、その要素は null(壊れた1回)。
    // plays が無い・配列でない・2つある・括弧や文字列の形が壊れている・後ろに余計なものがあれば false(全体が壊れている)。
    static bool SplitPlays(string json, out string envelope, out List<string> items) => SplitArray(json, "plays", out envelope, out items);

    // SplitPlays と同じ読み方で、arrayKey の配列を分ける(実績の記録 AchievementStore の "entries" にも使う)。
    internal static bool SplitArray(string json, string arrayKey, out string envelope, out List<string> items)
    {
        envelope = null;
        items = null;
        if (json == null) return false;
        var found = new List<string>();
        int arrayStart = -1, arrayEnd = -1;
        int i = 0;
        SkipSpace(json, ref i);
        if (i >= json.Length || json[i] != '{') return false;
        i++;
        SkipSpace(json, ref i);
        if (i < json.Length && json[i] == '}') return false; // plays が無い
        while (true)
        {
            SkipSpace(json, ref i);
            if (!ReadString(json, ref i, out string key)) return false;
            SkipSpace(json, ref i);
            if (i >= json.Length || json[i] != ':') return false;
            i++;
            SkipSpace(json, ref i);
            if (key == arrayKey)
            {
                if (arrayStart >= 0 || i >= json.Length || json[i] != '[') return false;
                arrayStart = i;
                i++;
                SkipSpace(json, ref i);
                if (i < json.Length && json[i] == ']') i++;
                else
                {
                    while (true)
                    {
                        SkipSpace(json, ref i);
                        int start = i;
                        bool isObject = i < json.Length && json[i] == '{';
                        if (!SkipValue(json, ref i)) return false;
                        found.Add(isObject ? json.Substring(start, i - start) : null);
                        SkipSpace(json, ref i);
                        if (i < json.Length && json[i] == ',') { i++; continue; }
                        if (i < json.Length && json[i] == ']') { i++; break; }
                        return false;
                    }
                }
                arrayEnd = i;
            }
            else if (!SkipValue(json, ref i)) return false;
            SkipSpace(json, ref i);
            if (i < json.Length && json[i] == ',') { i++; continue; }
            if (i < json.Length && json[i] == '}') { i++; break; }
            return false;
        }
        SkipSpace(json, ref i);
        if (i != json.Length || arrayStart < 0) return false;
        envelope = json.Substring(0, arrayStart) + "[]" + json.Substring(arrayEnd);
        items = found;
        return true;
    }

    static void SkipSpace(string s, ref int i)
    {
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
    }

    // 文字列を1つ読む(エスケープを解く)。i は閉じの " の次へ進む。
    static bool ReadString(string s, ref int i, out string value)
    {
        value = null;
        if (i >= s.Length || s[i] != '"') return false;
        var text = new StringBuilder();
        for (i++; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"') { i++; value = text.ToString(); return true; }
            if (c < ' ') return false;
            if (c != '\\') { text.Append(c); continue; }
            if (++i >= s.Length) return false;
            switch (s[i])
            {
                case '"': text.Append('"'); break;
                case '\\': text.Append('\\'); break;
                case '/': text.Append('/'); break;
                case 'b': text.Append('\b'); break;
                case 'f': text.Append('\f'); break;
                case 'n': text.Append('\n'); break;
                case 'r': text.Append('\r'); break;
                case 't': text.Append('\t'); break;
                case 'u':
                    if (i + 4 >= s.Length || !int.TryParse(s.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                        return false;
                    text.Append((char)code);
                    i += 4;
                    break;
                default: return false;
            }
        }
        return false;
    }

    // 値を1つ読み飛ばす(文字列・数・true / false / null・オブジェクト・配列)。中身の形は JsonUtility が確かめる。
    static bool SkipValue(string s, ref int i)
    {
        if (i >= s.Length) return false;
        char c = s[i];
        if (c == '"') return ReadString(s, ref i, out _);
        if (c == '{' || c == '[')
        {
            int depth = 0;
            while (i < s.Length)
            {
                char d = s[i];
                if (d == '"')
                {
                    if (!ReadString(s, ref i, out _)) return false;
                    continue;
                }
                if (d == '{' || d == '[') depth++;
                else if (d == '}' || d == ']')
                {
                    if (--depth == 0) { i++; return true; }
                }
                i++;
            }
            return false;
        }
        int start = i;
        while (i < s.Length && s[i] != ',' && s[i] != '}' && s[i] != ']' && s[i] != ' ' && s[i] != '\t' && s[i] != '\n' && s[i] != '\r') i++;
        return i > start;
    }

    // プレイの一覧をログと同じ形の JSON にする(足し直し待ちの回を、終了しても失われないように別のキーへ残す)。
    public static string PlaysJson(IEnumerable<RankingPlay> items)
    {
        var parts = new List<string>();
        foreach (var play in items)
            if (IsValid(play)) parts.Add(JsonUtility.ToJson(Copy(play)));
        return "{\"v\":" + SavedVersion.ToString(CultureInfo.InvariantCulture) + ",\"plays\":[" + string.Join(",", parts) + "]}";
    }

    // 1回分の JSON を読む。オブジェクトでない・読めなければ null(検査で落ちる)。
    static RankingPlay ParsePlay(string json)
    {
        if (json == null) return null;
        try { return JsonUtility.FromJson<RankingPlay>(json); }
        catch (Exception) { return null; }
    }

    public static RankingSyncLog Load(string key)
    {
        var log = FromJson(PlayerPrefs.GetString(key, ""), out bool broken);
        if (broken) Debug.LogWarning("RankingSync: 同期の記録(" + key + ")に読めない所があったので、その分を捨てました。");
        return log;
    }

    // PlayerPrefs へ書くだけ。ディスクへの保存(PlayerPrefs.Save)は呼び出し側がまとめてする。失敗は例外のまま返す。
    public void Save(string key) => PlayerPrefs.SetString(key, ToJson());

    // ---- 送る形 ----
    // from・station・hash・heard は決まった文字だけ(機械 ID は IsValidId、台名は PhoneSaberStation.Normalize)なので、そのまま埋める。

    static string Head(string kind, string from, string station, int count, string hashText, IEnumerable<string> heard)
    {
        var text = new StringBuilder(Prefix);
        text.Append("{\"v\":").Append(Version.ToString(CultureInfo.InvariantCulture))
            .Append(",\"kind\":\"").Append(kind).Append("\",\"from\":\"").Append(from)
            .Append("\",\"st\":\"").Append(station ?? "").Append("\",\"count\":").Append(count.ToString(CultureInfo.InvariantCulture))
            .Append(",\"hash\":\"").Append(hashText).Append('"');
        if (heard != null)
        {
            text.Append(",\"heard\":[");
            int written = 0;
            foreach (string id in heard)
            {
                if (!IsValidId(id) || written >= MaxHeard) continue;
                if (written++ > 0) text.Append(',');
                text.Append('"').Append(id).Append('"');
            }
            text.Append(']');
        }
        return text.Append(",\"plays\":[").ToString();
    }

    // 要約。heard には、この台が最近(60秒以内に)聞こえた相手の機械 ID を入れる(相手が「こちらに届いている」と分かるように)。
    public static string BuildDigest(string from, string station, int count, string hashText, IEnumerable<string> heard = null) =>
        Head(DigestKind, from, station, count, hashText, heard ?? Array.Empty<string>()) + "]}";

    public string DigestMessage(string from, string station, IEnumerable<string> heard = null) => BuildDigest(from, station, Count, HashText, heard);

    // start 番目から後ろの回を、1200 バイト以下の束に分ける(自分の台の1回だけを送るときは Count - 1 から)。
    public List<RankingSyncDatagram> Bundles(string from, string station, int start = 0) =>
        Bundles(from, station, start, int.MaxValue, out _);

    // start 番目から、束を maxDatagrams 通まで作る(全部の記録を送るとき、1コマで全部を作らないため)。next は次に送る回の番号。
    public List<RankingSyncDatagram> Bundles(string from, string station, int start, int maxDatagrams, out int next)
    {
        var result = new List<RankingSyncDatagram>();
        string head = Head(PlaysKind, from, station, Count, HashText, null);
        int headBytes = Utf8.GetByteCount(head) + 2; // 閉じの "]}"
        var current = new StringBuilder(head);
        int currentBytes = headBytes, inCurrent = 0;
        for (int i = Math.Max(0, start); i < playJson.Count; i++)
        {
            int itemBytes = playBytes[i];
            // 1回だけでも入らない回は送らない(検査を通った回では起きない大きさ)。
            if (headBytes + itemBytes > MaxDatagramBytes) continue;
            if (inCurrent > 0 && currentBytes + 1 + itemBytes > MaxDatagramBytes)
            {
                result.Add(new RankingSyncDatagram(current.Append("]}").ToString(), inCurrent));
                if (result.Count >= maxDatagrams) { next = i; return result; }
                current = new StringBuilder(head);
                currentBytes = headBytes;
                inCurrent = 0;
            }
            if (inCurrent > 0) { current.Append(','); currentBytes++; }
            current.Append(playJson[i]);
            currentBytes += itemBytes;
            inCurrent++;
        }
        if (inCurrent > 0) result.Add(new RankingSyncDatagram(current.Append("]}").ToString(), inCurrent));
        next = playJson.Count;
        return result;
    }

    // プレイの JSON を並べた束(テストで、壊れた回の混じった束を作るのにも使う)。
    public static List<RankingSyncDatagram> BuildPlays(IEnumerable<string> playJsonItems, string from, string station, int count, string hashText)
    {
        var result = new List<RankingSyncDatagram>();
        string head = Head(PlaysKind, from, station, count, hashText, null);
        int headBytes = Utf8.GetByteCount(head) + 2;
        var current = new StringBuilder(head);
        int currentBytes = headBytes, inCurrent = 0;
        foreach (string item in playJsonItems)
        {
            int itemBytes = Utf8.GetByteCount(item);
            if (headBytes + itemBytes > MaxDatagramBytes) continue;
            if (inCurrent > 0 && currentBytes + 1 + itemBytes > MaxDatagramBytes)
            {
                result.Add(new RankingSyncDatagram(current.Append("]}").ToString(), inCurrent));
                current = new StringBuilder(head);
                currentBytes = headBytes;
                inCurrent = 0;
            }
            if (inCurrent > 0) { current.Append(','); currentBytes++; }
            current.Append(item);
            currentBytes += itemBytes;
            inCurrent++;
        }
        if (inCurrent > 0) result.Add(new RankingSyncDatagram(current.Append("]}").ToString(), inCurrent));
        return result;
    }

    // ---- 受け取った文字列の検査 ----

    // 受信スレッドで使う最初のふるい: 大きさ(1400 バイトまで)・頭の目印・UTF-8 として読めるか。
    public static bool TryDecode(byte[] data, int length, out string text)
    {
        text = null;
        if (data == null || length < Prefix.Length || length > MaxReceiveBytes || length > data.Length) return false;
        for (int i = 0; i < Prefix.Length; i++)
            if (data[i] != (byte)Prefix[i]) return false;
        try { text = StrictUtf8.GetString(data, 0, length); }
        catch (ArgumentException) { return false; } // 壊れた UTF-8(DecoderFallbackException)
        return true;
    }

    // 新しい1回(遊び終わった直後に送る1回)と要約か(全部の記録の束より先に処理する)。受信スレッドで JSON を読まずに数える。
    // 各プレイの JSON は {"id":" で始まる(JsonUtility は欄を宣言の順に書く)。
    public static bool IsFresh(string text)
    {
        const string PlayStart = "{\"id\":\"";
        int first = text.IndexOf(PlayStart, StringComparison.Ordinal);
        return first < 0 || text.IndexOf(PlayStart, first + PlayStart.Length, StringComparison.Ordinal) < 0;
    }

    // 頭の目印・大きさ・JSON として読めるか・版・各欄の形。中のプレイは1回ずつ併合のときに検査する(壊れた回だけ捨てる)。
    public static bool TryParse(string text, out RankingSyncMessage message)
    {
        message = null;
        if (text == null || text.Length > MaxReceiveBytes || !text.StartsWith(Prefix, StringComparison.Ordinal) ||
            Utf8.GetByteCount(text) > MaxReceiveBytes) return false;
        if (!SplitPlays(text.Substring(Prefix.Length), out string envelope, out var items)) return false;
        RankingSyncMessage parsed;
        try { parsed = JsonUtility.FromJson<RankingSyncMessage>(envelope); }
        catch (Exception) { return false; }
        if (parsed == null || parsed.v != Version || (parsed.kind != PlaysKind && parsed.kind != DigestKind) ||
            !IsValidId(parsed.from) || PhoneSaberStation.Normalize(parsed.st) != (parsed.st ?? "") ||
            parsed.count < 0 || !IsHashText(parsed.hash)) return false;
        parsed.plays = new List<RankingPlay>(items.Count);
        foreach (string item in items) parsed.plays.Add(ParsePlay(item));
        if (parsed.heard == null) parsed.heard = new List<string>();
        message = parsed;
        return true;
    }

    // ほかの版の目印("BTSRANK1 " など、数字と空白)で始まるか。version にその版。合流はしない(運営に1回知らせる)。
    public static bool IsOtherVersion(string text, out int version)
    {
        version = 0;
        if (text == null || !text.StartsWith(PrefixStem, StringComparison.Ordinal)) return false;
        int i = PrefixStem.Length, value = 0, digits = 0;
        for (; i < text.Length && digits < 4 && text[i] >= '0' && text[i] <= '9'; i++, digits++) value = value * 10 + (text[i] - '0');
        if (digits == 0 || i >= text.Length || text[i] != ' ' || value == Version) return false;
        version = value;
        return true;
    }

    // 受信スレッドで、届いたバイト列のまま確かめる形。
    public static bool IsOtherVersion(byte[] data, int length, out int version)
    {
        version = 0;
        if (data == null || length > data.Length || length < PrefixStem.Length + 2) return false;
        for (int j = 0; j < PrefixStem.Length; j++)
            if (data[j] != (byte)PrefixStem[j]) return false;
        int i = PrefixStem.Length, value = 0, digits = 0;
        for (; i < length && digits < 4 && data[i] >= '0' && data[i] <= '9'; i++, digits++) value = value * 10 + (data[i] - '0');
        if (digits == 0 || i >= length || data[i] != ' ' || value == Version) return false;
        version = value;
        return true;
    }

    // ---- 1回のプレイの検査 ----

    public static bool IsValid(RankingPlay play) =>
        play != null && IsValidId(play.id) && IsValidSong(play.song) && IsValidDifficulty(play.diff) &&
        play.score >= 0 && play.score <= ScoreManager.MaxScore &&
        !float.IsNaN(play.acc) && play.acc >= 0f && play.acc <= 1f &&
        IsValidRank(play.rank) && IsValidDate(play.date) &&
        PhoneSaberStation.Normalize(play.st) == (play.st ?? "") &&
        play.at > 0 && play.at <= MaxUnixMilliseconds &&
        OperatorMode.IsValidNamespace(play.ns) && IsValidAchievements(play.ach) && EstimatedPlayBytes(play) <= MaxPlayBytes;

    // 実績の ID の並び: 無い(null・空)か、形の正しい ID が重ならずに MaxAchievementsPerPlay 個まで。
    static bool IsValidAchievements(List<string> ids)
    {
        if (ids == null || ids.Count == 0) return true;
        if (ids.Count > MaxAchievementsPerPlay) return false;
        for (int i = 0; i < ids.Count; i++)
        {
            if (!AchievementStore.IsValidId(ids[i])) return false;
            for (int j = 0; j < i; j++)
                if (ids[j] == ids[i]) return false;
        }
        return true;
    }

    // 1回分の JSON の大きさの見積もり(多めに数える。文字列は ASCII 1 バイト・" と \ は 2・それ以外は \uXXXX にされても収まる 6)。
    static int EstimatedPlayBytes(RankingPlay play)
    {
        int bytes = FixedPlayBytes + JsonBytes(play.id) + JsonBytes(play.song) + JsonBytes(play.diff) + JsonBytes(play.rank) +
                    JsonBytes(play.date) + JsonBytes(play.st) + JsonBytes(play.ns);
        if (play.ach != null)
            foreach (string id in play.ach) bytes += JsonBytes(id) + 3; // 引用符2つと区切り
        return bytes;
    }

    static int JsonBytes(string text)
    {
        if (text == null) return 0;
        int bytes = 0;
        foreach (char c in text) bytes += c < ' ' || c >= 0x80 ? 6 : c == '"' || c == '\\' ? 2 : 1;
        return bytes;
    }

    // 同じ回の中身か(同じ id で中身の違う回が届いたら、先のものを残して知らせる)。精度は JSON の往復の誤差を許す。
    public static bool SameContent(RankingPlay a, RankingPlay b) =>
        a != null && b != null && a.id == b.id && a.song == b.song && a.diff == b.diff && a.score == b.score &&
        a.rank == b.rank && Math.Abs(a.acc - b.acc) < 1e-5f && a.date == b.date && (a.st ?? "") == (b.st ?? "") && a.at == b.at &&
        (a.ns ?? "") == (b.ns ?? "") && SameAchievements(a.ach, b.ach);

    static bool SameAchievements(List<string> a, List<string> b)
    {
        int count = a?.Count ?? 0;
        if (count != (b?.Count ?? 0)) return false;
        for (int i = 0; i < count; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    // 回の ID と機械 ID: 8〜64 文字の英数字と - だけ(GUID の "N" 形式は 32 文字の16進)。
    public static bool IsValidId(string id)
    {
        if (id == null || id.Length < MinIdLength || id.Length > MaxIdLength) return false;
        foreach (char c in id)
            if (!(c >= 'A' && c <= 'Z') && !(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '-') return false;
        return true;
    }

    // 曲 ID は日本語・空白も使う(揺籠・Phobia PARTⅠ)。制御文字と、対になっていないサロゲートは通さない。
    static bool IsValidSong(string song)
    {
        if (string.IsNullOrWhiteSpace(song) || song.Length > MaxSongLength) return false;
        for (int i = 0; i < song.Length; i++)
        {
            char c = song[i];
            if (char.IsControl(c) || char.IsLowSurrogate(c)) return false;
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= song.Length || !char.IsLowSurrogate(song[i + 1])) return false;
                i++;
            }
        }
        return true;
    }

    // 難易度は Easy / Normal / Hard と裏譜面の1文字(E・G・R など)。
    static bool IsValidDifficulty(string difficulty)
    {
        if (string.IsNullOrEmpty(difficulty) || difficulty.Length > MaxDifficultyLength) return false;
        foreach (char c in difficulty)
            if (!(c >= 'A' && c <= 'Z') && !(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '-' && c != '_' && c != '+') return false;
        return true;
    }

    static bool IsValidRank(string rank)
    {
        if (string.IsNullOrEmpty(rank) || rank.Length > MaxRankLength) return false;
        foreach (char c in rank)
            if (c <= ' ' || c > '~') return false;
        return true;
    }

    static bool IsValidDate(string date) => date != null && date.Length == 10 &&
        DateTime.TryParseExact(date, "yyyy/MM/dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    static bool IsHashText(string text)
    {
        if (text == null || text.Length != 16) return false;
        foreach (char c in text)
            if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false;
        return true;
    }

    // 64bit FNV-1a。id は検査済みの ASCII だけなので、文字をそのままバイトとして扱う。
    public static ulong IdHash(string id)
    {
        unchecked
        {
            ulong value = 14695981039346656037UL;
            foreach (char c in id)
            {
                value ^= (byte)c;
                value *= 1099511628211UL;
            }
            return value;
        }
    }

    public static string HashToText(ulong value) => value.ToString("x16", CultureInfo.InvariantCulture);

    public static RankingPlay Copy(RankingPlay play) => new RankingPlay
    {
        id = play.id, song = play.song, diff = play.diff, score = play.score, rank = play.rank,
        acc = play.acc, date = play.date, st = play.st ?? "", at = play.at,
        ns = play.ns ?? "", ach = play.ach != null ? new List<string>(play.ach) : new List<string>(),
    };

    // 実績を同期の記録に入れきれなかったことを、起動の間に1回だけ警告したか。
    static bool achievementsDroppedWarned;

    // テスト用: 上の警告をもう一度出せるようにする。
    public static void ResetWarningsForTest() => achievementsDroppedWarned = false;

    // 自分の台で遊び終わった回を作る。日付はカルチャに依らず "yyyy/MM/dd"。ns はその回の名前空間(GameSession.RunNamespace)。
    // 実績は形の正しい ID を重ねずに入れる。形が違う・上限を超える・1回分が束に入る大きさを超える分は入れられないので、
    // 外したことを1回だけ警告する(送れない回を作らない。ふつうの回では起きない大きさ)。
    public static RankingPlay NewPlay(string runId, string song, string difficulty, int score, string rank, float accuracy,
        DateTime localTime, string station, string ns = "", IEnumerable<string> achievements = null)
    {
        var play = new RankingPlay
        {
            id = runId, song = song, diff = difficulty, score = score, rank = rank, acc = accuracy,
            date = localTime.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture),
            st = PhoneSaberStation.Normalize(station),
            at = new DateTimeOffset(localTime).ToUnixTimeMilliseconds(),
            ns = ns ?? "",
        };
        int dropped = 0;
        if (achievements != null)
            foreach (string id in achievements)
            {
                if (id != null && play.ach.Contains(id)) continue;
                if (AchievementStore.IsValidId(id) && play.ach.Count < MaxAchievementsPerPlay) play.ach.Add(id);
                else dropped++;
            }
        while (play.ach.Count > 0 && EstimatedPlayBytes(play) > MaxPlayBytes)
        {
            play.ach.RemoveAt(play.ach.Count - 1);
            dropped++;
        }
        if (dropped > 0 && !achievementsDroppedWarned)
        {
            achievementsDroppedWarned = true;
            Debug.LogWarning("RankingSync: この回の実績のうち " + dropped + " 個を同期の記録に入れられませんでした" +
                             "(ID の形が違う・" + MaxAchievementsPerPlay + " 個を超える・1通に収まらない)。相手の台の数には入りません。");
        }
        return play;
    }

    // この台の時刻での、遊び終わった時刻(通算の表を新しく作るときの「数え始めた日」に使う)。
    public static DateTime LocalTime(RankingPlay play) => DateTimeOffset.FromUnixTimeMilliseconds(play.at).LocalDateTime;

    public static HighScoreEntry ToHighScoreEntry(RankingPlay play) => new HighScoreEntry
    {
        score = play.score, rank = play.rank, accuracy = play.acc, date = play.date, runId = play.id, at = play.at,
    };

    // 同じ表へ書く回をまとめるための鍵(名前空間 × 曲 × 難易度。難易度は表と同じく小文字にそろえる)。
    public static string ChartKey(RankingPlay play) => ChartKey(play.ns, play.song, play.diff);

    public static string ChartKey(string ns, string song, string difficulty)
    {
        string diff = string.IsNullOrWhiteSpace(difficulty) ? "normal" : difficulty.Trim().ToLowerInvariant();
        return (ns ?? "") + "\n" + song + "\n" + diff;
    }

    // ---- 送り先 ----

    // 手で入れた相手(カンマ・セミコロン・空白区切り。「IP」か「IP:ポート」)。IPv4 の4つの数の形だけ。重なりは1つに。
    public static List<IPEndPoint> ParsePeers(string text, int defaultPort)
    {
        var result = new List<IPEndPoint>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (string raw in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string item = raw.Trim();
            int port = defaultPort;
            int colon = item.LastIndexOf(':');
            if (colon > 0)
            {
                if (!int.TryParse(item.Substring(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out port) ||
                    port < 1 || port > IPEndPoint.MaxPort) continue;
                item = item.Substring(0, colon);
            }
            // IPAddress.TryParse は "1" のような省略形も通すので、4つの数の形だけを受ける。
            if (item.Split('.').Length != 4 || !IPAddress.TryParse(item, out var address) ||
                address.AddressFamily != AddressFamily.InterNetwork) continue;
            var endPoint = new IPEndPoint(address, port);
            if (!result.Exists(e => e.Equals(endPoint))) result.Add(endPoint);
        }
        return result;
    }

    public static IPAddress DirectedBroadcast(IPAddress address, IPAddress mask) => DirectedBroadcast(address, mask, out _);

    // ネットワークごとの宛先ブロードキャスト(アドレス | ~マスク)。マスクが分からない(Mac で取れない・0.0.0.0)ときは、
    // 家庭やホットスポットに多い /24 と推測する(guessed = true。呼び出し側が1回だけ知らせる)。
    // ループバック・1対1のつながり(マスク /32、VPN など)・IPv6 は null。
    public static IPAddress DirectedBroadcast(IPAddress address, IPAddress mask, out bool guessed)
    {
        guessed = false;
        if (address == null || address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address) ||
            address.Equals(IPAddress.Any)) return null;
        byte[] a = address.GetAddressBytes();
        byte[] m = mask != null && mask.AddressFamily == AddressFamily.InterNetwork ? mask.GetAddressBytes() : null;
        if (m == null || (m[0] | m[1] | m[2] | m[3]) == 0)
        {
            m = new byte[] { 255, 255, 255, 0 };
            guessed = true;
        }
        if (m[0] == 255 && m[1] == 255 && m[2] == 255 && m[3] == 255) return null;
        var b = new byte[4];
        for (int i = 0; i < 4; i++) b[i] = (byte)(a[i] | (byte)~m[i]);
        return new IPAddress(b);
    }
}
