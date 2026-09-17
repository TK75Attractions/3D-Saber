using UnityEngine;

// 実際に再生されたサンプル位置にDSP時計を合わせ、曲開始からの秒数を公開する。
// 予約時刻だけでは、開始フレームが重かった場合に音声とのずれが曲末まで残る。
[RequireComponent(typeof(AudioSource))]
public class SongPlayer : MonoBehaviour
{
    private AudioSource source;
    private double startDspTime;
    private bool scheduled;
    private bool clockSynchronized;
    public float startDelay = 0.2f;

    public AudioClip Clip
    {
        get { EnsureSource(); return source.clip; }
        set { EnsureSource(); source.clip = value; }
    }

    // 開始予約中も先読み時計は動く。音が鳴っている状態とは区別する。
    public bool IsScheduled => scheduled;
    public bool IsPlaying => scheduled && AudioSettings.dspTime >= startDspTime;

    // 曲開始からの秒数。再生開始前は負の値。
    public double SongTime
    {
        get
        {
            if (!scheduled) return 0.0;
            double now = AudioSettings.dspTime;
            double elapsed = now - startDspTime;
            if (!clockSynchronized && elapsed >= 0.0 && source != null && source.clip != null)
            {
                int sample = source.timeSamples;
                if (sample <= 0) return 0.0; // 音声が始まるまでは先読みの終点で待つ。
                startDspTime = now - sample / (double)source.clip.frequency;
                clockSynchronized = true;
                elapsed = now - startDspTime;
            }
            // 同期後はDSPで連続させ、練習のループや音源終了後の余韻も巻き戻さない。
            return elapsed;
        }
    }

    public double Duration => source != null && source.clip != null ? source.clip.length : 0.0;

    void Awake()
    {
        EnsureSource();
    }

    private void EnsureSource()
    {
        if (source == null) source = GetComponent<AudioSource>();
    }

    public void Play()
    {
        PlayScheduled(AudioSettings.dspTime + startDelay);
    }

    // カウントインなど別演出と同じ DSP 時刻へ、曲開始を正確に揃える。
    public void PlayScheduled(double dspStartTime)
    {
        EnsureSource();
        source.Stop();
        clockSynchronized = false;
        startDspTime = System.Math.Max(dspStartTime, AudioSettings.dspTime + 0.01);
        if (source.clip != null)
        {
            source.PlayScheduled(startDspTime);
        }
        else
        {
            // 音源が無くてもゲームは進行させる（無音プレイ）
            Debug.LogWarning("SongPlayer: clip is null - running silent");
        }
        scheduled = true;
    }

    public void Stop()
    {
        if (source != null) source.Stop();
        scheduled = false;
        clockSynchronized = false;
    }
}
