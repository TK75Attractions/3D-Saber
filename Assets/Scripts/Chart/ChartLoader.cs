using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

public static class ChartLoader
{
    // JsonUtility は JSON 文字列から直接読む。ファイル I/O と分離してテストできるようにする。
    public static ChartData Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ChartData();
        try
        {
            json = json.Trim().TrimStart('\uFEFF');
            ChartData data = JsonUtility.FromJson<ChartData>(json);
            if (data == null) return new ChartData();
            if (data.notes == null) data.notes = new System.Collections.Generic.List<NoteData>();
            var explicitNullNotes = FindNullNoteIndices(json);
            // 同時刻の並びは作者の順番を維持する。壊れた1ノーツで曲全体を失わない。
            data.notes = data.notes.Where((n, index) => !explicitNullNotes.Contains(index)
                    && n != null && Finite(n.time) && Finite(n.x) && Finite(n.y))
                .OrderBy(n => n.time).ToList();
            foreach (var note in data.notes)
            {
                note.type = NormalizeToken(note.type, "tap");
                note.color = NormalizeToken(note.color, "default");
                note.direction = NormalizeToken(note.direction, "none");
                note.count = Math.Max(1, note.count);
                if (!Finite(note.lengthMs) || note.lengthMs < 0) note.lengthMs = 0;
                if (!Finite(note.beat)) note.beat = 0;
                note.veil = Finite(note.veil) ? Math.Max(0f, Math.Min(1f, note.veil)) : 0f;
                // 化ける(C)。無効値は通常表示、正の値は発色後の余裕がある範囲へ収める。
                note.reveal = ChameleonRules.Normalize(note.reveal);
                note.revealArrow = note.reveal > 0f && note.IsDirection && note.revealArrow;
                // 風(W)。画面から大きく外れる値や壊れた値は収める。
                note.windX = Clamp(note.windX, -3f, 3f);
                note.windY = Clamp(note.windY, -2f, 2f);
                note.windSpin = Clamp(note.windSpin, -2f * (float)Math.PI, 2f * (float)Math.PI);
                note.windSway = Clamp(note.windSway, 0f, 1f);
                // 追い越し(P)。省略は無効、奥の時間と出発位置は安全な範囲へ収める。
                note.approachScale = OvertakeRules.NormalizeScale(note.approachScale);
                note.laneX = note.approachScale > 0f
                    ? OvertakeRules.NormalizeLane(note.laneX) : 0f;
                // 消える(V)。知らない名前はふつうのノーツ。
                note.vanish = VanishRules.Name(VanishRules.Parse(note.vanish));
            }
            // 拍ガイドと描画を成立させる最低限の復旧。ノーツ時刻・配置は補正しない。
            if (!Finite(data.bpm) || data.bpm <= 0) data.bpm = 120;
            if (!Finite(data.coordScale) || data.coordScale <= 0) data.coordScale = 1;
            foreach (var note in data.notes) LongSlideRules.Normalize(note, data.coordScale);
            if (!Finite(data.offsetMs)) data.offsetMs = 0;
            if (!Finite(data.beatZeroMs)) data.beatZeroMs = 0;
            data.timeSignatures = ChartMeterMap.Normalize(data.timeSignatures);
            data.aim = NormalizeAim(data.aim);
            data.memory = NormalizeMemory(data.memory);
            data.scroll = NormalizeScroll(data.scroll);
            data.bull = NormalizeBull(data.bull);
            // JsonUtility が省略・null を空オブジェクトにしても、旧譜面を有効にしない。
            data.heat = HasHeatObject(json)
                ? HeatRules.Normalize(data.heat ?? new HeatChartData()) : null;
            data.fall = FallRules.Normalize(data.fall);
            // 勉強の裏譜面 Q の試験用紙と問題。壊れた値は捨て、時刻順に並べる(ふつうの譜面では空のまま)。
            data.quiz = QuizRules.Normalize(data.quiz);
            return data;
        }
        catch (ArgumentException exception)
        {
            Debug.LogWarning("ChartLoader: 譜面を読み込めません。" + exception.Message);
            return new ChartData();
        }
    }

    // ノーツ内の heat:true や文字列中の heat を、譜面全体の設定と取り違えない。
    static bool HasHeatObject(string json)
    {
        int depth = 0;
        bool found = false;
        for (int i = 0; i < json.Length; i++)
        {
            char token = json[i];
            if (token == '"')
            {
                int end = StringEnd(json, i);
                if (depth == 1 && end - i == 5
                    && string.CompareOrdinal(json, i + 1, "heat", 0, 4) == 0)
                {
                    int colon = SkipWhiteSpace(json, end + 1);
                    if (colon < json.Length && json[colon] == ':')
                    {
                        int value = SkipWhiteSpace(json, colon + 1);
                        found = value < json.Length && json[value] == '{';
                    }
                }
                i = end;
            }
            else if (token == '{' || token == '[') depth++;
            else if (token == '}' || token == ']') depth--;
        }
        return found;
    }

    static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    // 壊れた値は 0、範囲の外は端へ。
    static float Clamp(float value, float min, float max) => Finite(value) ? Math.Max(min, Math.Min(max, value)) : 0f;

    // 照準の裏譜面(避)の的とトゲ。壊れた値は捨て、時刻順に並べ、色の名前を小文字にそろえる。
    static AimChartData NormalizeAim(AimChartData aim)
    {
        aim ??= new AimChartData();
        aim.targets = (aim.targets ?? new List<AimTargetData>())
            .Where(t => t != null && Finite(t.time) && Finite(t.x) && Finite(t.y)).OrderBy(t => t.time).ToList();
        foreach (var target in aim.targets) target.color = NormalizeToken(target.color, "red");
        aim.spikes = (aim.spikes ?? new List<AimSpikeData>())
            .Where(s => s != null && Finite(s.time) && Finite(s.x) && Finite(s.y)).OrderBy(s => s.time).ToList();
        foreach (var spike in aim.spikes)
        {
            if (!Finite(spike.size) || spike.size <= 0) spike.size = 1;
            spike.follow = string.IsNullOrWhiteSpace(spike.follow) ? "" : spike.follow.Trim().ToLowerInvariant();
        }
        return aim;
    }

    // 覚える裏譜面(M)のノイズと説明。壊れた値・長さの無い区間を捨て、強さを 0〜1 に収め、時刻順に並べる。
    static MemoryChartData NormalizeMemory(MemoryChartData memory)
    {
        memory ??= new MemoryChartData();
        memory.veils = (memory.veils ?? new List<MemoryVeilData>())
            .Where(v => v != null && Finite(v.time) && Finite(v.endTime) && v.endTime > v.time).OrderBy(v => v.time).ToList();
        foreach (var veil in memory.veils) veil.strength = Finite(veil.strength) ? Math.Max(0f, Math.Min(1f, veil.strength)) : 0f;
        memory.captions = (memory.captions ?? new List<MemoryCaptionData>())
            .Where(c => c != null && Finite(c.time) && Finite(c.endTime) && c.endTime > c.time && !string.IsNullOrWhiteSpace(c.text))
            .OrderBy(c => c.time).ToList();
        return memory;
    }

    // 闘牛(T)の突進と弾。壊れた値・長さの無い突進を捨て、向きは ±1、高さと位置は画面の中に収め、時刻順に並べる。
    static BullChartData NormalizeBull(BullChartData bull)
    {
        bull ??= new BullChartData();
        bull.charges = (bull.charges ?? new List<BullChargeData>())
            .Where(c => c != null && Finite(c.time) && Finite(c.endTime) && c.endTime > c.time && Finite(c.y)).OrderBy(c => c.time).ToList();
        foreach (var charge in bull.charges)
        {
            charge.dir = charge.dir < 0 ? -1 : 1;
            charge.y = Clamp(charge.y, -1.6f, 1.6f);
            charge.x = Clamp(charge.x, -2f, 2f);
            charge.angle = Clamp(charge.angle, -180f, 180f);
            charge.warn = Finite(charge.warn) ? Math.Max(0f, charge.warn) : 0f;
        }
        bull.shots = (bull.shots ?? new List<BullShotData>())
            .Where(s => s != null && Finite(s.time) && Finite(s.x) && Finite(s.y)).OrderBy(s => s.time).ToList();
        foreach (var shot in bull.shots)
        {
            shot.x = Clamp(shot.x, -2.4f, 2.4f);
            shot.y = Clamp(shot.y, -1.5f, 1.5f);
            shot.kind = BullRules.ShotKind(shot.kind);
            shot.fromX = Clamp(shot.fromX, -3.4f, 3.4f);
            shot.fromY = Clamp(shot.fromY, -1.8f, 1.8f);
            shot.lead = Finite(shot.lead) ? Math.Max(0f, shot.lead) : 0f;
            shot.angle = Finite(shot.angle) ? shot.angle : 0f;
            shot.stay = Finite(shot.stay) ? Clamp(shot.stay, 0f, 2000f) : 700f;
            shot.blast = Finite(shot.blast) ? Clamp(shot.blast, .3f, 1.4f) : .9f;
            shot.amp = Finite(shot.amp) ? Clamp(shot.amp, 0f, 1.2f) : .6f;
            shot.follow = string.IsNullOrWhiteSpace(shot.follow) ? "" : shot.follow.Trim().ToLowerInvariant();
            // 追う弾は曲がらないふつうの玉だけ。
            if (shot.kind != "" || shot.lead > 0) shot.follow = "";
        }
        return bull;
    }

    // 緩急(S)の速さの変わり目。壊れた時刻は捨て、速さは 0〜ScrollSpeedMap.MaxSpeed、かける時間は 0 以上にして時刻順に並べる。
    static List<ScrollSpeedData> NormalizeScroll(List<ScrollSpeedData> scroll)
    {
        var result = (scroll ?? new List<ScrollSpeedData>()).Where(s => s != null && Finite(s.time)).OrderBy(s => s.time).ToList();
        foreach (var point in result)
        {
            point.speed = Finite(point.speed) ? Math.Max(0f, Math.Min((float)ScrollSpeedMap.MaxSpeed, point.speed)) : 1f;
            point.ramp = Finite(point.ramp) ? Math.Max(0f, point.ramp) : 0f;
            point.ease = Finite(point.ease) && point.ease > 0f
                ? Math.Max((float)ScrollSpeedMap.MinEase, Math.Min((float)ScrollSpeedMap.MaxEase, point.ease)) : 1f;
        }
        return result;
    }

    static string NormalizeToken(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();

    // JsonUtilityは配列内のnullを既定値オブジェクトに変換する。
    // 0秒・中央の正規ノーツと区別するため、検証済みJSONの明示null位置だけ読む。
    static HashSet<int> FindNullNoteIndices(string json)
    {
        var result = new HashSet<int>();
        if (json.IndexOf("null", StringComparison.Ordinal) < 0) return result;
        int depth = 0;
        for (int i = 0; i < json.Length; i++)
        {
            char token = json[i];
            if (token == '"')
            {
                int end = StringEnd(json, i);
                if (depth == 1 && end - i == 6 && string.CompareOrdinal(json, i + 1, "notes", 0, 5) == 0)
                {
                    int value = SkipWhiteSpace(json, end + 1);
                    if (value < json.Length && json[value] == ':')
                    {
                        value = SkipWhiteSpace(json, value + 1);
                        result.Clear();
                        if (value < json.Length && json[value] == '[') ReadNullElements(json, value, result);
                    }
                }
                i = end;
            }
            else if (token == '{' || token == '[') depth++;
            else if (token == '}' || token == ']') depth--;
        }
        return result;
    }

    static void ReadNullElements(string json, int start, HashSet<int> result)
    {
        int depth = 0, index = 0;
        bool atElementStart = true;
        for (int i = start + 1; i < json.Length; i++)
        {
            char token = json[i];
            if (char.IsWhiteSpace(token)) continue;
            if (depth == 0 && token == ']') return;
            if (depth == 0 && token == ',') { index++; atElementStart = true; continue; }
            if (depth == 0 && atElementStart)
            {
                if (token == 'n' && i + 4 <= json.Length && string.CompareOrdinal(json, i, "null", 0, 4) == 0)
                    result.Add(index);
                atElementStart = false;
            }
            if (token == '"') i = StringEnd(json, i);
            else if (token == '{' || token == '[') depth++;
            else if (token == '}' || token == ']') depth--;
        }
    }

    static int StringEnd(string json, int start)
    {
        for (int i = start + 1; i < json.Length; i++)
        {
            if (json[i] == '\\') { i++; continue; }
            if (json[i] == '"') return i;
        }
        return json.Length - 1;
    }

    static int SkipWhiteSpace(string json, int start)
    {
        while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
        return start;
    }

    public static ChartData LoadFromFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Debug.LogWarning($"Chart not found: {filePath}");
            return new ChartData();
        }
        try { return Parse(File.ReadAllText(filePath)); }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            Debug.LogWarning($"ChartLoader: 譜面ファイルを開けません: {filePath} ({exception.GetType().Name})");
            return new ChartData();
        }
    }

    // StreamingAssets/Songs/<songId>/chart.json
    public static ChartData LoadFromStreamingAssets(string songId)
    {
        return LoadFromStreamingAssets(songId, null);
    }

    // 難易度別ロード：chart_<difficulty>.json を優先、無ければ legacy chart.json にフォールバック。
    // difficulty 例：Easy / Normal / Hard（大文字小文字無視）
    public static ChartData LoadFromStreamingAssets(string songId, string difficulty)
    {
        if (!ValidPathSegment(songId)) return new ChartData();
        string dir = Path.Combine(Application.streamingAssetsPath, "Songs", songId);
        if (!string.IsNullOrWhiteSpace(difficulty) && ValidPathSegment(difficulty.Trim()))
        {
            string specific = Path.Combine(dir, $"chart_{difficulty.Trim().ToLowerInvariant()}.json");
            if (File.Exists(specific)) return LoadFromFile(specific);
        }
        return LoadFromFile(Path.Combine(dir, "chart.json"));
    }

    public static bool ValidPathSegment(string value) => !string.IsNullOrWhiteSpace(value)
        && value != "." && value != ".." && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && value.IndexOf('/') < 0 && value.IndexOf('\\') < 0;
}
