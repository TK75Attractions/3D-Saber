using UnityEngine;

// AudioSettings.dspTime を基準に、曲開始からの秒数 SongTime を公開する。
// AudioSource.time はフレームに張り付いて粗いので使わない。
[RequireComponent(typeof(AudioSource))]
public class SongPlayer : MonoBehaviour
{
    private AudioSource source;
    private double startDspTime;
    private bool scheduled;
    public float startDelay = 0.2f;

    public AudioClip Clip
    {
        get { EnsureSource(); return source.clip; }
        set { EnsureSource(); source.clip = value; }
    }

    public bool IsPlaying => scheduled && AudioSettings.dspTime >= startDspTime;

    // 曲開始からの秒数。再生開始前は負の値。
    public double SongTime
    {
        get
        {
            if (!scheduled) return 0.0;
            return AudioSettings.dspTime - startDspTime;
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
        EnsureSource();
        startDspTime = AudioSettings.dspTime + startDelay;
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
    }
}
