using System.Collections.Generic;
using UnityEngine;

// 背景ごとの演出を同じ時計で進め、撮影時にも同じ途中状態を再現できるようにする。
public interface ITitlePresentationLayer
{
    void SetPresentationTime(float age, float departure);
}

public sealed class TitlePresentationMotion : MonoBehaviour
{
    readonly List<ITitlePresentationLayer> layers = new List<ITitlePresentationLayer>();
    RectTransform[] words;
    Vector2[] wordPositions;
    TitleConceptAWordmark[] wordmarks;
    CanvasGroup targetGroup;
    TitleStartNote note;

    public float Age { get; private set; }
    public float Departure { get; private set; }
    public bool ManualTime { get; set; }

    public void Configure(Canvas canvas, RectTransform logo, CanvasGroup target, TitleStartNote startNote)
    {
        layers.Clear();
        foreach (var component in canvas.GetComponentsInChildren<MonoBehaviour>())
            if (component is ITitlePresentationLayer layer) layers.Add(layer);

        wordmarks = logo.GetComponentsInChildren<TitleConceptAWordmark>();
        words = new RectTransform[wordmarks.Length];
        wordPositions = new Vector2[wordmarks.Length];
        for (int i = 0; i < wordmarks.Length; i++)
        {
            words[i] = wordmarks[i].rectTransform;
            wordPositions[i] = words[i].anchoredPosition;
        }
        targetGroup = target;
        note = startNote;
        SetPresentationTime(0f, 0f);
    }

    void Update()
    {
        if (!ManualTime) SetPresentationTime(Age + Time.unscaledDeltaTime, Departure);
    }

    public void SetDeparture(float progress)
    {
        SetPresentationTime(Age, progress);
    }

    public void SetPresentationTime(float age, float departure)
    {
        Age = Mathf.Max(0f, age);
        Departure = Mathf.Clamp01(departure);
        foreach (var layer in layers) layer.SetPresentationTime(Age, Departure);

        if (words != null)
        {
            float leaving = Mathf.SmoothStep(0f, 1f, Departure);
            for (int i = 0; i < words.Length; i++)
            {
                float reveal = Mathf.SmoothStep(0f, 1f, (Age - .12f - i * .10f) / .56f);
                float side = i % 2 == 0 ? -1f : 1f;
                float idle = Mathf.Sin(Age * .66f + i * .23f);
                words[i].anchoredPosition = wordPositions[i]
                    + new Vector2(side * 28f * (1f - reveal), 9f * (1f - reveal) + idle * .75f - leaving * 12f);
                words[i].localScale = Vector3.one * (Mathf.Lerp(.985f, 1f, reveal) + idle * .0012f);
                // 半透明だと字形の重なりが濃くなるため、不透明な面の発光量で登場させる。
                float light = reveal * (1f - leaving);
                wordmarks[i].enabled = light > .0001f;
                wordmarks[i].canvasRenderer.SetColor(new Color(light, light, light, 1f));
            }
        }
        // 透明な当たり領域は維持し、登場演出の途中でも開始できる。
        if (targetGroup != null)
            targetGroup.alpha = Mathf.SmoothStep(0f, 1f, (Age - .25f) / .45f) * (1f - Departure);
        if (note != null) note.SetPresentationTime(Age, Departure);
    }
}
