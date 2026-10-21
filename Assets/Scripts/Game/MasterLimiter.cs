using UnityEngine;
using UnityEngine.SceneManagement;

// 音の出口の軽いリミッター(仕様面の点検 11、2026-10-21)。曲と切る音が重なって 0dBFS を超え、割れるのを防ぐ。
// 各シーンの AudioListener に実行時に付ける(シーンは編集しない)。AudioListener と同じ GameObject の
// OnAudioFilterRead は、すべての音を混ぜたあとの出力に掛かる。
// 先読みはしない(遅れを足さない=判定や遅れの計測は変わらない)。閾値を超えるサンプルではその場で下げ、ReleaseSeconds で戻す。
// 閾値(-1dBFS)を超えない普段の音は 1 倍のまま(サンプルの値は変わらない)。
public sealed class MasterLimiter : MonoBehaviour
{
    public const float Threshold = .891f; // -1 dBFS
    public const float ReleaseSeconds = .15f;
    // 倍率は double で持つ(float だと戻る途中の小さな増分が丸めで消え、0.9998 倍のまま止まる)。
    double gain = 1;
    double release;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Initialize()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => AttachToListeners();

    public static void AttachToListeners()
    {
        foreach (var listener in Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None))
            if (listener.GetComponent<MasterLimiter>() == null) listener.gameObject.AddComponent<MasterLimiter>();
    }

    // Unity の API はオーディオスレッドから呼べないので、出力の周波数はここで読んでおく。
    void Awake()
    {
        release = ReleaseCoefficient(AudioSettings.outputSampleRate);
        AudioSettings.OnAudioConfigurationChanged += ConfigurationChanged;
    }

    void OnDestroy() { AudioSettings.OnAudioConfigurationChanged -= ConfigurationChanged; }

    void ConfigurationChanged(bool deviceWasChanged) { release = ReleaseCoefficient(AudioSettings.outputSampleRate); }

    void OnAudioFilterRead(float[] data, int channels) { gain = Process(data, channels, gain, release); }

    // 1 サンプルごとに戻る割合(時定数 ReleaseSeconds)。周波数が分からないときは 48kHz とみなす。
    public static double ReleaseCoefficient(int sampleRate) =>
        1.0 - System.Math.Exp(-1.0 / (ReleaseSeconds * (sampleRate > 0 ? sampleRate : 48000)));

    // data(チャンネルが交互に並ぶ)を書き換え、次の呼び出しへ持ち越す倍率を返す。全チャンネルに同じ倍率を掛け、左右の位置を崩さない。
    // そのフレームの最大値が閾値を超えるなら、倍率は必ず Threshold / 最大値 以下になる(出力は閾値を超えない)。純関数。
    // 壊れたサンプル(NaN・無限大)は 0 にして、倍率を汚さない。
    public static double Process(float[] data, int channels, double gain, double release)
    {
        if (data == null || channels <= 0) return gain;
        if (!(gain > 0) || gain > 1) gain = 1;
        for (int i = 0; i + channels <= data.Length; i += channels)
        {
            float peak = 0f;
            for (int c = 0; c < channels; c++)
            {
                float s = data[i + c];
                if (float.IsNaN(s) || float.IsInfinity(s)) { data[i + c] = 0f; continue; }
                float v = s < 0f ? -s : s;
                if (v > peak) peak = v;
            }
            double limit = peak > Threshold ? Threshold / (double)peak : 1;
            // 戻りきる手前で 1 にそろえる(1 倍のときはサンプルに何も掛けない)。
            double recovered = gain + (1 - gain) * release;
            gain = System.Math.Min(limit, recovered > .99999 ? 1 : recovered);
            if (gain >= 1) continue;
            float g = (float)gain;
            for (int c = 0; c < channels; c++) data[i + c] *= g;
        }
        return gain;
    }
}
