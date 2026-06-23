using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using System.Collections.Generic;

public class TimelineManager : MonoBehaviour, IPointerDownHandler, IDragHandler
{
    private TimeManager timeManager;
    private ChartManager chartManager;

    [Header("UI要素")]
    public RectTransform timelineRect; 
    public RectTransform seekBarRect;  
    public GameObject noteDotPrefab;
    public Transform noteContainer;

    [Header("表示設定")]
    public float totalBeats = 100f;
    [SerializeField] private float laneHeightDivisor = 10f; 

    private List<GameObject> activeDots = new List<GameObject>();
    private int lastNoteCount = -1;
    private float lastWidth = -1f; 
    private float lastTotalBeats = -1f;

    void Start()
    {
        timeManager = Object.FindFirstObjectByType<TimeManager>();
        chartManager = Object.FindFirstObjectByType<ChartManager>();
        if (timelineRect != null) lastWidth = timelineRect.rect.width;
    }

    void Update()
    {
        if (timeManager == null || chartManager == null) return;

        UpdateSeekBar();

        // 描画更新が必要な条件をチェック
        float currentWidth = (timelineRect != null) ? timelineRect.rect.width : 0;
        int currentNoteCount = (chartManager.chartData != null) ? chartManager.chartData.notes.Count : 0;

        if (currentNoteCount != lastNoteCount || 
            !Mathf.Approximately(currentWidth, lastWidth) || 
            !Mathf.Approximately(totalBeats, lastTotalBeats))
        {
            RefreshTimelineDots();
            lastNoteCount = currentNoteCount;
            lastWidth = currentWidth;
            lastTotalBeats = totalBeats;
        }
    }

    // シークバーの更新
    void UpdateSeekBar()
    {
        if (timelineRect == null || seekBarRect == null || totalBeats <= 0) return;

        float progress = timeManager.a / totalBeats;
        SetRectXByProgress(seekBarRect, progress);
    }

    // 共通の座標計算ロジック
    private void SetRectXByProgress(RectTransform targetRect, float progress)
    {
        progress = Mathf.Clamp01(progress);
        float parentWidth = timelineRect.rect.width;
        float leftEdgeOffset = timelineRect.pivot.x * parentWidth;
        
        float xPos = (progress * parentWidth) - leftEdgeOffset;
        targetRect.anchoredPosition = new Vector2(xPos, targetRect.anchoredPosition.y);
    }

    // タイムラインをクリック・ドラッグした時のシーク
    public void HandleClick(PointerEventData eventData)
    {
        if (timelineRect == null) return;
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(timelineRect, eventData.position, eventData.pressEventCamera, out Vector2 localPoint))
        {
            float width = timelineRect.rect.width;
            float leftEdgeOffset = timelineRect.pivot.x * width;
            
            float normalizedX = (localPoint.x + leftEdgeOffset) / width;
            float pct = Mathf.Clamp01(normalizedX);
            
            timeManager.SetBeat(pct * totalBeats);
        }
    }

    public void OnPointerDown(PointerEventData eventData) => HandleClick(eventData);
    public void OnDrag(PointerEventData eventData) => HandleClick(eventData);

    // ドットの再描画
    public void RefreshTimelineDots()
    {
        if (chartManager == null || chartManager.chartData == null || 
            noteDotPrefab == null || noteContainer == null || timelineRect == null)
        {
            return; 
        }

        // 既存のドットを削除
        foreach (var dot in activeDots) 
        {
            if (dot != null) Destroy(dot);
        }
        activeDots.Clear();

        float parentHeight = timelineRect.rect.height;

        foreach (var note in chartManager.chartData.notes)
        {
            // --- 精度向上のための計算修正 ---
            // TickからBeat(float)に変換。resolutionが0でないことを確認。
            float res = (timeManager != null && timeManager.resolution > 0) ? (float)timeManager.resolution : 4f;
            float noteBeat = (float)note.startTick / res;
            float progress = noteBeat / totalBeats;

            if (progress < 0 || progress > 1) continue;

            GameObject dot = Instantiate(noteDotPrefab, noteContainer);
            dot.SetActive(true);
            RectTransform rect = dot.GetComponent<RectTransform>();

            // アンカーを固定（左端・中央）
            rect.anchorMin = new Vector2(0, 0.5f);
            rect.anchorMax = new Vector2(0, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);

            // シークバーと同じ計算式を適用
            SetRectXByProgress(rect, progress);

            // Y座標（レーン位置）の計算
            float yPos = (note.y - 3.5f) * (parentHeight / laneHeightDivisor);
            rect.anchoredPosition = new Vector2(rect.anchoredPosition.x, yPos);
            
            activeDots.Add(dot);
        }
    }
}