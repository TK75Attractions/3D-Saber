using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

public class GridCell : MonoBehaviour, IPointerClickHandler
{
    [Header("座標設定")]
    public int x;
    public int y;

    private Image img;
    private ChartManager chartManager;
    private TimeManager timeManager;

    // 色の設定
    private readonly Color colorTap = Color.white;
    private readonly Color colorFlick = Color.yellow;
    private readonly Color colorLong = Color.red;
    private readonly Color colorDefault = new Color(1, 1, 1, 0.1f);

    void Awake()
    {
        img = GetComponent<Image>();
        chartManager = Object.FindFirstObjectByType<ChartManager>();
        timeManager = Object.FindFirstObjectByType<TimeManager>();
    }

    /// <summary>
    /// 【重要】GridGeneratorから座標を設定するためのメソッド
    /// </summary>
    public void Setup(int xPos, int yPos)
    {
        x = xPos;
        y = yPos;
        gameObject.name = $"Cell_{x}_{y}";
    }

    void Update()
    {
        CheckNotePresence();
    }

    public void CheckNotePresence()
    {
        if (chartManager == null || timeManager == null || chartManager.chartData == null) return;

        var note = chartManager.chartData.notes.Find(n => 
            n.x == x && 
            n.y == y && 
            n.startTick == timeManager.currentTick
        );

        if (note != null)
        {
            Color targetColor = note.type switch
            {
                1 => colorFlick, 
                2 => colorLong,  
                _ => colorTap    
            };

            if (chartManager.selectedNote == note)
            {
                img.color = targetColor * 1.5f; 
            }
            else
            {
                img.color = targetColor;
            }
        }
        else
        {
            img.color = colorDefault;
        }
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        if (chartManager == null || timeManager == null) return;

        if (eventData.button == PointerEventData.InputButton.Left)
        {
            chartManager.AddNote(x, y, timeManager.currentTick);
        }
        else if (eventData.button == PointerEventData.InputButton.Right)
        {
            chartManager.RemoveNote(x, y, timeManager.currentTick);
        }
    }

    public void Flash()
    {
        img.color = Color.cyan;
    }
}