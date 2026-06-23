using UnityEngine;

[DefaultExecutionOrder(-100)] // 他のスクリプトより確実に早く動かす
public class GridGenerator : MonoBehaviour
{
    [Header("グリッド設定")]
    public int columns = 8; // 列数 (X)
    public int rows = 8;    // 行数 (Y)

    void Awake()
    {
        GenerateOrSetupGrid();
    }

    [ContextMenu("座標を再設定する")] // インスペクターから手動で実行可能にする
    public void GenerateOrSetupGrid()
    {
        // 1. 子要素の GridCell をすべて取得
        GridCell[] cells = GetComponentsInChildren<GridCell>();

        if (cells.Length != columns * rows)
        {
            Debug.LogWarning($"マスの数({cells.Length})が設定({columns}x{rows})と一致しません。");
        }

        // 2. 座標を計算して割り振る
        for (int i = 0; i < cells.Length; i++)
        {
            int x = i % columns;
            int y = i / columns;

            cells[i].Setup(x, y);
            
            // エディタ上でも分かりやすいように、オブジェクト名も変えておくと便利
            cells[i].gameObject.name = $"Cell_{x}_{y}";
        }
        
        Debug.Log($"{cells.Length}個のマスに座標({columns}x{rows})を自動設定しました！");
    }
}