using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Saber.ChartEditor
{
    /// <summary>
    /// Unity Editor 内部の AudioUtil を安全に包むプレビュー再生ヘルパー。
    /// Unity の版によるメソッド名差を吸収し、失敗時は例外を外へ出さない。
    /// </summary>
    internal static class SaberChartAudioPreview
    {
        private const BindingFlags StaticFlags =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static bool resolved;
        private static MethodInfo playMethod;
        private static MethodInfo setPositionMethod;
        private static MethodInfo stopAllMethod;
        private static MethodInfo getPositionMethod;
        private static MethodInfo isPlayingMethod;
        private static AudioClip previewClip;
        private static int startSample;
        private static double requestedAt;
        private static bool observedPlayback;
        private static string lastError;

        public static bool IsSupported
        {
            get
            {
                Resolve();
                return playMethod != null && getPositionMethod != null && isPlayingMethod != null;
            }
        }

        public static string LastError => lastError;

        public static bool Play(AudioClip clip, float seconds)
        {
            if (clip == null) return false;
            Resolve();
            if (!IsSupported)
            {
                lastError = "このUnityバージョンでは音源プレビューAPIを見つけられませんでした。";
                return false;
            }

            Stop();
            int sample = Mathf.Clamp(
                Mathf.RoundToInt(Mathf.Max(0f, seconds) * clip.frequency),
                0,
                Mathf.Max(0, clip.samples - 1));

            try
            {
                object[] arguments = BuildArguments(playMethod, clip, sample);
                playMethod.Invoke(null, arguments);

                // 旧APIは再生開始サンプルを受け取らないため、再生直後に位置を合わせる。
                bool playAcceptedSample = playMethod.GetParameters().Any(parameter => parameter.ParameterType == typeof(int));
                if (!playAcceptedSample && setPositionMethod != null)
                    setPositionMethod.Invoke(null, BuildArguments(setPositionMethod, clip, sample));

                previewClip = clip;
                startSample = sample;
                requestedAt = EditorApplication.timeSinceStartup;
                observedPlayback = false;
                lastError = null;
                return true;
            }
            catch (Exception exception)
            {
                lastError = exception.GetBaseException().Message;
                return false;
            }
        }

        public static void Stop()
        {
            previewClip = null;
            observedPlayback = false;
            Resolve();
            if (stopAllMethod == null) return;
            try
            {
                stopAllMethod.Invoke(null, BuildArguments(stopAllMethod, null, 0));
            }
            catch (Exception exception)
            {
                lastError = exception.GetBaseException().Message;
            }
        }

        // 再生ボタンを押した時刻ではなく、音声が実際に読み進めたサンプル位置を返す。
        public static bool TryGetPosition(AudioClip clip, out float seconds)
        {
            seconds = 0f;
            if (clip == null || previewClip != clip || clip.frequency <= 0 || !IsSupported) return false;
            try
            {
                bool playing = (bool)isPlayingMethod.Invoke(null, BuildArguments(isPlayingMethod, clip, 0));
                int sample = (int)getPositionMethod.Invoke(null, BuildArguments(getPositionMethod, clip, 0));
                if (playing && sample >= startSample)
                {
                    observedPlayback = true;
                    seconds = sample / (float)clip.frequency;
                    return true;
                }
                // 開始・シークの要求が音声側に届くまでは、カーソルを先走らせない。
                if (!observedPlayback && EditorApplication.timeSinceStartup - requestedAt < 1.0)
                {
                    seconds = startSample / (float)clip.frequency;
                    return true;
                }
                return false;
            }
            catch (Exception exception)
            {
                lastError = exception.GetBaseException().Message;
                return false;
            }
        }

        private static void Resolve()
        {
            if (resolved) return;
            resolved = true;

            Type audioUtil = typeof(AudioImporter).Assembly.GetType("UnityEditor.AudioUtil");
            if (audioUtil == null) return;

            playMethod = FindMethod(audioUtil, "PlayPreviewClip")
                         ?? FindMethod(audioUtil, "PlayClip");
            setPositionMethod = FindMethod(audioUtil, "SetPreviewClipSamplePosition")
                                ?? FindMethod(audioUtil, "SetClipSamplePosition");
            stopAllMethod = FindMethod(audioUtil, "StopAllPreviewClips")
                            ?? FindMethod(audioUtil, "StopAllClips");
            getPositionMethod = FindMethod(audioUtil, "GetPreviewClipSamplePosition")
                                ?? FindMethod(audioUtil, "GetClipSamplePosition");
            isPlayingMethod = FindMethod(audioUtil, "IsPreviewClipPlaying")
                              ?? FindMethod(audioUtil, "IsClipPlaying");
        }

        private static MethodInfo FindMethod(Type type, string name)
        {
            return type.GetMethods(StaticFlags)
                .Where(method => method.Name == name)
                .OrderByDescending(method => method.GetParameters().Length)
                .FirstOrDefault();
        }

        private static object[] BuildArguments(MethodInfo method, AudioClip clip, int sample)
        {
            ParameterInfo[] parameters = method.GetParameters();
            var arguments = new object[parameters.Length];
            bool sampleAssigned = false;

            for (int i = 0; i < parameters.Length; i++)
            {
                Type type = parameters[i].ParameterType;
                if (type == typeof(AudioClip))
                {
                    arguments[i] = clip;
                }
                else if (type == typeof(int))
                {
                    arguments[i] = sampleAssigned ? 0 : sample;
                    sampleAssigned = true;
                }
                else if (type == typeof(bool))
                {
                    arguments[i] = false;
                }
                else if (type == typeof(float))
                {
                    arguments[i] = 1f;
                }
                else if (parameters[i].HasDefaultValue)
                {
                    arguments[i] = parameters[i].DefaultValue;
                }
                else
                {
                    arguments[i] = type.IsValueType ? Activator.CreateInstance(type) : null;
                }
            }

            return arguments;
        }
    }

    /// <summary>タイムライン描画用の軽量な波形キャッシュ。</summary>
    internal sealed class SaberChartWaveform
    {
        private const int DefaultBins = 2048;

        public AudioClip Clip { get; private set; }
        public float[] Peaks { get; private set; }
        public string Error { get; private set; }

        public void Clear()
        {
            Clip = null;
            Peaks = null;
            Error = null;
        }

        public void Build(AudioClip clip, int binCount = DefaultBins)
        {
            Clear();
            Clip = clip;
            if (clip == null || clip.samples <= 0 || clip.channels <= 0) return;

            int bins = Mathf.Clamp(binCount, 256, 8192);
            Peaks = new float[bins];
            int channels = clip.channels;
            int framesPerChunk = Mathf.Min(32768, clip.samples);
            var buffer = new float[Mathf.Max(1, framesPerChunk * channels)];

            try
            {
                for (int offset = 0; offset < clip.samples; offset += framesPerChunk)
                {
                    int frameCount = Mathf.Min(framesPerChunk, clip.samples - offset);
                    int valueCount = frameCount * channels;
                    if (buffer.Length != valueCount) buffer = new float[valueCount];
                    if (!clip.GetData(buffer, offset))
                    {
                        Error = "音源の圧縮設定により波形を取得できません。";
                        Peaks = null;
                        return;
                    }

                    for (int frame = 0; frame < frameCount; frame++)
                    {
                        float peak = 0f;
                        int baseIndex = frame * channels;
                        for (int channel = 0; channel < channels; channel++)
                            peak = Mathf.Max(peak, Mathf.Abs(buffer[baseIndex + channel]));

                        int absoluteFrame = offset + frame;
                        int bin = Mathf.Min(bins - 1, (int)((long)absoluteFrame * bins / clip.samples));
                        if (peak > Peaks[bin]) Peaks[bin] = peak;
                    }
                }

                // 小さい音も見えるように平方根カーブで表示強度だけ補正する。
                for (int i = 0; i < Peaks.Length; i++) Peaks[i] = Mathf.Sqrt(Peaks[i]);
            }
            catch (Exception exception)
            {
                Error = exception.GetBaseException().Message;
                Peaks = null;
            }
        }

        public float Sample(float normalizedTime)
        {
            if (Peaks == null || Peaks.Length == 0) return 0f;
            float index = Mathf.Clamp01(normalizedTime) * (Peaks.Length - 1);
            int lower = Mathf.FloorToInt(index);
            int upper = Mathf.Min(Peaks.Length - 1, lower + 1);
            return Mathf.Lerp(Peaks[lower], Peaks[upper], index - lower);
        }
    }
}
