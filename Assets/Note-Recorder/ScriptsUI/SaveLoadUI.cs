using UnityEngine;

public class SaveLoadUI : MonoBehaviour
{
    private ChartManager chartManager;

    void Start()
    {
        chartManager = Object.FindFirstObjectByType<ChartManager>();
    }

    // ボタンから呼び出すための関数
    public void OnSaveButtonClicked()
    {
        if (chartManager != null)
        {
            chartManager.SaveChart();
        }
    }

    public void OnLoadButtonClicked()
    {
        if (chartManager != null)
        {
            chartManager.LoadChart();
        }
    }
}