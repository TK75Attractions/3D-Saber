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

    // 本編形式(chart.json)で StreamingAssets へ書き出す。ボタンの OnClick に割り当てる。
    public void OnExportButtonClicked()
    {
        if (chartManager != null)
        {
            chartManager.ExportToGame();
        }
    }
}