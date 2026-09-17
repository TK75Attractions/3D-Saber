using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// 背景ごとの演出を同じ時計で進め、撮影時にも同じ途中状態を再現できるようにする。
public interface ITitlePresentationLayer
{
    void SetPresentationTime(float age, float departure);
}

public sealed class TitlePresentationMotion : MonoBehaviour
{
    public const float DepartureDuration = .92f;

    readonly List<ITitlePresentationLayer> layers = new List<ITitlePresentationLayer>();
    RectTransform[] words;
    Vector2[] wordPositions;
    TitleConceptAWordmark[] wordmarks;
    CanvasGroup targetGroup;
    TitleStartNote note;
    RectTransform scenery;
    Vector2 sceneryPosition;
    Vector3 sceneryScale;
    TitleDepartureGraphic departureGraphic;
    Image transitionOverlay;

    public float Age { get; private set; }
    public float Departure { get; private set; }
    public bool ManualTime { get; set; }

    public void Configure(Canvas canvas, RectTransform logo, CanvasGroup target, TitleStartNote startNote,
        RectTransform background = null, Image overlay = null)
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
        scenery = background;
        if (scenery != null)
        {
            sceneryPosition = scenery.anchoredPosition;
            sceneryScale = scenery.localScale;
        }
        transitionOverlay = overlay;
        if (departureGraphic == null)
        {
            var go = new GameObject("TitleDeparture", typeof(RectTransform), typeof(TitleDepartureGraphic));
            go.transform.SetParent(canvas.transform, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            // ロゴ・開始ノーツの目印より後ろに置き、切った後だけ光路を描く。
            go.transform.SetSiblingIndex(logo.GetSiblingIndex());
            departureGraphic = go.GetComponent<TitleDepartureGraphic>();
            departureGraphic.raycastTarget = false;
        }
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

        // 最初の約0.1秒は切断片を見せ、その後に背景だけを消失点の外へ押し出す。
        float flight = Mathf.InverseLerp(.11f, 1f, Departure);
        float acceleration = flight * flight;
        if (scenery != null)
        {
            float scale = 1f + 2.3f * acceleration;
            scenery.localScale = sceneryScale * scale;
            scenery.anchoredPosition = sceneryPosition + new Vector2(0f, -120f) * (1f - scale);
        }
        if (departureGraphic != null) departureGraphic.SetDeparture(Departure);
        if (transitionOverlay != null)
        {
            float dark = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.82f, 1f, Departure));
            float flash = Departure < .24f ? Mathf.Sin(Departure / .24f * Mathf.PI) * .14f : 0f;
            transitionOverlay.color = Color.Lerp(new Color(1f, 1f, 1f, flash),
                new Color(.003f, .007f, .014f, 1f), dark);
        }

        if (words != null)
        {
            float leaving = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.12f, .65f, Departure));
            for (int i = 0; i < words.Length; i++)
            {
                float reveal = Mathf.SmoothStep(0f, 1f, (Age - .12f - i * .10f) / .56f);
                float side = i % 2 == 0 ? -1f : 1f;
                float idle = Mathf.Sin(Age * .66f + i * .23f);
                words[i].anchoredPosition = wordPositions[i]
                    + new Vector2(side * 28f * (1f - reveal), 9f * (1f - reveal) + idle * .75f + leaving * 95f);
                words[i].localScale = Vector3.one * (Mathf.Lerp(.985f, 1f, reveal) + idle * .0012f)
                    * (1f + leaving * .16f);
                // 半透明だと字形の重なりが濃くなるため、不透明な面の発光量で登場させる。
                float light = reveal * (1f - leaving);
                wordmarks[i].enabled = light > .0001f;
                wordmarks[i].canvasRenderer.SetColor(new Color(light, light, light, 1f));
            }
        }
        // 透明な当たり領域は維持し、登場演出の途中でも開始できる。
        if (targetGroup != null)
            targetGroup.alpha = Mathf.SmoothStep(0f, 1f, (Age - .25f) / .45f)
                * (1f - Mathf.SmoothStep(0f, 1f, Departure / .24f));
        if (note != null) note.SetPresentationTime(Age, Departure);
    }
}
