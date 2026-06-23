using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(RawImage))]
public class AudioWaveformRenderer : MonoBehaviour
{
    private RawImage rawImage;
    private TimeManager timeManager;

    [Header("波形の色設定")]
    public Color waveformColor = Color.cyan;
    public Color backgroundColor = new Color(0, 0, 0, 0.5f);

    [Header("解像度（横幅のピクセル数、大きいほど精密）")]
    public int width = 1024;
    public int height = 128;

    void Awake()
    {
        rawImage = GetComponent<RawImage>();
        timeManager = Object.FindFirstObjectByType<TimeManager>();
    }

    void Start()
    {
        if (timeManager != null && timeManager.audioSource != null && timeManager.audioSource.clip != null)
        {
            // 音楽データが既にセットされている場合は描画
            RenderWaveform(timeManager.audioSource.clip);
        }
    }

    /// <summary>
    /// AudioClipから波形データを抽出してRawImageに描画する
    /// </summary>
    public void RenderWaveform(AudioClip clip)
    {
        if (clip == null) return;

        // 1. 新しいテクスチャを作成
        Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        
        // 背景色で塗りつぶし
        Color[] blank = new Color[width * height];
        for (int i = 0; i < blank.Length; i++) blank[i] = backgroundColor;
        texture.SetPixels(blank);

        // 2. オーディオクリップから生データを取得
        // データのサンプル数（全容量）
        int totalSamples = clip.samples * clip.channels;
        float[] audioData = new float[totalSamples];
        clip.GetData(audioData, 0);

        // テクスチャの1ピクセルあたり、どれくらいの音声データ（サンプル）を割り当てるか
        int packSize = totalSamples / width;

        // 3. データを解析してテクスチャに線を引く
        for (int x = 0; x < width; x++)
        {
            // packSize分のデータの中から、最大の音量（絶対値）を探す
            float max = 0;
            for (int i = 0; i < packSize; i++)
            {
                int index = x * packSize + i;
                if (index >= audioData.Length) break;
                
                float absValue = Mathf.Abs(audioData[index]);
                if (absValue > max) max = absValue;
            }

            // 音量（0〜1）をピクセルの高さ（中心から上下）に変換
            int waveHeight = Mathf.RoundToInt(max * (height / 2));
            int centerY = height / 2;

            // 中心から上下にピクセルを塗る
            for (int y = centerY - waveHeight; y <= centerY + waveHeight; y++)
            {
                if (y >= 0 && y < height)
                {
                    texture.SetPixel(x, y, waveformColor);
                }
            }
        }

        // 4. テクスチャの変更を確定させてUIに適用
        texture.Apply();
        rawImage.texture = texture;
    }
}