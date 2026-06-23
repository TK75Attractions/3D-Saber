using UnityEngine;

public class TimeManager : MonoBehaviour
{
    public AudioSource audioSource;
    public float bpm = 120f;
    public int resolution = 4;
    public int beatsPerMeasure = 4; // 1小節内の拍数
    public float offsetSeconds = 0f;

    public bool isPlaying = false;
    public float a = 0f; // 現在の拍数

    public int currentTick => Mathf.RoundToInt(a * resolution);

    void Start()
    {
        if (audioSource == null) audioSource = GetComponent<AudioSource>();
        isPlaying = false;
        if (audioSource != null) audioSource.playOnAwake = false;
    }

    void Update()
    {
        if (!isPlaying) return;

        if (audioSource != null && audioSource.clip != null)
        {
            float musicTime = audioSource.time - offsetSeconds;
            a = musicTime * (bpm / 60f);
        }
    }

    public void TogglePlay()
    {
        if (audioSource == null || audioSource.clip == null) return;
        isPlaying = !isPlaying;

        if (isPlaying)
        {
            float startTime = a * (60f / bpm) + offsetSeconds;
            audioSource.time = Mathf.Clamp(startTime, 0, audioSource.clip.length - 0.01f);
            audioSource.Play();
        }
        else
        {
            audioSource.Pause();
        }
    }

    public void SetBeat(float newBeat)
    {
        a = Mathf.Max(0, newBeat);
        if (isPlaying && audioSource != null && audioSource.clip != null)
        {
            float newTime = a * (60f / bpm) + offsetSeconds;
            if (newTime >= 0 && newTime < audioSource.clip.length)
                audioSource.time = newTime;
        }
    }

    // 「小節 : 拍 : ティック」の文字列を返す
    public string GetFormattedTime()
    {
        int totalTicks = currentTick;
        int ticksPerMeasure = beatsPerMeasure * resolution;

        int measure = (totalTicks / ticksPerMeasure) + 1;
        int beat = ((totalTicks % ticksPerMeasure) / resolution) + 1;
        int tick = totalTicks % resolution;

        return $"{measure:D2} : {beat} : {tick}";
    }
}