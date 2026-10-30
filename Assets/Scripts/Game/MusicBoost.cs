using UnityEngine;

// 曲の音の上乗せ(2026-10-30 文化祭当日、ユーザー「曲の音が小さすぎて聞こえない」「20dbぐらいあげたら大きくなる？」)。
// 曲は loudness.json で全編 -18 LUFS にそろえて鳴らす(SongLoudness)。AudioSource.volume は 1 より上げられないので、
// 曲を鳴らす AudioSource と同じ GameObject の OnAudioFilterRead で BoostDb だけ掛ける。0dBFS を超える分は、出口の MasterLimiter が抑える。
// 倍率を掛けるだけなので、AudioSource.volume(曲ごとの補正・フェード・サビの下げ)とどちらが先に掛かっても結果は同じ。遅れは足さない。
// ffmpeg で -1dBFS のリミッターに通して測ると、+12 dB で -18 LUFS → -6.0〜-8.9 LUFS(実際に 9〜12 dB 大きい)。
// +20 dB でも -5.1〜-6.8 LUFS で、それより上は押さえ込む量が増えて音がつぶれるだけ。
// 効果音(切る音など)には掛けない(本編の曲・チュートリアルの練習音源・判定調整のクリック・選曲の試聴だけ)。
[DisallowMultipleComponent]
public sealed class MusicBoost : MonoBehaviour
{
    public const float BoostDb = 12f;
    public static readonly float Factor = (float)System.Math.Pow(10, BoostDb / 20);

    // 曲の AudioSource に付ける(付いていればそのまま)。
    public static MusicBoost AttachTo(AudioSource source)
    {
        if (source == null) return null;
        var boost = source.GetComponent<MusicBoost>();
        return boost != null ? boost : source.gameObject.AddComponent<MusicBoost>();
    }

    // オーディオスレッドから呼ばれる。Unity の API は使わない。
    void OnAudioFilterRead(float[] data, int channels)
    {
        float g = Factor;
        for (int i = 0; i < data.Length; i++) data[i] *= g;
    }
}
