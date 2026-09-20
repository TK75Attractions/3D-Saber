using System;
using System.IO;
using UnityEngine;

// 音源先頭を0秒とする、難易度共通の演出区間。音量やノーツ数をサビと誤認しない。
[Serializable]
public sealed class StagePerformanceTimeline
{
    [Serializable]
    public sealed class Section
    {
        public double startSeconds;
        public double endSeconds;
        public float fadeInSeconds = 2;
        public float fadeOutSeconds = 3;
        public float intensity = 1;
        // サビ前の収束と、頭の開放。低強度の助走区間には適用しない。
        public float anticipationSeconds = 2.4f;
        public float impactSeconds = .8f;
    }

    public Section[] sections = Array.Empty<Section>();
    // 選曲画面用の見せ場。音源先頭基準。未指定なら演出区間から安全に選ぶ。
    public double previewStartSeconds = -1;
    // 拍子が一定でない曲は、誤った4拍の小節線を出さない。未指定の既存曲は維持する。
    public bool hideBarLines = false;
    // 提供音源に記載された作者。未提供なら空欄のままにする。
    public string artist = "";

    public struct Presentation
    {
        public float anticipation, hush, impact, opening;
    }

    // 曲時計から直接評価するため、停止・再開で入口イベントを二重発火しない。
    // 従来のEvaluateはそのまま維持し、入口の短いアクセントだけを重ねる。
    public Presentation EvaluatePresentation(double songSeconds)
    {
        var value = new Presentation { opening = Evaluate(songSeconds) };
        if (!Finite(songSeconds) || songSeconds < 0 || sections == null) return value;
        foreach (var s in sections)
        {
            if (s == null || !Finite(s.startSeconds) || !Finite(s.endSeconds) ||
                !Finite(s.intensity) || s.startSeconds < 0 || s.endSeconds <= s.startSeconds || s.intensity < .65f) continue;
            float strength = Mathf.Clamp01(s.intensity);
            double until = s.startSeconds - songSeconds;
            float preparation = Finite(s.anticipationSeconds) ? Mathf.Clamp(s.anticipationSeconds, 0, 8) : 0;
            if (preparation > 0 && until > 0 && until <= preparation)
            {
                value.anticipation = Mathf.Max(value.anticipation,
                    Mathf.SmoothStep(0, 1, 1 - (float)until / preparation) * strength);
                value.hush = Mathf.Max(value.hush, Mathf.Clamp01(1 - (float)until / .22f) * strength);
            }
            double age = -until;
            float duration = Finite(s.impactSeconds) ? Mathf.Clamp(s.impactSeconds, 0, 2) : 0;
            if (duration > 0 && age >= 0 && age < duration && songSeconds < s.endSeconds)
                value.impact = Mathf.Max(value.impact, Mathf.Pow(1 - (float)age / duration, 2) * strength);
        }
        value.opening = Mathf.Max(value.opening, value.impact);
        return value;
    }

    public float Evaluate(double songSeconds)
    {
        if (!Finite(songSeconds) || songSeconds < 0 || sections == null) return 0;
        float result = 0;
        foreach (var section in sections)
        {
            if (section == null || !Finite(section.startSeconds) || !Finite(section.endSeconds) ||
                section.startSeconds < 0 || section.endSeconds <= section.startSeconds ||
                !Finite(section.intensity) || !Finite(section.fadeInSeconds) || !Finite(section.fadeOutSeconds)) continue;
            if (songSeconds < section.startSeconds || songSeconds >= section.endSeconds) continue;
            double length = section.endSeconds - section.startSeconds;
            double fadeIn = Math.Min(length*.5, Math.Max(.05, section.fadeInSeconds));
            double fadeOut = Math.Min(length*.5, Math.Max(.05, section.fadeOutSeconds));
            float enter = Mathf.SmoothStep(0,1,(float)((songSeconds-section.startSeconds)/fadeIn));
            float leave = Mathf.SmoothStep(0,1,(float)((section.endSeconds-songSeconds)/fadeOut));
            result = Mathf.Max(result,Mathf.Min(enter,leave)*Mathf.Clamp01(section.intensity));
        }
        return result;
    }

    // 登録済みの強い区間を一つだけ利用する。音量・ノーツ密度からサビを推測しない。
    // 区間中央の最大16秒で食が入り戻る。曲時計だけから求め、停止・シークで状態を持ち越さない。
    public float EvaluateEclipse(double songSeconds)
    {
        if (!Finite(songSeconds) || songSeconds <= 0 || sections == null) return 0;
        Section selected = null;
        float strongest = 0;
        foreach (var section in sections)
        {
            if (section == null || !Finite(section.startSeconds) || !Finite(section.endSeconds) ||
                section.startSeconds < 0 || section.endSeconds - section.startSeconds < 8 ||
                !Finite(section.intensity) || section.intensity < .65f ||
                !Finite(section.fadeInSeconds) || !Finite(section.fadeOutSeconds)) continue;
            float strength = Mathf.Clamp01(section.intensity);
            if (selected == null || strength > strongest ||
                (strength == strongest && section.startSeconds > selected.startSeconds))
            {
                selected = section;
                strongest = strength;
            }
        }
        if (selected == null) return 0;
        double length = selected.endSeconds - selected.startSeconds;
        double duration = Math.Min(16, length);
        double start = selected.startSeconds + (length - duration) * .5;
        if (songSeconds <= start || songSeconds >= start + duration) return 0;
        double wave = Math.Sin(Math.PI * (songSeconds - start) / duration);
        return Mathf.Clamp01((float)(wave * wave));
    }

    // 灯具の配列は最強の有効区間一つだけで変える。入力・難易度・拍パルスは参照しない。
    // 入口から2秒待ち、3秒で整列し、区間末の3秒で戻す。シーク先の曲時計から直接再現する。
    public float EvaluateLightFormation(double songSeconds)
    {
        if (!Finite(songSeconds) || songSeconds <= 0 || sections == null) return 0;
        Section selected = null;
        float strongest = 0;
        foreach (var section in sections)
        {
            if (section == null || !Finite(section.startSeconds) || !Finite(section.endSeconds) ||
                section.startSeconds < 0 || section.endSeconds - section.startSeconds < 12 ||
                !Finite(section.intensity) || section.intensity < .65f ||
                !Finite(section.fadeInSeconds) || !Finite(section.fadeOutSeconds)) continue;
            float strength = Mathf.Clamp01(section.intensity);
            // 同強度・同開始でも、配列の順序ではなく終了時刻で一意に選ぶ。
            if (selected == null || strength > strongest ||
                (strength == strongest && (section.startSeconds > selected.startSeconds ||
                (section.startSeconds == selected.startSeconds && section.endSeconds > selected.endSeconds))))
            {
                selected = section;
                strongest = strength;
            }
        }
        if (selected == null) return 0;
        double age = songSeconds - selected.startSeconds;
        double remaining = selected.endSeconds - songSeconds;
        if (age <= 2 || remaining <= 0) return 0;
        // doubleのまま割合を制限し、大きな有限時刻もfloatへあふれさせない。
        float enter = Mathf.SmoothStep(0,1,(float)Math.Min(1,(age - 2) / 3));
        float leave = Mathf.SmoothStep(0,1,(float)Math.Min(1,remaining / 3));
        return Mathf.Min(enter,leave);
    }

    // 紫の側廊は長い明示区間一つだけで開く。成功判定や譜面の密度は参照しない。
    // 入口から2秒待ち、4秒で畳み、終端の4秒で戻す。停止・シークでも曲時計から直接再現する。
    public float EvaluateVaultCurtain(double songSeconds)
    {
        if (!Finite(songSeconds) || songSeconds <= 0 || sections == null) return 0;
        Section selected = null;
        float strongest = 0;
        foreach (var section in sections)
        {
            if (section == null || !Finite(section.startSeconds) || !Finite(section.endSeconds) ||
                section.startSeconds < 0 || section.endSeconds - section.startSeconds < 16 ||
                !Finite(section.intensity) || section.intensity < .65f ||
                !Finite(section.fadeInSeconds) || !Finite(section.fadeOutSeconds)) continue;
            float strength = Mathf.Clamp01(section.intensity);
            if (selected == null || strength > strongest ||
                (strength == strongest && (section.startSeconds > selected.startSeconds ||
                (section.startSeconds == selected.startSeconds && section.endSeconds > selected.endSeconds))))
            {
                selected = section;
                strongest = strength;
            }
        }
        if (selected == null) return 0;
        double age = songSeconds - selected.startSeconds;
        double remaining = selected.endSeconds - songSeconds;
        if (age <= 2 || remaining <= 0) return 0;
        float enter = Mathf.SmoothStep(0,1,(float)Math.Min(1,(age - 2) / 4));
        float leave = Mathf.SmoothStep(0,1,(float)Math.Min(1,remaining / 4));
        return Mathf.Min(enter,leave);
    }

    // 長い明示区間の中央14秒を一度だけ通過する。操作成否や音量から区間を推測しない。
    // 範囲外は-1。時計から直接求め、停止・シークでも航路を巻き戻し予約しない。
    public float EvaluateMarinePassAge(double songSeconds)
    {
        if (!Finite(songSeconds) || songSeconds < 0 || sections == null) return -1;
        Section selected = null;
        float strongest = 0;
        foreach (var section in sections)
        {
            if (section == null || !Finite(section.startSeconds) || !Finite(section.endSeconds) ||
                section.startSeconds < 0 || section.endSeconds - section.startSeconds < 18 ||
                !Finite(section.intensity) || section.intensity < .65f ||
                !Finite(section.fadeInSeconds) || !Finite(section.fadeOutSeconds)) continue;
            float strength = Mathf.Clamp01(section.intensity);
            if (selected == null || strength > strongest ||
                (strength == strongest && (section.startSeconds > selected.startSeconds ||
                (section.startSeconds == selected.startSeconds && section.endSeconds > selected.endSeconds))))
            {
                selected = section; strongest = strength;
            }
        }
        if (selected == null) return -1;
        double start = selected.startSeconds + (selected.endSeconds - selected.startSeconds - 14) * .5;
        double end = start + 14;
        // 巨大時刻で14秒の窓が丸め落ちる場合は表示しない。
        if (!Finite(end) || Math.Abs((end - start) - 14) > .000001 || start < selected.startSeconds || end > selected.endSeconds) return -1;
        double age = songSeconds - start;
        // 終端直前のdoubleをfloatへ変えて14秒ちょうどに丸めない。
        return age >= 0 && age < 14 ? Mathf.Min((float)age, 13.999999f) : -1;
    }

    public static StagePerformanceTimeline Load(string songId)
    {
        // stage.jsonがない既存曲は通常演出を維持する。譜面の内容・オフセットは変えない。
        if (string.IsNullOrWhiteSpace(songId) || songId != Path.GetFileName(songId) || songId == "." || songId == "..")
            return new StagePerformanceTimeline();
        string path = Path.Combine(Application.streamingAssetsPath,"Songs",songId,"stage.json");
        try
        {
            return File.Exists(path) ? JsonUtility.FromJson<StagePerformanceTimeline>(File.ReadAllText(path)) ?? new StagePerformanceTimeline()
                : new StagePerformanceTimeline();
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException)
        {
            Debug.LogWarning("背景の演出区間を読み込めません。通常演出で続行します: "+error.Message);
            return new StagePerformanceTimeline();
        }
    }

    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
