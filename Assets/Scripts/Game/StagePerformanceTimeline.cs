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
    }

    public Section[] sections = Array.Empty<Section>();
    // 選曲画面用の見せ場。音源先頭基準。未指定なら演出区間から安全に選ぶ。
    public double previewStartSeconds = -1;

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
